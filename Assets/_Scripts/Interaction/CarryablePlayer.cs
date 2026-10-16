using System.Collections.Generic;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Makes a player ghost something other players can pick up, carry, drop, and throw with <see cref="PlayerInteractor"/>.
    /// The server is authoritative. While carried, the server holds this player at a pose in front of the carrier and
    /// <see cref="FPSControllerGhost"/> stops simulating movement. Clients learn about it through the replicated
    /// <see cref="CarrierNetworkId"/>. Put this on the player ghost prefab next to <see cref="PlayerInteractor"/>.
    /// </summary>
    [NoAutoStaticsCleanup] // statics are reset manually in ResetStatics
    public partial class CarryablePlayer : GhostBehaviour
    {
        /// <summary>NetworkId of the carrying player's owner, or 0 when this player is free.</summary>
        public GhostField<int> CarrierNetworkId;

        /// <summary>
        /// While carried: the body part the carrier grabbed, as an index into <see cref="CombatControllerGhost.Hurtboxes"/>,
        /// or -1 for the hips.
        /// </summary>
        public GhostField<int> GrabbedHurtbox;

        [Tooltip("Name shown in the interaction prompt.")]
        public string DisplayName = "Player";

        [Tooltip("Where this player is held, in the carrier's local space (origin is the carrier's feet).")]
        public Vector3 HoldOffset = new Vector3(0f, 0.5f, 1.4f);

        [Tooltip("Where the grabbed body part is held, in the carrier's local space. The ragdoll hangs from this point.")]
        public Vector3 GrabPointOffset = new Vector3(0f, 1.5f, 0.9f);

        [Tooltip("Rotation of this player relative to the carrier while held.")]
        public Vector3 HoldRotationEuler;

        static readonly List<CarryablePlayer> s_All = new();
        public static IReadOnlyList<CarryablePlayer> All => s_All;

        FPSControllerGhost m_Controller;
        Rigidbody m_Rigidbody;
        bool m_BodyEnabled = true;

        // Server only
        Transform m_Carrier;

        public bool IsCarried => CarrierNetworkId.Value != 0;

        /// <summary>The collider that makes this player solid, or null if it has none.</summary>
        internal Collider BodyCollider => m_Controller != null ? m_Controller.CharacterCollider : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => s_All.Clear();

        public override void Awake()
        {
            base.Awake();
            m_Controller = GetComponent<FPSControllerGhost>();
            m_Rigidbody = GetComponent<Rigidbody>();
        }

        void OnEnable() => s_All.Add(this);
        void OnDisable() => s_All.Remove(this);

        void Update()
        {
            // Clients learn about pick-ups through the replicated CarrierNetworkId.
            if (!IsServer) ApplyCarriedState();
        }

        /// <summary>Server: keeps the held pose after each tick's physics step (ready for the next one) and again before rendering.</summary>
        public override void PredictionUpdate(PredictionUpdateContext context) => KeepHoldPose();

        void LateUpdate() => KeepHoldPose();

        void KeepHoldPose()
        {
            if (!IsServer || !IsCarried) return;

            // The carrier was destroyed without dropping us (e.g. disconnect): don't stay stuck.
            if (m_Carrier == null)
            {
                ServerDetach(Vector3.zero);
                return;
            }

            var rotation = m_Carrier.rotation * Quaternion.Euler(HoldRotationEuler);
            var position = m_Carrier.TransformPoint(HoldOffset);
            transform.SetPositionAndRotation(position, rotation);
            if (m_Rigidbody != null)
            {
                m_Rigidbody.position = position;
                m_Rigidbody.rotation = rotation;
                m_Rigidbody.linearVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Where the grabbed body part should be held, from this copy's view of the carrier, and the carrier's rotation.
        /// False when not carried or the carrier isn't known in this world.
        /// </summary>
        public bool TryGetGrabPoint(out Vector3 position, out Quaternion carrierRotation)
        {
            position = default;
            carrierRotation = Quaternion.identity;
            if (!IsCarried) return false;

            int carrierId = CarrierNetworkId.Value;
            foreach (var carrier in PlayerInteractor.All)
            {
                if (carrier.Ghost.World != Ghost.World || carrier.Ghost.OwnerNetworkId.Value != carrierId) continue;
                position = carrier.transform.TransformPoint(GrabPointOffset);
                carrierRotation = carrier.transform.rotation;
                return true;
            }
            return false;
        }

        void ApplyCarriedState()
        {
            // A held player must not push its carrier or block other players.
            SetBodyEnabled(!IsCarried);
        }

        void SetBodyEnabled(bool enabled)
        {
            if (m_BodyEnabled == enabled) return;
            m_BodyEnabled = enabled;
            var body = BodyCollider;
            if (body != null) body.enabled = enabled;
        }

        #region Server

        /// <summary>Server only. Finds the server copy of a player from the ghost identity a client sent.</summary>
        internal static CarryablePlayer FindServerPlayer(NetcodeWorld serverWorld, int ghostId, uint spawnTick)
        {
            foreach (var player in s_All)
            {
                if (!player.IsServer || player.Ghost.World != serverWorld) continue;
                if (player.Ghost.GhostId == ghostId && player.Ghost.SpawnTick.SerializedData == spawnTick)
                    return player;
            }
            return null;
        }

        /// <summary>Server only. Starts holding this player at the carrier's hold pose, grabbed by the given hurtbox.</summary>
        internal void ServerAttach(PlayerInteractor carrier, int hurtbox)
        {
            if (!IsServer) return;

            var combat = GetComponent<CombatControllerGhost>();
            int hurtboxCount = combat != null && combat.Hurtboxes != null ? combat.Hurtboxes.Length : 0;

            m_Carrier = carrier.transform;
            CarrierNetworkId.Value = carrier.Ghost.OwnerNetworkId.Value;
            GrabbedHurtbox.Value = hurtbox >= 0 && hurtbox < hurtboxCount ? hurtbox : -1;
            if (m_Controller != null) m_Controller.PredictedVelocity.Value = Vector3.zero;
            SetBodyEnabled(false);
            KeepHoldPose();
        }

        /// <summary>Server only. Releases this player and hands it back to its controller with the given velocity.</summary>
        internal void ServerDetach(Vector3 velocity)
        {
            if (!IsServer) return;

            m_Carrier = null;
            CarrierNetworkId.Value = 0;
            SetBodyEnabled(true);
            if (m_Controller != null) m_Controller.PredictedVelocity.Value = velocity;
        }

        #endregion
    }
}
