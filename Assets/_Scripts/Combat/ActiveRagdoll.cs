using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unity.Netcode
{
    /// <summary>
    /// Physics hit reactions for a player with any skinned model, humanoid or not; <see cref="RagdollBuilder"/> works
    /// out the body parts and their colliders from the model. A ragdoll of the main bones, joined by ConfigurableJoints, follows the
    /// animated pose: joint drives (muscles) bend each joint to the animated angle, and pins pull each body part to its
    /// animated position and hold up its weight. When the player is hit, muscles and pins go slack and the struck body
    /// part gets the hit's impulse, so the body is thrown by the blow. The muscles then recover and the pose blends back to
    /// pure animation.
    ///
    /// It is cosmetic and runs on clients only, driven by the hit data <see cref="CombatControllerGhost"/> replicates.
    /// The ragdoll lives in the <see cref="RagdollWorld"/>'s physics scene, shared by all ragdolls and stepped once per
    /// rendered frame: netcode steps the default physics scene per prediction tick, replays included, which would make
    /// ragdoll bodies there move several times a frame. Each ragdoll only has its own ground plane under the player to
    /// stand on, so limbs don't collide with walls or other ragdolls. The world decides each frame whether this ragdoll
    /// gets to simulate; without a slot it blends back to animation.
    /// Put this on the player ghost prefab next to <see cref="CombatControllerGhost"/>.
    /// </summary>
    public class ActiveRagdoll : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional. On a Humanoid rig, its main bones become the body parts.")]
        public Animator TargetAnimator;
        [Tooltip("Optional. Where to look for the model's skinned meshes. Defaults to the animator's object, or this one.")]
        public Transform ModelRoot;
        public CombatControllerGhost Combat;
        [Tooltip("Optional. While this player is carried, the ragdoll goes limp.")]
        public CarryablePlayer Carryable;

        [Header("Body parts (built from the model)")]
        [Tooltip("Most body parts a non-humanoid model gets. The smallest are merged into their parents.")]
        public int MaxParts = 16;
        [Tooltip("Non-humanoid models: share of the mesh's vertices a bone must drive to get its own body part.")]
        [Range(0f, 0.2f)] public float MinPartVertexShare = 0.02f;
        [Tooltip("Shrinks the colliders fitted to the mesh, so they sit inside the surface.")]
        [Range(0.5f, 1.2f)] public float ColliderScale = 0.9f;

        [Header("Muscles")]
        [Tooltip("Joint drive spring at full strength, per kg of the body part.")]
        public float MuscleSpring = 400f;
        [Tooltip("Joint drive damper at full strength, per kg of the body part.")]
        public float MuscleDamper = 12f;
        [Tooltip("How hard each body part is pulled to its animated position at full strength, in 1/s². Pins hold the body up and upright; muscles only bend joints.")]
        public float PinSpring = 400f;
        [Tooltip("How much each body part's velocity is matched to the animation at full strength, in 1/s.")]
        public float PinDamper = 40f;
        [Tooltip("Pin weight is strength raised to this power, so pins let go sooner than muscles and a hit body can fall.")]
        public float PinExponent = 2f;
        [Tooltip("The hips are kept at most this far from the animated hips.")]
        public float MaxHipsOffset = 0.5f;

        [Header("Hit reaction")]
        [Tooltip("A hit with this impulse (m/s) or more drops the muscles to Min Strength.")]
        public float FullLimpImpulse = 14f;
        [Range(0f, 1f)] public float MinStrength = 0.06f;
        [Tooltip("Muscle strength right after the weakest hit.")]
        [Range(0f, 1f)] public float MaxStrengthAfterHit = 0.6f;
        [Tooltip("Muscle strength regained per second while hit-stunned.")]
        public float RecoveryWhileStunned = 0.5f;
        [Tooltip("Muscle strength regained per second after hit stun ends.")]
        public float RecoveryAfterStun = 2.5f;
        [Tooltip("Multiplies the hit's impulse on the struck body part.")]
        public float ImpulseScale = 0.6f;
        [Tooltip("Share of the impulse given to the struck part's parent.")]
        [Range(0f, 1f)] public float ParentImpulseShare = 0.5f;
        [Tooltip("Seconds to blend back to the animated pose once the muscles have recovered.")]
        public float BlendOutTime = 0.25f;

        [Tooltip("Muscle strength while carried. Low means limp. The grabbed body part is held at the carrier's grab point regardless.")]
        [Range(0f, 1f)] public float CarriedStrength = 0.03f;
        [Tooltip("After being dropped or thrown, the body stays limp until the player is within this height of the ground.")]
        public float LandedHeight = 0.15f;
        [Tooltip("Seconds after release before landing is checked, so the release itself can't count as landing.")]
        public float LandingGraceTime = 0.15f;
        [Tooltip("Longest a released body stays limp in the air before recovering anyway.")]
        public float MaxLimpAirTime = 5f;

        [Header("Simulation (step rate and budget are on RagdollWorld)")]
        [Tooltip("Total ragdoll mass in kg, spread over the body parts.")]
        public float TotalMass = 70f;
        [Tooltip("How far below the ground under the player the ragdoll's floor is.")]
        public float GroundClearance = 0.02f;

        /// <summary>Current muscle strength, 0 to 1.</summary>
        public float Strength => m_Strength;
        /// <summary>How much of the pose comes from the ragdoll, 0 to 1. 0 means pure animation.</summary>
        public float Weight => m_Weight;
        public bool IsActive => m_Weight > 0f;

        class Part
        {
            public Transform Target;      // the animated bone
            public Rigidbody Body;        // its ragdoll body
            public ConfigurableJoint Joint;
            public int Parent = -1;       // index of the parent part
            public Quaternion StartLocal; // rotation relative to the parent part when the joint was made
            public float Mass;
            // Animated pose this frame, and position last frame.
            public Vector3 AnimPosition;
            public Quaternion AnimRotation;
            public Vector3 PrevAnimPosition;
        }

        readonly List<Part> m_Parts = new();
        readonly List<Collider> m_PartColliders = new();
        Rigidbody m_Ground;
        Collider m_GroundCollider;
        FPSAnimatorGhost m_AnimatorGhost;
        bool m_Initialized;
        bool m_BodiesActive = true;          // bodies are made active, so the first SetBodiesActive(false) applies
        bool m_Simulated;                    // stepped this frame, so EndFrame writes the pose
        bool m_PendingHit;                   // a hit arrived this frame; applied only if the ragdoll gets a slot
        int m_PendingHurtbox;
        Vector3 m_PendingImpulse;
        // This frame's step setup, used by each substep.
        float m_FrameDt;
        float m_Pin;
        Part m_Grabbed;
        Vector3 m_GrabStart, m_GrabTarget;
        Quaternion m_GrabStartRotation, m_GrabTargetRotation;
        bool m_HavePrevPose;
        bool m_Carried;
        bool m_LimpUntilLanded;              // released from a carry and not yet back on the ground
        float m_ReleaseTime;
        int m_GrabbedPart = -1;              // while carried: the part held at the carrier's grab point, kinematic
        Quaternion m_GrabLocalRotation;      // its rotation relative to the carrier when grabbed
        int m_LastHitCount;
        float m_Strength = 1f;
        float m_Weight;
        readonly RaycastHit[] m_GroundHits = new RaycastHit[8];

        void Awake()
        {
            if (TargetAnimator == null) TargetAnimator = GetComponentInChildren<Animator>();
            if (Combat == null) Combat = GetComponent<CombatControllerGhost>();
            if (Carryable == null) Carryable = GetComponent<CarryablePlayer>();
        }

        void Update()
        {
            // Build once spawned. From then on RagdollWorld drives this ragdoll.
            if (!m_Initialized) TryInitialize();
        }

        #region Driven by RagdollWorld, each frame

        /// <summary>Set by the world: whether this ragdoll has a simulation slot this frame.</summary>
        internal bool Granted { get; set; }
        /// <summary>Whether the ragdoll is simulating (has live bodies and a pose weight), for ranking.</summary>
        internal bool IsSimulating => m_BodiesActive && m_Weight > 0f;
        internal bool IsLocalPlayer => m_AnimatorGhost != null && m_AnimatorGhost.IsLocalOwner;
        /// <summary>Where the ragdoll is, for distance and visibility culling: the animated hips.</summary>
        internal Vector3 FocusPoint => m_Parts.Count > 0 ? m_Parts[0].AnimPosition : transform.position;
        internal IReadOnlyList<Collider> PartColliders => m_PartColliders;
        internal Collider GroundCollider => m_GroundCollider;

        /// <summary>Reads the animated pose and new hits. Returns whether the ragdoll wants to simulate this frame.</summary>
        internal bool Prepare()
        {
            ReadAnimatedPose();

            if (Combat.HitCount.Value != m_LastHitCount)
            {
                // Several hits between frames react once, to the latest.
                m_LastHitCount = Combat.HitCount.Value;
                m_PendingHit = true;
                m_PendingHurtbox = Combat.LastHitHurtbox.Value;
                m_PendingImpulse = Combat.LastHitImpulse.Value;
            }

            // Being carried: go limp, hanging from the grabbed body part, until put down. After being dropped or
            // thrown, stay limp until the player lands. Then recover like after a hit.
            bool wasCarried = m_Carried;
            bool carried = m_Carried = Carryable != null && Carryable.IsCarried;
            if (wasCarried && !carried)
            {
                m_LimpUntilLanded = true;
                m_ReleaseTime = Time.time;
            }
            else if (carried)
            {
                m_LimpUntilLanded = false;
            }

            if (m_LimpUntilLanded)
            {
                float sinceRelease = Time.time - m_ReleaseTime;
                // A short grace period, so the release frame (still at the hold pose, near the floor on a drop)
                // can't count as landing, and a timeout so the body can't stay limp forever.
                if ((sinceRelease > LandingGraceTime && IsOnGround()) || sinceRelease > MaxLimpAirTime)
                    m_LimpUntilLanded = false;
            }

            return m_PendingHit || carried || m_LimpUntilLanded || m_Weight > 0f;
        }

        /// <summary>
        /// Applies this frame's hit, carry and recovery, and sets up the step. Returns whether to step this ragdoll.
        /// Without a slot, the ragdoll freezes its bodies and blends back to animation over <paramref name="cullBlendTime"/>.
        /// </summary>
        internal bool BeginFrame(bool granted, float dt, float cullBlendTime)
        {
            bool hit = m_PendingHit;
            m_PendingHit = false;
            m_Simulated = false;

            if (!granted)
            {
                // Hits while culled are skipped: the player just plays its animated hit reaction.
                if (m_Weight <= 0f) return false;
                SetGrabbedPart(-1);
                SetBodiesActive(false);
                m_Weight = cullBlendTime > 0f ? Mathf.MoveTowards(m_Weight, 0f, dt / cullBlendTime) : 0f;
                if (m_Weight > 0f) WritePose();
                else m_Strength = 1f;
                return false;
            }

            // Back in budget partway through blending out: carry on from where the bodies were frozen.
            if (m_Weight > 0f && !m_BodiesActive) SetBodiesActive(true);
            if (hit) OnHit(m_PendingHurtbox, m_PendingImpulse);

            bool limp = m_Carried || m_LimpUntilLanded;
            if (limp)
            {
                if (m_Weight <= 0f) SnapToAnimation();
                m_Weight = 1f;
                m_Strength = Mathf.Min(m_Strength, CarriedStrength);
            }
            SetGrabbedPart(m_Carried ? FindGrabbedPart() : -1);

            if (m_Weight <= 0f) return false;
            if (!limp) Recover(dt);
            if (m_Weight <= 0f) return false;

            PrepareStep(dt);
            m_Simulated = true;
            return true;
        }

        /// <summary>Applies muscle pins and the carry grab before substep <paramref name="index"/> of <paramref name="count"/>.</summary>
        internal void Substep(int index, int count)
        {
            if (m_Grabbed != null)
            {
                float t = (index + 1f) / count;
                m_Grabbed.Body.MovePosition(Vector3.Lerp(m_GrabStart, m_GrabTarget, t));
                m_Grabbed.Body.MoveRotation(Quaternion.Slerp(m_GrabStartRotation, m_GrabTargetRotation, t));
            }
            foreach (var part in m_Parts)
                if (part != m_Grabbed) Pin(part, m_Pin, m_FrameDt);
        }

        /// <summary>Writes the simulated pose onto the skeleton, after the step.</summary>
        internal void EndFrame()
        {
            if (m_Simulated) WritePose();
            m_Simulated = false;
            foreach (var part in m_Parts) part.PrevAnimPosition = part.AnimPosition;
            m_HavePrevPose = true;
        }

        #endregion

        bool TryInitialize()
        {
            if (Combat == null) { enabled = false; return false; }
            var ghost = Combat.Ghost;
            if (ghost == null || !(ghost.IsClient || ghost.IsServer)) return false; // not spawned yet
            if (!ghost.IsClient) { enabled = false; return false; } // server-only copy: nothing to show

            m_AnimatorGhost = GetComponent<FPSAnimatorGhost>();
            if (!Build()) { enabled = false; return false; }
            m_LastHitCount = Combat.HitCount.Value; // don't react to hits from before we joined
            m_Initialized = true;
            RagdollWorld.Instance.Register(this);
            return true;
        }

        bool Build()
        {
            var root = ModelRoot != null ? ModelRoot : TargetAnimator != null ? TargetAnimator.transform : transform;
            var specs = RagdollBuilder.Build(root, TargetAnimator, new RagdollBuilder.Settings
            {
                TotalMass = TotalMass,
                MaxParts = MaxParts,
                MinPartVertexShare = MinPartVertexShare,
                ColliderScale = ColliderScale,
            });
            if (specs.Count == 0)
            {
                Debug.LogWarning($"[{nameof(ActiveRagdoll)}] {name}: no skinned skeleton found under {root.name}, so there's no ragdoll.", this);
                return false;
            }

            var world = RagdollWorld.Instance;
            var scene = world.Scene;
            int partLayer = world.PartLayer;

            // Parts come parents first, root at index 0. Each part's parent is its nearest ancestor bone with a part,
            // which skips bones like Neck and UpperChest.
            foreach (var spec in specs)
            {
                var target = spec.Bone;
                var go = new GameObject($"Ragdoll_{name}_{target.name}");
                SceneManager.MoveGameObjectToScene(go, scene);
                if (partLayer >= 0) go.layer = partLayer;
                go.transform.SetPositionAndRotation(target.position, target.rotation);

                var body = go.AddComponent<Rigidbody>();
                body.mass = spec.Mass;
                body.linearDamping = 0.05f;
                body.angularDamping = 0.5f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                body.maxAngularVelocity = 40f;
                body.solverIterations = 12;

                m_PartColliders.Add(AddCollider(go.transform, spec, partLayer));

                m_Parts.Add(new Part { Target = target, Body = body, Mass = body.mass, Parent = spec.Parent });
            }

            foreach (var part in m_Parts)
            {
                if (part.Parent < 0) continue;
                var parent = m_Parts[part.Parent];
                var joint = part.Body.gameObject.AddComponent<ConfigurableJoint>();
                joint.connectedBody = parent.Body;
                // Joint space is the body's local space, so target rotations are plain local rotations.
                joint.axis = Vector3.right;
                joint.secondaryAxis = Vector3.up;
                joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
                joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Free;
                joint.rotationDriveMode = RotationDriveMode.Slerp;
                joint.enablePreprocessing = false;
                part.Joint = joint;
                part.StartLocal = Quaternion.Inverse(parent.Body.transform.rotation) * part.Body.transform.rotation;
            }

            // Body parts don't collide with each other, only with the ground: the RagdollWorld keeps the Ragdoll
            // layer from colliding with itself, or ignores the pairs when the layer is missing.
            // The ground is a kinematic body, which is cheaper to move every frame than a static collider. It only
            // needs to cover the area the ragdoll can reach around the player.
            var ground = new GameObject($"Ragdoll_{name}_Ground");
            SceneManager.MoveGameObjectToScene(ground, scene);
            m_Ground = ground.AddComponent<Rigidbody>();
            m_Ground.isKinematic = true;
            var box = ground.AddComponent<BoxCollider>();
            box.size = new Vector3(8f, 1f, 8f);
            box.center = new Vector3(0f, -0.5f, 0f);
            m_GroundCollider = box;

            SetBodiesActive(false);
            return true;
        }

        static Collider AddCollider(Transform parent, RagdollBuilder.PartSpec spec, int layer)
        {
            var go = new GameObject("Collider");
            if (layer >= 0) go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(spec.Center, Quaternion.FromToRotation(Vector3.up, spec.Axis));
            if (spec.Height <= spec.Radius * 2.01f)
            {
                var sphere = go.AddComponent<SphereCollider>();
                sphere.radius = spec.Radius;
                return sphere;
            }
            var capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1;
            capsule.radius = spec.Radius;
            capsule.height = spec.Height;
            return capsule;
        }

        void ReadAnimatedPose()
        {
            foreach (var part in m_Parts)
            {
                part.AnimPosition = part.Target.position;
                part.AnimRotation = part.Target.rotation;
            }
        }

        void OnHit(int hurtboxIndex, Vector3 impulse)
        {
            if (m_Weight <= 0f) SnapToAnimation();

            float hitStrength = Mathf.Lerp(MaxStrengthAfterHit, MinStrength, impulse.magnitude / Mathf.Max(0.01f, FullLimpImpulse));
            m_Strength = Mathf.Min(m_Strength, hitStrength);
            m_Weight = 1f;

            // Push the struck part, and its parent less, so the blow bends the body.
            var hurtboxes = Combat.Hurtboxes;
            int struck = hurtboxes != null && hurtboxIndex >= 0 && hurtboxIndex < hurtboxes.Length ? FindPart(hurtboxes[hurtboxIndex].transform) : -1;
            if (struck < 0) struck = HeaviestPart(); // the torso, on most bodies
            var part = m_Parts[struck];
            part.Body.AddForce(impulse * ImpulseScale, ForceMode.VelocityChange);
            if (part.Parent >= 0)
                m_Parts[part.Parent].Body.AddForce(impulse * (ImpulseScale * ParentImpulseShare), ForceMode.VelocityChange);
        }

        int HeaviestPart()
        {
            int heaviest = 0;
            for (int i = 1; i < m_Parts.Count; i++)
                if (m_Parts[i].Mass > m_Parts[heaviest].Mass) heaviest = i;
            return heaviest;
        }

        /// <summary>The part for a transform: the nearest part bone at or above it.</summary>
        int FindPart(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                int index = m_Parts.FindIndex(p => p.Target == t);
                if (index >= 0) return index;
            }
            return -1;
        }

        /// <summary>The part the carrier grabbed: the one under the replicated hurtbox, or the hips.</summary>
        int FindGrabbedPart()
        {
            int hurtboxIndex = Carryable.GrabbedHurtbox.Value;
            var hurtboxes = Combat.Hurtboxes;
            int part = hurtboxes != null && hurtboxIndex >= 0 && hurtboxIndex < hurtboxes.Length && hurtboxes[hurtboxIndex] != null
                ? FindPart(hurtboxes[hurtboxIndex].transform) : -1;
            return part >= 0 ? part : 0;
        }

        /// <summary>Makes a part kinematic so the rest of the body hangs from it, or releases it with -1.</summary>
        void SetGrabbedPart(int index)
        {
            if (index == m_GrabbedPart) return;
            if (m_GrabbedPart >= 0) m_Parts[m_GrabbedPart].Body.isKinematic = m_Weight <= 0f;
            m_GrabbedPart = index;
            if (index < 0) return;

            var body = m_Parts[index].Body;
            body.isKinematic = true;
            m_GrabLocalRotation = Carryable.TryGetGrabPoint(out _, out var carrierRotation)
                ? Quaternion.Inverse(carrierRotation) * body.rotation
                : Quaternion.identity;
        }

        /// <summary>Moves the ragdoll onto the animated pose, moving the way the animation moves.</summary>
        void SnapToAnimation()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            SetBodiesActive(true);
            foreach (var part in m_Parts)
            {
                part.Body.transform.SetPositionAndRotation(part.AnimPosition, part.AnimRotation);
                part.Body.position = part.AnimPosition;
                part.Body.rotation = part.AnimRotation;
                part.Body.linearVelocity = m_HavePrevPose ? (part.AnimPosition - part.PrevAnimPosition) / dt : Vector3.zero;
                part.Body.angularVelocity = Vector3.zero;
            }
        }

        void Recover(float dt)
        {
            m_Strength = Mathf.MoveTowards(m_Strength, 1f, (Combat.IsStunned ? RecoveryWhileStunned : RecoveryAfterStun) * dt);
            if (m_Strength >= 1f && !Combat.IsStunned)
            {
                m_Weight = BlendOutTime > 0f ? Mathf.MoveTowards(m_Weight, 0f, dt / BlendOutTime) : 0f;
                if (m_Weight <= 0f) SetBodiesActive(false);
            }
        }

        /// <summary>Sets up the frame's step: ground, joint targets, keeping up with the player, and the carry grab.</summary>
        void PrepareStep(float dt)
        {
            var hips = m_Parts[0];
            m_FrameDt = dt;

            // Put the ground plane where the static ground is under the player. It sits slightly low so animated feet
            // resting on the floor aren't pushed up. With no ground in reach, it stays at the player's feet.
            float groundY = TryFindGroundY(out var y) ? y : transform.position.y;
            m_Ground.position = new Vector3(transform.position.x, groundY - GroundClearance, transform.position.z);

            // Joint targets: each part's animated rotation relative to its parent part.
            foreach (var part in m_Parts)
            {
                if (part.Joint == null) continue;
                var targetLocal = Quaternion.Inverse(m_Parts[part.Parent].AnimRotation) * part.AnimRotation;
                part.Joint.targetRotation = Quaternion.Inverse(targetLocal) * part.StartLocal;
                part.Joint.slerpDrive = new JointDrive
                {
                    positionSpring = MuscleSpring * part.Mass * m_Strength,
                    positionDamper = MuscleDamper * part.Mass * m_Strength,
                    maximumForce = float.MaxValue,
                };
            }

            // Keep the ragdoll with the player, which knockback is already moving. A teleport snaps it back.
            var offset = hips.Body.position - hips.AnimPosition;
            if (offset.magnitude > 3f)
            {
                SnapToAnimation();
            }
            else if (offset.magnitude > MaxHipsOffset && m_GrabbedPart < 0)
            {
                // Not while carried: the body hangs from the grabbed part, which can be well away from the animated hips.
                var shift = offset.normalized * MaxHipsOffset - offset;
                foreach (var part in m_Parts) part.Body.position += shift;
            }

            // The grabbed part follows the carrier's grab point over the substeps; the rest of the body hangs from it
            // through the joints.
            m_Grabbed = null;
            if (m_GrabbedPart >= 0)
            {
                m_Grabbed = m_Parts[m_GrabbedPart];
                m_GrabStart = m_Grabbed.Body.position;
                m_GrabStartRotation = m_Grabbed.Body.rotation;
                if (Carryable.TryGetGrabPoint(out m_GrabTarget, out var carrierRotation))
                {
                    m_GrabTargetRotation = carrierRotation * m_GrabLocalRotation;
                }
                else
                {
                    // Carrier not known here yet: hold the part where the animation has it.
                    m_GrabTarget = m_Grabbed.AnimPosition;
                    m_GrabTargetRotation = m_Grabbed.AnimRotation;
                }
            }

            m_Pin = Mathf.Pow(m_Strength, PinExponent);
        }

        /// <summary>
        /// Height of the static ground under the player, ignoring triggers and moving objects such as other players.
        /// False if there is none within 5 m.
        /// </summary>
        bool TryFindGroundY(out float groundY)
        {
            var origin = transform.position + Vector3.up * 0.5f;
            int count = UnityEngine.Physics.RaycastNonAlloc(origin, Vector3.down, m_GroundHits, 5.5f, UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            groundY = 0f;
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = m_GroundHits[i];
                if (hit.distance >= nearest || hit.collider.attachedRigidbody != null || hit.transform.IsChildOf(transform)) continue;
                nearest = hit.distance;
                groundY = hit.point.y;
            }
            return nearest < float.MaxValue;
        }

        /// <summary>Whether the player (its feet, not the ragdoll) is standing on the ground.</summary>
        bool IsOnGround() => TryFindGroundY(out var groundY) && transform.position.y - groundY <= LandedHeight;

        /// <summary>Pulls a body part towards its animated position and motion, and holds up its weight, by the pin weight.</summary>
        void Pin(Part part, float pin, float dt)
        {
            if (pin <= 0f) return;
            var body = part.Body;
            var animVelocity = m_HavePrevPose ? (part.AnimPosition - part.PrevAnimPosition) / dt : Vector3.zero;
            var accel = (part.AnimPosition - body.position) * (PinSpring * pin) + (animVelocity - body.linearVelocity) * (PinDamper * pin);
            accel -= UnityEngine.Physics.gravity * pin;
            body.AddForce(accel, ForceMode.Acceleration);
        }

        void WritePose()
        {
            // Parts are in hierarchy order, parents first, so writing a parent can't disturb a child written earlier.
            float w = m_Weight;
            foreach (var part in m_Parts)
            {
                var rotation = Quaternion.Slerp(part.AnimRotation, part.Body.rotation, w);
                if (part.Parent < 0)
                    part.Target.SetPositionAndRotation(Vector3.Lerp(part.AnimPosition, part.Body.position, w), rotation);
                else
                    part.Target.rotation = rotation;
            }
        }

        /// <summary>Wakes the bodies to simulate, or makes them kinematic and collisionless so they cost nothing.</summary>
        void SetBodiesActive(bool active)
        {
            if (active == m_BodiesActive) return;
            m_BodiesActive = active;
            for (int i = 0; i < m_Parts.Count; i++)
            {
                var part = m_Parts[i];
                part.Body.isKinematic = !active || i == m_GrabbedPart; // the grabbed part is moved, not simulated
                part.Body.detectCollisions = active;
            }
            if (m_Ground != null) m_Ground.detectCollisions = active;
        }

        void OnDisable()
        {
            // The world skips disabled ragdolls, so park the bodies instead of leaving them to fall in the shared scene.
            if (!m_Initialized) return;
            SetGrabbedPart(-1);
            SetBodiesActive(false);
            m_Weight = 0f;
            m_Strength = 1f;
        }

        void OnDestroy()
        {
            RagdollWorld.UnregisterIfAlive(this);
            // The bodies live in the shared scene, not under this object, so remove them explicitly.
            foreach (var part in m_Parts)
                if (part.Body != null) Destroy(part.Body.gameObject);
            if (m_Ground != null) Destroy(m_Ground.gameObject);
        }
    }
}
