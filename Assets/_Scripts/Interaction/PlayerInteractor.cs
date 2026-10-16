using System.Collections.Generic;
using Unity.Burst.Intrinsics;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Netcode
{
    /// <summary>
    /// Lets a player pick up, carry, drop, and throw <see cref="CarryableItem"/>s.
    /// Owning client: aims with a raycast from the camera and sends <see cref="InteractRequest"/> RPCs.
    /// Server: validates each request (ownership, range, item state) and attaches or detaches the item.
    /// Put this on the player ghost prefab next to <see cref="FPSControllerGhost"/>.
    /// </summary>
    [NoAutoStaticsCleanup] // statics are reset manually in ResetStatics
    public partial class PlayerInteractor : GhostBehaviour
    {
        [Header("Aiming")]
        [Tooltip("Max distance from the camera at which an item can be picked up.")]
        public float InteractRange = 3f;
        [Tooltip("Extra range the server allows, to absorb interpolation delay between client and server views.")]
        public float ServerRangeTolerance = 1f;

        [Header("Carrying")]
        [Tooltip("Hold position relative to the camera target, in the player's local space.")]
        public Vector3 HoldOffset = new Vector3(0f, -0.4f, 1.1f);
        [Tooltip("Speed added along the aim direction when throwing.")]
        public float ThrowSpeed = 10f;
        [Tooltip("Extra upward speed added when throwing, on top of the aim direction, for a higher arc.")]
        public float ThrowUpwardSpeed = 5f;

        [Header("UI")]
        public bool ShowPrompt = true;

        static readonly List<PlayerInteractor> s_All = new();
        readonly RaycastHit[] m_Hits = new RaycastHit[16];

        FPSControllerGhost m_Controller;
        CarryablePlayer m_Self;
        Transform m_Eye;

        InputAction m_InteractAction;
        InputAction m_ThrowAction;

        // Owning client state
        CarryableItem m_Focused;
        CarryableItem m_LocallyHeld;
        CarryablePlayer m_FocusedPlayer;
        int m_FocusedHurtbox = -1;
        CarryablePlayer m_LocallyHeldPlayer;

        /// <summary>Owning client only. Whether this player is carrying an item or player, as seen by the client.</summary>
        public bool IsLocallyCarrying => m_LocallyHeld != null || m_LocallyHeldPlayer != null;

        /// <summary>Server only. The item this player is carrying.</summary>
        public CarryableItem HeldItem { get; private set; }

        /// <summary>Server only. The player this player is carrying.</summary>
        public CarryablePlayer HeldPlayer { get; private set; }

        internal static IReadOnlyList<PlayerInteractor> All => s_All;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_All.Clear();

        public override void Awake()
        {
            base.Awake();
            m_Controller = GetComponent<FPSControllerGhost>();
            m_Self = GetComponent<CarryablePlayer>();
            m_Eye = m_Controller != null && m_Controller.CameraTarget != null ? m_Controller.CameraTarget : transform;

            InputSystem.actions.Enable();
            m_InteractAction = InputSystem.actions.FindAction("Interact");
            m_ThrowAction = InputSystem.actions.FindAction("Attack");
        }

        void OnEnable() => s_All.Add(this);
        void OnDisable() => s_All.Remove(this);

        #region Owning client

        bool IsLocalOwner()
        {
            if (!IsClient) return false;
            var em = Ghost.World.EntityManager;
            return em.HasComponent<GhostOwnerIsLocal>(Ghost.Entity) && em.IsComponentEnabled<GhostOwnerIsLocal>(Ghost.Entity);
        }

        void Update()
        {
            // A player who is being carried can't interact either.
            if (!IsLocalOwner() || (m_Self != null && m_Self.IsCarried))
            {
                m_Focused = null;
                m_LocallyHeld = null;
                m_FocusedPlayer = null;
                m_LocallyHeldPlayer = null;
                return;
            }

            m_LocallyHeld = FindItemHeldByMe();
            m_LocallyHeldPlayer = FindPlayerHeldByMe();
            if (IsLocallyCarrying)
            {
                m_Focused = null;
                m_FocusedPlayer = null;
            }
            else
            {
                FindAimed(out m_Focused, out m_FocusedPlayer);
            }

            bool interactPressed = m_InteractAction != null && m_InteractAction.WasPressedThisFrame();
            bool throwPressed = m_ThrowAction != null && m_ThrowAction.WasPressedThisFrame();

            if (IsLocallyCarrying)
            {
                if (throwPressed) Send(InteractAction.Throw, null);
                else if (interactPressed) Send(InteractAction.Drop, null);
            }
            else if (m_Focused != null && interactPressed)
            {
                Send(InteractAction.PickUp, m_Focused);
            }
            else if (m_FocusedPlayer != null && interactPressed)
            {
                Send(InteractAction.PickUpPlayer, m_FocusedPlayer, m_FocusedHurtbox);
            }
        }

        void Send(InteractAction action, GhostBehaviour target, int hurtbox = -1)
        {
            var request = new InteractRequest { Action = (byte)action, Hurtbox = hurtbox };
            if (target != null)
            {
                request.ItemGhostId = target.Ghost.GhostId;
                request.ItemSpawnTick = target.Ghost.SpawnTick.SerializedData;
            }
            // Send from this ghost's client world to its server.
            Netcode.RPC.Invoke(request, Ghost.World, SendDirection.ClientToServer);
        }

        CarryableItem FindItemHeldByMe()
        {
            int myId = Ghost.OwnerNetworkId.Value;
            foreach (var item in CarryableItem.All)
            {
                if (item.IsClient && item.Ghost.World == Ghost.World && item.CarrierNetworkId.Value == myId)
                    return item;
            }
            return null;
        }

        CarryablePlayer FindPlayerHeldByMe()
        {
            int myId = Ghost.OwnerNetworkId.Value;
            foreach (var player in CarryablePlayer.All)
            {
                if (player.IsClient && player.Ghost.World == Ghost.World && player.CarrierNetworkId.Value == myId)
                    return player;
            }
            return null;
        }

        void FindAimed(out CarryableItem item, out CarryablePlayer player)
        {
            var cam = Camera.main;
            var origin = cam != null ? cam.transform : m_Eye;

            int clientLayer = LayerMask.NameToLayer("ClientWorld");
            int mask = clientLayer >= 0 ? 1 << clientLayer : UnityEngine.Physics.DefaultRaycastLayers;

            int count = UnityEngine.Physics.RaycastNonAlloc(origin.position, origin.forward, m_Hits, InteractRange, mask, QueryTriggerInteraction.Ignore);

            // Nearest hit that isn't part of our own player. Walls and other blockers hide targets behind them.
            CarryableItem resultItem = null;
            CarryablePlayer resultPlayer = null;
            Vector3 resultPoint = default;
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = m_Hits[i];
                if (hit.collider.transform.IsChildOf(transform)) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance;
                resultPoint = hit.point;
                resultItem = hit.collider.GetComponentInParent<CarryableItem>();
                resultPlayer = hit.collider.GetComponentInParent<CarryablePlayer>();
            }
            item = resultItem != null && resultItem.IsClient && !resultItem.IsCarried ? resultItem : null;
            player = resultPlayer != null && resultPlayer.IsClient && !resultPlayer.IsCarried ? resultPlayer : null;
            m_FocusedHurtbox = player != null ? FindAimedHurtbox(player, origin, mask, resultPoint) : -1;
        }

        /// <summary>
        /// The body part of <paramref name="player"/> under the crosshair, as an index into its hurtboxes. The body
        /// capsule is usually hit before the hurtboxes inside it, so this casts again against triggers, and falls back
        /// to the hurtbox nearest the capsule hit point.
        /// </summary>
        int FindAimedHurtbox(CarryablePlayer player, Transform origin, int mask, Vector3 capsuleHitPoint)
        {
            if (!player.TryGetComponent<CombatControllerGhost>(out var combat) || combat.Hurtboxes == null) return -1;
            var hurtboxes = combat.Hurtboxes;

            int count = UnityEngine.Physics.RaycastNonAlloc(origin.position, origin.forward, m_Hits, InteractRange + 1f, mask, QueryTriggerInteraction.Collide);
            int result = -1;
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = m_Hits[i];
                if (hit.distance >= nearest || !hit.collider.TryGetComponent<Hurtbox>(out var hurtbox)) continue;
                int index = System.Array.IndexOf(hurtboxes, hurtbox);
                if (index < 0) continue; // someone else's
                nearest = hit.distance;
                result = index;
            }
            if (result >= 0) return result;

            float nearestSq = float.MaxValue;
            for (int i = 0; i < hurtboxes.Length; i++)
            {
                if (hurtboxes[i] == null || !hurtboxes[i].TryGetComponent<Collider>(out var col)) continue;
                float distSq = (col.ClosestPoint(capsuleHitPoint) - capsuleHitPoint).sqrMagnitude;
                if (distSq >= nearestSq) continue;
                nearestSq = distSq;
                result = i;
            }
            return result;
        }

        void OnGUI()
        {
            if (!ShowPrompt) return;

            string text = null;
            if (m_LocallyHeld != null) text = $"[E] Drop {m_LocallyHeld.DisplayName}    [LMB] Throw";
            else if (m_LocallyHeldPlayer != null) text = $"[E] Drop {m_LocallyHeldPlayer.DisplayName}    [LMB] Throw";
            else if (m_Focused != null) text = $"[E] Pick up {m_Focused.DisplayName}";
            else if (m_FocusedPlayer != null) text = $"[E] Pick up {m_FocusedPlayer.DisplayName}";
            if (text == null) return;

            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 18 };
            var rect = new Rect(0, Screen.height * 0.5f + 40f, Screen.width, 30f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), $"<color=black>{text}</color>", style);
            GUI.Label(rect, text, style);
        }

        #endregion

        #region Server

        /// <summary>Server only. Finds the server copy of the player owned by a connection.</summary>
        internal static PlayerInteractor FindServerPlayer(NetcodeWorld serverWorld, NetworkId owner)
        {
            foreach (var p in s_All)
            {
                if (!p.IsServer || p.Ghost.World != serverWorld) continue;
                if (p.Ghost.OwnerNetworkId.Value == owner.Value) return p;
            }
            return null;
        }

        /// <summary>Server only. Validates and applies a request sent by this player's owner.</summary>
        internal void ServerHandleRequest(in InteractRequest request)
        {
            if (!IsServer) return;
            if (m_Self != null && m_Self.IsCarried) return; // can't act while being carried

            switch ((InteractAction)request.Action)
            {
                case InteractAction.PickUpPlayer:
                    ServerTryPickUpPlayer(request.ItemGhostId, request.ItemSpawnTick, request.Hurtbox);
                    break;
                case InteractAction.PickUp:
                    ServerTryPickUp(request.ItemGhostId, request.ItemSpawnTick);
                    break;
                case InteractAction.Drop:
                    ServerDrop(CarrierVelocity());
                    break;
                case InteractAction.Throw:
                    ServerDrop(velocity: CarrierVelocity() + m_Eye.forward * ThrowSpeed + (Vector3.up * ThrowUpwardSpeed));
                    break;
            }
        }

        void ServerTryPickUp(int ghostId, uint spawnTick)
        {
            if (HeldItem != null || HeldPlayer != null) return;

            var item = CarryableItem.FindServerItem(Ghost.World, ghostId, spawnTick);
            if (item == null || item.IsCarried) return;

            // Range check against the item's surface, with tolerance for the client's interpolated view.
            var eye = m_Eye.position;
            var closest = item.TryGetComponent<Collider>(out var col) && col.enabled ? col.ClosestPoint(eye) : item.transform.position;
            if (Vector3.Distance(eye, closest) > InteractRange + ServerRangeTolerance) return;

            var holdLocal = transform.InverseTransformPoint(m_Eye.position) + HoldOffset;
            item.ServerAttach(this, holdLocal);
            HeldItem = item;
        }

        void ServerTryPickUpPlayer(int ghostId, uint spawnTick, int hurtbox)
        {
            if (HeldItem != null || HeldPlayer != null) return;

            var target = CarryablePlayer.FindServerPlayer(Ghost.World, ghostId, spawnTick);
            if (target == null || target == m_Self || target.IsCarried) return;

            // Don't pick up someone who is busy carrying something.
            if (target.TryGetComponent<PlayerInteractor>(out var targetInteractor)
                && (targetInteractor.HeldItem != null || targetInteractor.HeldPlayer != null))
                return;

            // Range check against the target's body, with tolerance for the client's interpolated view.
            var eye = m_Eye.position;
            var body = target.BodyCollider;
            var closest = body != null && body.enabled ? body.ClosestPoint(eye) : target.transform.position;
            if (Vector3.Distance(eye, closest) > InteractRange + ServerRangeTolerance) return;

            target.ServerAttach(this, hurtbox);
            HeldPlayer = target;
        }

        /// <summary>Server only. Releases the held item or player, if any.</summary>
        internal void ServerDrop(Vector3 velocity)
        {
            // Unity's overloaded null check also covers something destroyed while held.
            if (HeldItem != null) HeldItem.ServerDetach(velocity);
            HeldItem = null;

            if (HeldPlayer != null) HeldPlayer.ServerDetach(velocity);
            HeldPlayer = null;
        }

        Vector3 CarrierVelocity() => m_Controller != null ? m_Controller.PredictedVelocity.Value : Vector3.zero;

        #endregion
    }
}
