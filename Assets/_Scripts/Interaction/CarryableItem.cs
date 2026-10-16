using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// A networked item a player can pick up, carry, drop, and throw.
    /// The server is authoritative. While carried, the item is parented under the carrier's
    /// GhostObject (the prefab's GhostObject must have Parent Replication enabled), so clients
    /// see it move with the carrier's predicted transform without interpolation lag.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [NoAutoStaticsCleanup] // statics are reset manually in ResetStatics
    public partial class CarryableItem : GhostBehaviour
    {
        /// <summary>NetworkId of the carrying player's owner, or 0 when the item is free.</summary>
        public GhostField<int> CarrierNetworkId;

        [Tooltip("Name shown in the interaction prompt.")]
        public string DisplayName = "Item";

        [Tooltip("Extra offset applied to the hold position, in the carrier's local space.")]
        public Vector3 HoldPositionOffset;

        [Tooltip("Rotation of the item relative to the carrier while held.")]
        public Vector3 HoldRotationEuler;

        static readonly List<CarryableItem> s_All = new();
        public static IReadOnlyList<CarryableItem> All => s_All;

        Rigidbody m_Rigidbody;
        Collider[] m_Colliders;
        bool m_CollidersEnabled = true;

        // Server: pose relative to the carrier while held. See KeepHoldPose.
        bool m_HoldingPose;
        Vector3 m_HoldLocalPosition;
        Quaternion m_HoldLocalRotation;

        public bool IsCarried => CarrierNetworkId.Value != 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_All.Clear();

        public override void Awake()
        {
            base.Awake();
            m_Rigidbody = GetComponent<Rigidbody>();
            m_Colliders = GetComponentsInChildren<Collider>(true);
            // The ghost's Parent Replication owns this item's parenting. NGO's parent sync would replicate it a second
            // time with its own message, and whichever arrives first on a client is rejected and reverted by the other.
            if (TryGetComponent<NetworkObject>(out var networkObject))
                networkObject.AutoObjectParentSync = false;
            // Kinematic until we know we're the server copy. Client copies are driven by snapshots, never by local physics.
            m_Rigidbody.isKinematic = true;
        }

        void OnEnable() => s_All.Add(this);
        void OnDisable() => s_All.Remove(this);

        void Start()
        {
            if (IsServer)
            {
                // The server copy simulates physics while the item is free.
                m_Rigidbody.isKinematic = IsCarried;
            }

            if (IsServer && !IsClient)
            {
                // Binary-world host or dedicated server: this copy is never seen by a local player.
                // Hide it so a host doesn't render the item twice (matches FPSControllerGhost).
                SetLayerRecursively(gameObject, LayerMask.NameToLayer("ServerWorld"));
                foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
            }
            else
            {
                // Remote client copy, or the single shared copy in Single World host mode.
                SetLayerRecursively(gameObject, LayerMask.NameToLayer("ClientWorld"));
            }
            ApplyCarriedState();
        }

        void Update()
        {
            // Clients learn about pick-ups through the replicated CarrierNetworkId.
            if (!IsServer) ApplyCarriedState();
        }

        /// <summary>
        /// Server: re-applies the hold pose. The bridge's GhostRigidbody puts this kinematic body into Netcode for Entities'
        /// predicted physics, which moves it to its transform before each step and writes its pose back after. The carrier
        /// is a dynamic body that the step can nudge (e.g. its ground-stick velocity), so without this the held item
        /// drifts a little relative to the carrier every tick. PredictionUpdate runs after each tick's physics step, so the
        /// pose set here is what the next step moves the body to. LateUpdate re-applies it before rendering.
        /// </summary>
        public override void PredictionUpdate(PredictionUpdateContext context) => KeepHoldPose();

        void LateUpdate() => KeepHoldPose();

        void KeepHoldPose()
        {
            if (!m_HoldingPose || !IsServer || transform.parent == null) return;
            transform.SetLocalPositionAndRotation(m_HoldLocalPosition, m_HoldLocalRotation);
        }

        void ApplyCarriedState()
        {
            // A held item must not push its carrier or block other players.
            SetCollidersEnabled(!IsCarried);
        }

        void SetCollidersEnabled(bool enabled)
        {
            if (m_CollidersEnabled == enabled) return;
            m_CollidersEnabled = enabled;
            foreach (var c in m_Colliders) c.enabled = enabled;
        }

        static void SetLayerRecursively(GameObject obj, int layer)
        {
            if (layer == -1) return;
            obj.layer = layer;
            foreach (Transform child in obj.transform) SetLayerRecursively(child.gameObject, layer);
        }

        #region Server

        /// <summary>Server only. Finds the server copy of an item from the ghost identity a client sent.</summary>
        internal static CarryableItem FindServerItem(NetcodeWorld serverWorld, int ghostId, uint spawnTick)
        {
            foreach (var item in s_All)
            {
                if (!item.IsServer || item.Ghost.World != serverWorld) continue;
                if (item.Ghost.GhostId == ghostId && item.Ghost.SpawnTick.SerializedData == spawnTick)
                    return item;
            }
            return null;
        }

        /// <summary>Server only. Parents this item under the carrier at the given local hold position.</summary>
        internal void ServerAttach(PlayerInteractor carrier, Vector3 holdLocalPosition)
        {
            if (!IsServer) return;

            m_Rigidbody.linearVelocity = Vector3.zero;
            m_Rigidbody.angularVelocity = Vector3.zero;
            m_Rigidbody.isKinematic = true;
            SetCollidersEnabled(false);

            CarrierNetworkId.Value = carrier.Ghost.OwnerNetworkId.Value;

            // Move to the hold pose first, then parent keeping the world pose. The GhostObject's Parent Replication
            // sends the parent change to clients.
            var carrierTransform = carrier.transform;
            transform.SetPositionAndRotation(
                carrierTransform.TransformPoint(holdLocalPosition + HoldPositionOffset),
                carrierTransform.rotation * Quaternion.Euler(HoldRotationEuler));
            transform.SetParent(carrierTransform, worldPositionStays: true);

            m_HoldLocalPosition = holdLocalPosition + HoldPositionOffset;
            m_HoldLocalRotation = Quaternion.Euler(HoldRotationEuler);
            m_HoldingPose = true;
        }

        /// <summary>Server only. Unparents the item and hands it back to physics with the given velocity.</summary>
        internal void ServerDetach(Vector3 velocity)
        {
            if (!IsServer) return;

            m_HoldingPose = false;
            transform.SetParent(null, worldPositionStays: true);
            CarrierNetworkId.Value = 0;

            SetCollidersEnabled(true);
            m_Rigidbody.isKinematic = false;
            m_Rigidbody.linearVelocity = velocity;
            m_Rigidbody.angularVelocity = Vector3.zero;
            m_Rigidbody.WakeUp();
        }

        #endregion
    }
}
