using Unity.Mathematics;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>How a <see cref="CombatDummy"/> defends against attacks.</summary>
    public enum DummyDefense
    {
        /// <summary>Takes every hit.</summary>
        None,
        /// <summary>Keeps its guard up, so attacks are blocked but never parried.</summary>
        Block,
        /// <summary>Keeps its guard down and presses Block just before each parryable attack lands.</summary>
        Parry,
        /// <summary>Keeps its guard up, but parries each attack with <see cref="CombatDummy.ParryChance"/>.</summary>
        Mixed,
    }

    /// <summary>
    /// A server-side brain for a player ghost that no client owns, for testing combat against. It turns to face the
    /// nearest player, can block or parry their attacks, and can attack them with a combo string so they can practice
    /// blocking and parrying.
    /// It plays by the same rules as a player: it presses the same buttons, through <see cref="FPSControllerGhost"/>
    /// and <see cref="CombatControllerGhost"/>, so its parries go through the real parry window and cooldown.
    /// Only the server runs it. Spawn it with <see cref="CombatDummySpawner"/>.
    /// </summary>
    public class CombatDummy : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Players farther than this are ignored.")]
        public float SightRange = 12f;
        [Tooltip("Also target other dummies, so two dummies can fight each other.")]
        public bool TargetDummies;
        [Tooltip("Turn to face whoever is attacking the dummy, or else the target.")]
        public bool FaceTarget = true;
        [Tooltip("Degrees per second the dummy turns at.")]
        public float TurnSpeed = 540f;
        [Tooltip("Walk toward the target until within Follow Distance, such as after knocking it back.")]
        public bool FollowTarget;
        public float FollowDistance = 1.1f;
        [Tooltip("Following only chases targets within this distance of where the dummy spawned, and walks back otherwise, so it doesn't wander off.")]
        public float LeashRadius = 5f;

        [Header("Defense")]
        public DummyDefense Defense = DummyDefense.Block;
        [Tooltip("Mixed defense: the chance of parrying each attack rather than blocking it.")]
        [Range(0f, 1f)] public float ParryChance = 0.5f;
        [Tooltip("Seconds before an attack's hit window opens, or a projectile arrives, that the dummy presses Block to parry, on top of one simulation step. Keep the total below the Parry Window.")]
        public float ParryLead = 0.05f;

        [Header("Offense")]
        [Tooltip("Attack the target when it's in range.")]
        public bool Attack;
        [Tooltip("The buttons pressed for each combo string, one per attack in the combo.")]
        public AttackKind[] AttackSequence = { AttackKind.Light, AttackKind.Light, AttackKind.Light };
        [Tooltip("Attacks start when the target is within this distance. Keep it within the attacks' reach.")]
        public float AttackRange = 1.4f;
        [Tooltip("Seconds between the end of one combo string and the start of the next.")]
        public float AttackInterval = 1.5f;

        CombatControllerGhost m_Combat;
        FPSControllerGhost m_Controller;
        CombatControllerGhost m_Target;
        Unity.Mathematics.Random m_Random;
        Vector3 m_Home;

        // Offense: how far through AttackSequence the current string is, and the swing the last press was for.
        int m_SequenceIndex;
        int m_PressedForSerial = -1;
        float m_AttackCooldown;

        // Defense: the attack being defended against, and whether it's being parried.
        // A player swinging at the dummy, or a projectile flying at it.
        Component m_Threat;
        int m_ThreatSerial;
        bool m_ParryThreat;

        public CombatControllerGhost Target => m_Target;

        void Awake()
        {
            m_Combat = GetComponent<CombatControllerGhost>();
            m_Controller = GetComponent<FPSControllerGhost>();
            m_Random = Unity.Mathematics.Random.CreateFromIndex((uint)GetEntityId().GetHashCode());
            m_AttackCooldown = AttackInterval;
            m_Home = transform.position; // Instantiate places it before Awake
        }

        /// <summary>Server only, once a tick from <see cref="FPSControllerGhost"/>: turns and walks toward the target.</summary>
        internal void DriveMovement(ref FPSInputs input, float dt)
        {
            m_Target = FindTarget();
            input.Move = float2.zero;
            input.Look = float2.zero;

            // Face whoever is attacking first, since blocks only stop attacks from in front, then the target.
            var face = m_Threat != null ? m_Threat : m_Target;
            if (face != null)
            {
                var toFace = Flat(face.transform.position - transform.position);
                if (FaceTarget && toFace.sqrMagnitude > 0.0001f && m_Controller.LookSensitivity > 0f)
                {
                    // Look input is a yaw delta scaled by the look sensitivity.
                    float angle = Vector3.SignedAngle(transform.forward, toFace, Vector3.up);
                    float step = Mathf.Clamp(angle, -TurnSpeed * dt, TurnSpeed * dt);
                    input.Look = new float2(step / m_Controller.LookSensitivity, 0f);
                }
            }
            if (!FollowTarget) return;

            bool chase = m_Target != null && Flat(m_Target.transform.position - m_Home).magnitude <= LeashRadius;
            var goal = chase ? m_Target.transform.position : m_Home;
            var toGoal = Flat(goal - transform.position);
            if (toGoal.magnitude > (chase ? FollowDistance : 0.3f))
            {
                // Move input is in the dummy's own space.
                var local = transform.InverseTransformDirection(toGoal.normalized);
                input.Move = new float2(local.x, local.z);
            }
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>Server only, once a tick from <see cref="CombatControllerGhost"/>: blocks, parries and attacks.</summary>
        internal void DriveCombat(ref FPSInputs input, float dt)
        {
            input.LightAttack = default;
            input.MediumAttack = default;
            input.HeavyAttack = default;
            input.SpecialAttack = default;

            float timeToHit = UpdateThreat();
            input.Block = WantsBlock(timeToHit, dt);

            // Defending comes first: don't start an attack into one that's about to land.
            bool threatened = timeToHit < 0.3f && Defense != DummyDefense.None;
            var press = ChooseAttack(dt, threatened);
            if (press == AttackKind.None) return;

            input.Block = false;
            if (press == AttackKind.Light) input.LightAttack.Set();
            else if (press == AttackKind.Medium) input.MediumAttack.Set();
            else if (press == AttackKind.Heavy) input.HeavyAttack.Set();
            else input.SpecialAttack.Set();
        }

        bool WantsBlock(float timeToHit, float dt)
        {
            switch (Defense)
            {
                case DummyDefense.Block:
                    return true;
                case DummyDefense.Parry:
                case DummyDefense.Mixed:
                    // Parrying: guard down until the hit is about to land, then a fresh press opens the parry window.
                    // This runs once a step, and a slow server's step can cover several ticks, so press a step early.
                    if (m_Threat != null && m_ParryThreat) return timeToHit <= ParryLead + dt;
                    return Defense == DummyDefense.Mixed;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Finds the attack or projectile about to land on the dummy soonest and decides whether to parry it. Returns the
        /// seconds until it can hit: 0 if a hit window is open, infinity if nothing is coming.
        /// </summary>
        float UpdateThreat()
        {
            Component soonest = null;
            int soonestSerial = 0;
            AttackData soonestAttack = null;
            float soonestTime = float.PositiveInfinity;
            foreach (var attacker in CombatControllerGhost.All)
            {
                if (attacker == null || attacker == m_Combat || !attacker.IsServer) continue;
                var attack = attacker.CurrentAttack;
                if (attack == null || (attacker.transform.position - transform.position).sqrMagnitude > SightRange * SightRange) continue;

                float time = TimeToNextHit(attacker, attack);
                if (time < soonestTime)
                {
                    soonestTime = time;
                    soonest = attacker;
                    soonestSerial = attacker.AttackSerial.Value;
                    soonestAttack = attack;
                }
            }
            foreach (var projectile in Projectile.All)
            {
                if (projectile == null || !projectile.IsServer || projectile.Exploded.Value) continue;
                if (projectile.ShooterGhostId.Value == m_Combat.GhostId) continue;
                float time = TimeToImpact(projectile);
                if (time < soonestTime)
                {
                    soonestTime = time;
                    soonest = projectile;
                    // A parried projectile coming back is a new threat.
                    soonestSerial = projectile.ShooterGhostId.Value;
                    soonestAttack = projectile.Attack;
                }
            }

            if (soonest == null)
            {
                m_Threat = null;
                return float.PositiveInfinity;
            }
            if (soonest != m_Threat || soonestSerial != m_ThreatSerial)
            {
                // A new swing or projectile: decide once how to meet it.
                m_Threat = soonest;
                m_ThreatSerial = soonestSerial;
                m_ParryThreat = soonestAttack != null && soonestAttack.CanBeParried && (Defense == DummyDefense.Parry ||
                                                        (Defense == DummyDefense.Mixed && m_Random.NextFloat() < ParryChance));
            }
            return soonestTime;
        }

        /// <summary>Seconds until a projectile reaches the dummy, or infinity if it's out of sight or will miss.</summary>
        float TimeToImpact(Projectile projectile)
        {
            var toDummy = transform.position + Vector3.up - projectile.transform.position;
            if (toDummy.sqrMagnitude > SightRange * SightRange) return float.PositiveInfinity;
            var direction = projectile.Direction.Value;
            float along = Vector3.Dot(toDummy, direction);
            if (along <= 0f || (toDummy - direction * along).magnitude > 1f + projectile.Radius) return float.PositiveInfinity;
            // Measured to the front of the body, about half a meter from its middle.
            return Mathf.Max(0f, along - 0.5f - projectile.Radius) / Mathf.Max(projectile.Speed, 0.01f);
        }

        static float TimeToNextHit(CombatControllerGhost attacker, AttackData attack)
        {
            float elapsed = attacker.AttackElapsed.Value;
            float best = float.PositiveInfinity;
            foreach (var window in attack.HitWindows)
            {
                float start = attack.ToElapsed(window.Start), end = attack.ToElapsed(window.End);
                if (elapsed >= end) continue;
                best = Mathf.Min(best, Mathf.Max(0f, start - elapsed));
            }
            return best;
        }

        AttackKind ChooseAttack(float dt, bool threatened)
        {
            if (!Attack || AttackSequence == null || AttackSequence.Length == 0) return AttackKind.None;

            var current = m_Combat.CurrentAttack;
            if (current == null)
            {
                if (m_SequenceIndex > 0)
                {
                    // The string ended, or was interrupted by a hit or parry.
                    m_SequenceIndex = 0;
                    m_AttackCooldown = AttackInterval;
                }
                m_AttackCooldown -= dt;
                if (m_AttackCooldown > 0f || threatened || m_Combat.IsStunned || !TargetInRange()) return AttackKind.None;
                return Next();
            }

            // Mid-string: press the next button once the combo window opens.
            int serial = m_Combat.AttackSerial.Value;
            if (m_SequenceIndex == 0 || m_SequenceIndex >= AttackSequence.Length || serial == m_PressedForSerial) return AttackKind.None;
            if (!current.InComboWindow(m_Combat.AttackNormalizedTime)) return AttackKind.None;
            if (current.GetFollowUp(AttackSequence[m_SequenceIndex]) == null)
            {
                m_SequenceIndex = AttackSequence.Length; // the combo has no such follow-up; end the string here
                return AttackKind.None;
            }
            return Next();

            AttackKind Next()
            {
                m_PressedForSerial = m_Combat.AttackSerial.Value;
                return AttackSequence[m_SequenceIndex++];
            }
        }

        bool TargetInRange()
        {
            if (m_Target == null) return false;
            var toTarget = m_Target.transform.position - transform.position;
            toTarget.y = 0f;
            return toTarget.sqrMagnitude <= AttackRange * AttackRange;
        }

        CombatControllerGhost FindTarget()
        {
            CombatControllerGhost best = null;
            float bestDistance = SightRange * SightRange;
            foreach (var other in CombatControllerGhost.All)
            {
                if (other == null || other == m_Combat || !other.IsServer || other.Ghost == null || other.Ghost.World != m_Combat.Ghost.World) continue;
                if (other.IsDead || (!TargetDummies && other.TryGetComponent<CombatDummy>(out _))) continue;
                float distance = (other.transform.position - transform.position).sqrMagnitude;
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = other;
                }
            }
            return best;
        }
    }
}
