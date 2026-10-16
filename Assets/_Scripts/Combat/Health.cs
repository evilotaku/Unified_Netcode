using System;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Hit points for a combat player, taken by <see cref="CombatControllerGhost.ServerHit"/>. At zero the player dies:
    /// it can't move, attack, block or be hit, and <see cref="ActiveRagdoll"/> goes limp. After
    /// <see cref="RespawnDelay"/> it gets up with full health, optionally back where it spawned, and can't be hurt for
    /// <see cref="RespawnInvulnerability"/> seconds.
    /// Only the server changes health; clients receive it in snapshots. The timers run in PredictionUpdate on the
    /// server, so this needs a predicted ghost, like the player.
    /// Put this on the player ghost prefab next to <see cref="CombatControllerGhost"/>.
    /// </summary>
    public partial class Health : GhostBehaviour
    {
        [Header("Health")]
        public float MaxHealth = 100f;
        [Tooltip("Health regained per second. 0 turns regeneration off.")]
        public float RegenPerSecond = 0f;
        [Tooltip("Seconds after taking damage before regeneration starts.")]
        public float RegenDelay = 5f;

        [Header("Death")]
        [Tooltip("Seconds a dead player stays down before respawning. Below 0 means never.")]
        public float RespawnDelay = 3f;
        [Tooltip("Respawn where the player first spawned. Off: get up where it fell.")]
        public bool RespawnAtSpawnPoint;
        [Tooltip("Seconds after respawning that hits do no damage.")]
        public float RespawnInvulnerability = 1f;

        public GhostField<float> Current;
        public GhostField<bool> IsDead;
        /// <summary>Goes up by one per death, for effects and scoring.</summary>
        public GhostField<int> DeathCount;
        public GhostField<float> RespawnRemaining;
        public GhostField<float> InvulnerableRemaining;
        public GhostField<float> RegenDelayRemaining;

        /// <summary>Server only. Raised when a player's health reaches zero, with the hit that did it.</summary>
        public static event Action<Health, CombatHit> ServerDied;
        /// <summary>Server only. Raised when a dead player respawns.</summary>
        public static event Action<Health> ServerRespawned;

        public float Fraction => MaxHealth > 0f ? Mathf.Clamp01(Current.Value / MaxHealth) : 0f;
        public bool IsInvulnerable => InvulnerableRemaining.Value > 0f;

        Rigidbody m_Body;
        FPSControllerGhost m_Controller;
        CombatControllerGhost m_Combat;
        bool m_ServerInitialized;
        Vector3 m_SpawnPosition;
        Quaternion m_SpawnRotation;

        public override void Awake()
        {
            base.Awake();
            m_Body = GetComponent<Rigidbody>();
            m_Controller = GetComponent<FPSControllerGhost>();
            m_Combat = GetComponent<CombatControllerGhost>();
        }

        // Server: start at full health before the first snapshot is sent, rather than on the first tick. Runs before
        // Awake, so it only touches GhostFields and the transform.
        public override void OnGhostSpawn()
        {
            if (IsServer && !m_ServerInitialized) ServerInitialize();
        }

        void OnEnable() => CombatControllerGhost.ServerHit += OnServerHit;
        void OnDisable() => CombatControllerGhost.ServerHit -= OnServerHit;

        public override void OnDestroy()
        {
            base.OnDestroy();
            // OnDisable doesn't always run first, such as for an Editor preview copy whose Awake failed.
            CombatControllerGhost.ServerHit -= OnServerHit;
        }

        public override void PredictionUpdate(PredictionUpdateContext context)
        {
            // Only the server changes health. The owner's prediction reads the replicated values as they are.
            if (!IsServer) return;
            if (!m_ServerInitialized) ServerInitialize();
            float dt = context.TickedDeltaTime;

            if (InvulnerableRemaining.Value > 0f)
                InvulnerableRemaining.Value = Mathf.Max(0f, InvulnerableRemaining.Value - dt);

            if (IsDead.Value)
            {
                if (RespawnDelay < 0f) return;
                RespawnRemaining.Value = Mathf.Max(0f, RespawnRemaining.Value - dt);
                if (RespawnRemaining.Value <= 0f) ServerRespawn();
                return;
            }

            if (RegenDelayRemaining.Value > 0f)
                RegenDelayRemaining.Value = Mathf.Max(0f, RegenDelayRemaining.Value - dt);
            else if (RegenPerSecond > 0f && Current.Value < MaxHealth)
                Current.Value = Mathf.Min(MaxHealth, Current.Value + RegenPerSecond * dt);
        }

        void ServerInitialize()
        {
            m_ServerInitialized = true;
            m_SpawnPosition = transform.position;
            m_SpawnRotation = transform.rotation;
            // A newly spawned ghost starts at its field's initial value, 0; a ghost that's already been running keeps its health.
            if (!IsDead.Value && Current.Value <= 0f) Current.Value = MaxHealth;
        }

        void OnServerHit(CombatHit hit)
        {
            if (hit.Target != gameObject || !IsServer) return;
            if (!m_ServerInitialized) ServerInitialize();
            if (IsDead.Value || IsInvulnerable || hit.Damage <= 0f) return;

            Current.Value = Mathf.Max(0f, Current.Value - hit.Damage);
            RegenDelayRemaining.Value = RegenDelay;
            if (Current.Value <= 0f) ServerDie(hit);
        }

        /// <summary>Server only. Changes health by <paramref name="amount"/>, such as for healing or fall damage. Doesn't respawn the dead.</summary>
        public void ServerAddHealth(float amount)
        {
            if (!IsServer || IsDead.Value) return;
            if (!m_ServerInitialized) ServerInitialize();
            Current.Value = Mathf.Clamp(Current.Value + amount, 0f, MaxHealth);
            if (Current.Value <= 0f) ServerDie(default);
        }

        void ServerDie(CombatHit hit)
        {
            IsDead.Value = true;
            DeathCount.Value++;
            RespawnRemaining.Value = Mathf.Max(0f, RespawnDelay);
            if (m_Combat != null) m_Combat.ServerInterrupt();
            ServerDied?.Invoke(this, hit);
        }

        void ServerRespawn()
        {
            IsDead.Value = false;
            Current.Value = MaxHealth;
            InvulnerableRemaining.Value = RespawnInvulnerability;
            RegenDelayRemaining.Value = 0f;
            if (RespawnAtSpawnPoint)
            {
                transform.SetPositionAndRotation(m_SpawnPosition, m_SpawnRotation);
                if (m_Body != null)
                {
                    m_Body.position = m_SpawnPosition;
                    m_Body.rotation = m_SpawnRotation;
                    if (!m_Body.isKinematic) m_Body.linearVelocity = Vector3.zero;
                }
                if (m_Controller != null) m_Controller.PredictedVelocity.Value = Vector3.zero;
            }
            ServerRespawned?.Invoke(this);
        }
    }
}
