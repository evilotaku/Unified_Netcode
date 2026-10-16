using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// A predicted projectile, such as a fireball, that flies straight and explodes on the first thing it touches or
    /// when its lifetime runs out. The explosion hits everyone in <see cref="ExplosionRadius"/> with the firing attack's
    /// damage, knockback and hit stun (see <see cref="CombatControllerGhost.ServerExplosion"/>).
    /// Fired by an <see cref="AttackData"/> with a Projectile Prefab. The server spawns the real one and the shooter's
    /// client predict-spawns its own copy, so the shooter sees it straight away. Its flight is worked out from
    /// <see cref="Origin"/>, <see cref="Direction"/> and <see cref="Elapsed"/>, so every machine predicts the same path.
    /// Only the server applies hits and despawns it; clients predict the explosion for its effects.
    /// A player who parries it sends it straight back the way it came, as their own projectile.
    /// The prefab's GhostObject must support predicted mode.
    /// </summary>
    public partial class Projectile : GhostBehaviour
    {
        [Header("Flight")]
        [Tooltip("Meters per second.")]
        public float Speed = 20f;
        [Tooltip("Radius of the sphere swept along the flight path to find what it touches.")]
        public float Radius = 0.25f;
        [Tooltip("Seconds before it explodes in the air.")]
        public float MaxLifetime = 2.5f;

        [Header("Explosion")]
        public float ExplosionRadius = 3f;
        [Tooltip("Seconds the server keeps it after exploding, so clients receive the explosion before it's removed.")]
        public float DespawnDelay = 0.5f;

        [Header("Visuals")]
        [Tooltip("Hidden when it explodes, such as the ball and its trail.")]
        public GameObject[] HideOnExplode = System.Array.Empty<GameObject>();
        [Tooltip("Spawned where it explodes.")]
        public GameObject ExplosionPrefab;
        [Tooltip("Seconds before the spawned explosion is destroyed.")]
        public float ExplosionLifetime = 2f;
        public AudioClip ExplosionSound;
        [Range(0f, 1f)] public float ExplosionVolume = 1f;

        // Origin, Direction and Elapsed decide the position; they're predicted, so they stay unquantized.
        public GhostField<Vector3> Origin;
        public GhostField<Vector3> Direction;
        public GhostField<float> Elapsed;
        /// <summary>
        /// The shooter's <see cref="CombatControllerGhost.GhostId"/>, so it doesn't hit them. After a parry, it's the
        /// player who parried it.
        /// </summary>
        public GhostField<int> ShooterGhostId;
        /// <summary>
        /// The <see cref="CombatControllerGhost.GhostId"/> of the player who fired it. Unlike
        /// <see cref="ShooterGhostId"/>, a parry doesn't change it, so the spawn classification can match the server's
        /// projectile to the shooter's predicted copy even when one of them has already bounced.
        /// </summary>
        public GhostField<int> LaunchShooterGhostId;
        /// <summary>The attack that fired it, as an id in the shooter's ComboSet.</summary>
        public GhostField<int> AttackId;
        public GhostField<bool> Exploded;
        public GhostField<Vector3> ExplodePoint;
        public GhostField<float> ExplodeElapsed;

        static readonly System.Collections.Generic.List<Projectile> s_All = new();
        /// <summary>Every projectile in play, on the server and clients.</summary>
        public static System.Collections.Generic.IReadOnlyList<Projectile> All => s_All;

        readonly RaycastHit[] m_Hits = new RaycastHit[16];
        readonly Collider[] m_Overlaps = new Collider[16];
        CombatControllerGhost m_Shooter;
        bool m_ExplosionShown;

        /// <summary>Sets up a projectile that was just instantiated, on the server or in the shooter's prediction.</summary>
        internal void Launch(CombatControllerGhost shooter, int attackId, Vector3 origin, Vector3 direction)
        {
            m_Shooter = shooter;
            ShooterGhostId.Value = shooter.GhostId;
            LaunchShooterGhostId.Value = shooter.GhostId;
            AttackId.Value = attackId;
            Origin.Value = origin;
            Direction.Value = direction.normalized;
            Elapsed.Value = 0f;
            Exploded.Value = false;
        }

        void OnEnable() => s_All.Add(this);
        void OnDisable() => s_All.Remove(this);

        void Start()
        {
            // Same layer rules as the player: a server-only copy goes on ServerWorld and isn't drawn.
            bool serverOnly = IsServer && !IsClient;
            SetLayerRecursively(gameObject, LayerMask.NameToLayer(serverOnly ? "ServerWorld" : "ClientWorld"));
            if (serverOnly)
            {
                foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;
                foreach (var p in GetComponentsInChildren<ParticleSystem>()) p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        static void SetLayerRecursively(GameObject obj, int layer)
        {
            if (layer < 0) return;
            obj.layer = layer;
            foreach (Transform child in obj.transform) SetLayerRecursively(child.gameObject, layer);
        }

        Vector3 PositionAt(float elapsed) => Origin.Value + Direction.Value * (Speed * elapsed);

        public override void PredictionUpdate(PredictionUpdateContext context)
        {
            float dt = context.TickedDeltaTime;
            if (Exploded.Value)
            {
                ExplodeElapsed.Value += dt;
                transform.position = ExplodePoint.Value;
                if (IsServer && ExplodeElapsed.Value >= DespawnDelay) Destroy(gameObject);
                return;
            }

            float before = Elapsed.Value;
            float after = before + dt;
            Elapsed.Value = after;
            var from = PositionAt(before);
            var to = PositionAt(after);

            if (TryFindImpact(from, to, before <= 0f, out var point, out var player))
            {
                if (player == null || !TryReflect(player, from, point))
                    Explode(point);
            }
            else if (after >= MaxLifetime)
                Explode(to);
            else
                transform.SetPositionAndRotation(to, Quaternion.LookRotation(Direction.Value));
        }

        /// <summary>
        /// Sweeps the ball from <paramref name="from"/> to <paramref name="to"/> and finds where it first touches
        /// something, and the player it touched, if any.
        /// </summary>
        bool TryFindImpact(Vector3 from, Vector3 to, bool firstStep, out Vector3 point, out CombatControllerGhost player)
        {
            point = to;
            player = null;
            var shooter = Shooter;
            int mask = shooter != null ? shooter.HitMask() : UnityEngine.Physics.DefaultRaycastLayers;

            // A sweep misses colliders it starts inside, so check the launch point too.
            if (firstStep)
            {
                int overlaps = UnityEngine.Physics.OverlapSphereNonAlloc(from, Radius, m_Overlaps, mask, QueryTriggerInteraction.Collide);
                for (int i = 0; i < overlaps; i++)
                {
                    if (!CanHit(m_Overlaps[i], shooter)) continue;
                    point = from;
                    player = PlayerOf(m_Overlaps[i]);
                    return true;
                }
            }

            var delta = to - from;
            float distance = delta.magnitude;
            if (distance <= 0f) return false;
            int count = UnityEngine.Physics.SphereCastNonAlloc(from, Radius, delta / distance, m_Hits, distance, mask, QueryTriggerInteraction.Collide);
            float nearest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = m_Hits[i];
                if (hit.distance >= nearest || !CanHit(hit.collider, shooter)) continue;
                nearest = hit.distance;
                point = from + delta / distance * hit.distance;
                player = PlayerOf(hit.collider);
            }
            return nearest < float.MaxValue;
        }

        /// <summary>Solid world geometry and other players' hurtboxes stop it. Triggers, itself and the shooter don't.</summary>
        bool CanHit(Collider collider, CombatControllerGhost shooter)
        {
            if (collider.transform.IsChildOf(transform)) return false;
            if (shooter != null && collider.transform.IsChildOf(shooter.transform)) return false;
            if (collider.TryGetComponent<Hurtbox>(out var hurtbox))
                return hurtbox.Owner != null && hurtbox.Owner != shooter && !hurtbox.Owner.IsDead;
            if (collider.isTrigger) return false;
            // Players are hit through their hurtboxes, not their character controllers.
            return collider.GetComponentInParent<CombatControllerGhost>() == null;
        }

        static CombatControllerGhost PlayerOf(Collider collider) =>
            collider.TryGetComponent<Hurtbox>(out var hurtbox) ? hurtbox.Owner : null;

        CombatControllerGhost Shooter
        {
            get
            {
                // The server's copy and the shooter's predicted copy are told in Launch; other clients look it up.
                // A parry, or a rollback of one, changes the shooter.
                if (m_Shooter == null || m_Shooter.GhostId != ShooterGhostId.Value)
                    m_Shooter = CombatControllerGhost.FindByGhostId(ShooterGhostId.Value, this);
                return m_Shooter;
            }
        }

        /// <summary>The attack that fired it. Every player uses the same ComboSet, so any player's set will do.</summary>
        internal AttackData Attack
        {
            get
            {
                var shooter = Shooter;
                return shooter != null && shooter.Combos != null ? shooter.Combos.GetAttack(AttackId.Value) : null;
            }
        }

        /// <summary>
        /// If <paramref name="player"/> parries it, sends it back the way it came from <paramref name="point"/>, now
        /// fired by them. Predicted like the rest of the flight; only the server records the parry for its effects.
        /// </summary>
        bool TryReflect(CombatControllerGhost player, Vector3 from, Vector3 point)
        {
            var attack = Attack;
            if (attack == null || !player.ParriesProjectile(from, attack)) return false;

            Origin.Value = point;
            Direction.Value = -Direction.Value;
            Elapsed.Value = 0f;
            ShooterGhostId.Value = player.GhostId;
            m_Shooter = player;
            transform.SetPositionAndRotation(point, Quaternion.LookRotation(Direction.Value));
            if (IsServer) player.ServerParryProjectile(point);
            return true;
        }

        void Explode(Vector3 point)
        {
            Exploded.Value = true;
            ExplodePoint.Value = point;
            ExplodeElapsed.Value = 0f;
            transform.position = point;
            if (!IsServer) return;

            var attack = Attack;
            if (attack != null) Shooter.ServerExplosion(attack, point, ExplosionRadius);
        }

        void Update()
        {
            if (!IsClient) return;
            // Exploded is predicted, so a rollback can undo it; show the ball again if so.
            bool exploded = Exploded.Value;
            if (exploded == m_ExplosionShown) return;
            m_ExplosionShown = exploded;
            foreach (var obj in HideOnExplode)
                if (obj != null) obj.SetActive(!exploded);
            if (!exploded) return;

            var point = ExplodePoint.Value;
            if (ExplosionPrefab != null)
            {
                var effect = Instantiate(ExplosionPrefab, point, Quaternion.identity);
                SetLayerRecursively(effect, LayerMask.NameToLayer("ClientWorld"));
                Destroy(effect, ExplosionLifetime);
            }
            if (ExplosionSound != null) AudioSource.PlayClipAtPoint(ExplosionSound, point, ExplosionVolume);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.4f, 0.1f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, Radius);
            Gizmos.color = new Color(1f, 0.2f, 0f, 0.3f);
            Gizmos.DrawWireSphere(transform.position, ExplosionRadius);
        }
    }
}
