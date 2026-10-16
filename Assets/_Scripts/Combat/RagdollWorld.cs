using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.Netcode
{
    /// <summary>
    /// The one physics scene every <see cref="ActiveRagdoll"/> on this client simulates in, and the budget that decides
    /// which of them simulate each frame.
    ///
    /// All ragdolls share a scene so they're stepped together once a frame, rather than one scene step per ragdoll,
    /// and PhysX can solve them in parallel. Ragdolls don't touch each other: their parts are on the Ragdoll layer, which
    /// doesn't collide with itself, and each ragdoll ignores the other ragdolls' ground planes.
    ///
    /// Each frame, the ragdolls that want to simulate (just hit, carried, or still recovering) are ranked: the local
    /// player first, then by distance to the camera, favoring ones already simulating. Those too far away or off
    /// screen are culled, and only the closest <see cref="MaxActiveRagdolls"/> simulate. The rest blend back to their
    /// animation, so the cost has a ceiling however many players are hit at once.
    ///
    /// Created on first use. Put one in a scene to change its settings.
    /// </summary>
    public class RagdollWorld : MonoBehaviour
    {
        [Header("Budget")]
        [Tooltip("Most ragdolls simulating at once. The local player's ragdoll always simulates, on top of these.")]
        public int MaxActiveRagdolls = 8;
        [Tooltip("Ragdolls farther than this from the camera don't simulate.")]
        public float MaxDistance = 40f;
        [Tooltip("Ragdolls outside the camera's view don't simulate.")]
        public bool CullOffscreen = true;
        [Tooltip("Half the size of the box around a ragdoll's hips that must be in view.")]
        public float OffscreenMargin = 1.2f;
        [Tooltip("A ragdoll already simulating ranks as if this many meters closer, so ragdolls at similar distances don't swap slots back and forth.")]
        public float KeepActiveBonus = 5f;
        [Tooltip("Seconds a ragdoll that loses its slot takes to blend back to its animation.")]
        public float CullBlendTime = 0.2f;

        [Header("Simulation")]
        [Tooltip("Longest physics step. Longer frames are split into several steps.")]
        public float MaxStepTime = 1f / 90f;
        [Tooltip("Most steps per frame. A long frame beyond MaxStepTime x this is simulated in fewer, longer steps.")]
        public int MaxSubsteps = 4;
        [Tooltip("Layer for ragdoll body parts. It must not collide with itself (Project Settings > Physics).")]
        public string PartLayerName = "Ragdoll";

        const string k_SceneName = "ActiveRagdoll_Shared";
        static RagdollWorld s_Instance;
        static NetworkSceneManager s_FilteredSceneManager;

        Scene m_Scene;
        PhysicsScene m_PhysicsScene;
        int m_PartLayer = -1;
        bool m_LayerChecked;
        readonly List<ActiveRagdoll> m_Ragdolls = new();
        readonly List<ActiveRagdoll> m_Candidates = new();
        readonly List<float> m_Scores = new();
        readonly List<ActiveRagdoll> m_Stepping = new();
        readonly Plane[] m_FrustumPlanes = new Plane[6];

        /// <summary>How many ragdolls simulated last frame.</summary>
        public int SimulatingCount => m_Stepping.Count;
        /// <summary>How many ragdolls wanted to simulate last frame, before culling and the budget.</summary>
        public int WantingCount { get; private set; }
        public int RegisteredCount => m_Ragdolls.Count;

        public static RagdollWorld Instance
        {
            get
            {
                if (s_Instance != null) return s_Instance;
                s_Instance = FindAnyObjectByType<RagdollWorld>();
                if (s_Instance == null)
                {
                    var go = new GameObject(nameof(RagdollWorld));
                    DontDestroyOnLoad(go);
                    s_Instance = go.AddComponent<RagdollWorld>();
                }
                return s_Instance;
            }
        }

        /// <summary>The layer for body parts, or -1 when it's missing or collides with itself.</summary>
        internal int PartLayer
        {
            get
            {
                if (m_LayerChecked) return m_PartLayer;
                m_LayerChecked = true;
                int layer = LayerMask.NameToLayer(PartLayerName);
                if (layer < 0)
                    Debug.LogWarning($"[{nameof(RagdollWorld)}] No '{PartLayerName}' layer. Ragdolls work without it but take longer to set up per player; add the layer and turn off its collision with itself.");
                else if (!UnityEngine.Physics.GetIgnoreLayerCollision(layer, layer))
                    Debug.LogWarning($"[{nameof(RagdollWorld)}] The '{PartLayerName}' layer collides with itself; turn that off in Project Settings > Physics. Ignoring part collisions pair by pair instead.");
                else
                    m_PartLayer = layer;
                return m_PartLayer;
            }
        }

        /// <summary>The shared ragdoll scene, created on first use.</summary>
        internal Scene Scene
        {
            get
            {
                if (!m_Scene.IsValid())
                {
                    ExcludeRagdollSceneFromSync();
                    m_Scene = SceneManager.CreateScene($"{k_SceneName}_{GetEntityId()}", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                    m_PhysicsScene = m_Scene.GetPhysicsScene();
                }
                return m_Scene;
            }
        }

        /// <summary>Adds a built ragdoll, so it's stepped and kept from touching the other ragdolls.</summary>
        internal void Register(ActiveRagdoll ragdoll)
        {
            if (m_Ragdolls.Contains(ragdoll)) return;
            ExcludeRagdollSceneFromSync(); // the network session may have started since the scene was made
            bool byLayer = PartLayer >= 0;
            if (!byLayer) IgnoreAll(ragdoll.PartColliders, ragdoll.PartColliders);
            foreach (var other in m_Ragdolls)
            {
                // Each ragdoll only stands on its own ground plane.
                IgnoreAll(ragdoll.PartColliders, other.GroundCollider);
                IgnoreAll(other.PartColliders, ragdoll.GroundCollider);
                if (!byLayer) IgnoreAll(ragdoll.PartColliders, other.PartColliders);
            }
            m_Ragdolls.Add(ragdoll);
        }

        internal void Unregister(ActiveRagdoll ragdoll) => m_Ragdolls.Remove(ragdoll);

        internal static void UnregisterIfAlive(ActiveRagdoll ragdoll)
        {
            if (s_Instance != null) s_Instance.Unregister(ragdoll);
        }

        static void IgnoreAll(IReadOnlyList<Collider> a, IReadOnlyList<Collider> b)
        {
            for (int i = 0; i < a.Count; i++)
                for (int j = ReferenceEquals(a, b) ? i + 1 : 0; j < b.Count; j++)
                    UnityEngine.Physics.IgnoreCollision(a[i], b[j]);
        }

        static void IgnoreAll(IReadOnlyList<Collider> a, Collider b)
        {
            for (int i = 0; i < a.Count; i++) UnityEngine.Physics.IgnoreCollision(a[i], b);
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || m_Ragdolls.Count == 0) return;

            // 1. Each ragdoll reads its animated pose and says whether it wants to simulate.
            m_Candidates.Clear();
            m_Scores.Clear();
            var camera = Camera.main;
            if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, m_FrustumPlanes);
            foreach (var ragdoll in m_Ragdolls)
            {
                ragdoll.Granted = false;
                if (!ragdoll.isActiveAndEnabled || !ragdoll.Prepare()) continue;
                float score = Score(ragdoll, camera);
                if (float.IsPositiveInfinity(score)) continue; // culled
                m_Candidates.Add(ragdoll);
                m_Scores.Add(score);
            }
            WantingCount = m_Candidates.Count;

            // 2. The best-ranked get a slot.
            int slots = Mathf.Max(0, MaxActiveRagdolls);
            for (int granted = 0; granted < m_Candidates.Count; granted++)
            {
                int best = -1;
                for (int i = 0; i < m_Candidates.Count; i++)
                    if (!m_Candidates[i].Granted && (best < 0 || m_Scores[i] < m_Scores[best])) best = i;
                // The local player (negative score) doesn't use up the budget.
                if (m_Scores[best] >= 0f && slots-- <= 0) break;
                m_Candidates[best].Granted = true;
            }

            // 3. Set up this frame's simulation; ragdolls without a slot blend back to animation.
            m_Stepping.Clear();
            foreach (var ragdoll in m_Ragdolls)
            {
                if (ragdoll.isActiveAndEnabled && ragdoll.BeginFrame(ragdoll.Granted, dt, CullBlendTime))
                    m_Stepping.Add(ragdoll);
            }

            // 4. Step them all together.
            if (m_Stepping.Count > 0 && m_PhysicsScene.IsValid())
            {
                int steps = Mathf.Clamp(Mathf.CeilToInt(dt / Mathf.Max(0.001f, MaxStepTime)), 1, Mathf.Max(1, MaxSubsteps));
                float step = dt / steps;
                for (int i = 0; i < steps; i++)
                {
                    foreach (var ragdoll in m_Stepping) ragdoll.Substep(i, steps);
                    m_PhysicsScene.Simulate(step);
                }
            }

            // 5. Write the results onto the skeletons.
            foreach (var ragdoll in m_Ragdolls)
                if (ragdoll.isActiveAndEnabled) ragdoll.EndFrame();
        }

        /// <summary>Lower ranks first. The local player is always first; culled ragdolls are infinite.</summary>
        float Score(ActiveRagdoll ragdoll, Camera camera)
        {
            if (ragdoll.IsLocalPlayer) return -1f;
            if (camera == null) return 0f;

            var focus = ragdoll.FocusPoint;
            float distance = Vector3.Distance(camera.transform.position, focus);
            if (distance > MaxDistance) return float.PositiveInfinity;
            if (CullOffscreen && !GeometryUtility.TestPlanesAABB(m_FrustumPlanes, new Bounds(focus, Vector3.one * (OffscreenMargin * 2f))))
                return float.PositiveInfinity;
            return Mathf.Max(0f, distance - (ragdoll.IsSimulating ? KeepActiveBonus : 0f));
        }

        /// <summary>
        /// NGO sends every loaded scene to a joining client, and a scene made at runtime isn't in the build settings, so
        /// the client fails to load it. Rejecting the ragdoll scene in VerifySceneBeforeLoading leaves it out.
        /// </summary>
        static void ExcludeRagdollSceneFromSync()
        {
            var networkManager = NetworkManager.Singleton;
            var sceneManager = networkManager != null ? networkManager.SceneManager : null;
            if (sceneManager == null || sceneManager == s_FilteredSceneManager) return;
            s_FilteredSceneManager = sceneManager;

            var previous = sceneManager.VerifySceneBeforeLoading;
            // Rejected scenes log a warning on every join. With no other handler, only the ragdoll scene is rejected.
            if (previous == null) sceneManager.DisableValidationWarnings(true);
            sceneManager.VerifySceneBeforeLoading = (sceneIndex, sceneName, loadSceneMode) =>
                !sceneName.StartsWith(k_SceneName) && (previous == null || previous(sceneIndex, sceneName, loadSceneMode));
        }

        void OnDestroy()
        {
            if (m_Scene.IsValid() && m_Scene.isLoaded) SceneManager.UnloadSceneAsync(m_Scene);
            if (s_Instance == this) s_Instance = null;
        }
    }
}
