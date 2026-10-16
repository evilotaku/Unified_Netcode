using System;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Netcode
{
    /// <summary>What happened when an attack connected. Raised on the server by <see cref="CombatControllerGhost.ServerHit"/>.</summary>
    public struct CombatHit
    {
        public CombatControllerGhost Attacker;
        public GameObject Target;
        /// <summary>The body part hit, or null for a non-player object.</summary>
        public Hurtbox Hurtbox;
        public AttackData Attack;
        /// <summary>The target blocked, so it took no damage, knockback or stun.</summary>
        public bool Blocked;
        /// <summary>The target parried: it blocked just in time, and the attacker is staggered. Blocked is also set.</summary>
        public bool Parried;
        public float Damage;
        /// <summary>Where the hitbox touched the target.</summary>
        public Vector3 Point;
    }

    /// <summary>
    /// Combo attacks and blocking for the predicted player ghost. The moves come from a <see cref="ComboSet"/>: each
    /// button starts an attack, and pressing a button during an attack's combo window queues the follow-up that
    /// attack links to that button, which starts at the attack's cancel time.
    /// The attack and block state is in GhostFields and advanced in PredictionUpdate, so the owning client predicts its
    /// own combos and animation. Only the server tests <see cref="Hitbox"/>es against <see cref="Hurtbox"/>es and
    /// applies knockback and hit stun; clients receive them in snapshots. A hit player's FPSControllerGhost adds
    /// <see cref="KnockbackVelocity"/> to its movement. <see cref="CombatEffects"/> plays the attacks' effects.
    /// Put this on the player ghost prefab next to <see cref="FPSControllerGhost"/>.
    /// </summary>
    public partial class CombatControllerGhost : GhostBehaviour
    {
        [Header("Attacks")]
        [Tooltip("The move list. Must be the same asset on the server and every client.")]
        public ComboSet Combos;

        [Header("Special")]
        [Tooltip("Seconds after starting the Special button's attack before it can be started again.")]
        public float SpecialCooldown = 1.5f;
        [Tooltip("How far along the view a projectile is aimed when nothing is under the crosshair. It's fired from " +
                 "below the eye, so it crosses the crosshair line at this distance.")]
        public float ProjectileAimDistance = 60f;

        [Header("Blocking")]
        [Tooltip("Attacks from within this angle in front of the player are blocked.")]
        public float BlockAngle = 120f;
        [Tooltip("Move speed multiplier while blocking.")]
        public float BlockMoveSpeedMultiplier = 0.4f;
        [Tooltip("Server only: block whenever able, regardless of input. For training dummies and bots, not player-owned ghosts.")]
        public bool ServerAlwaysBlock;
        [Tooltip("Server only: with Server Always Block, parry every parryable hit. For testing parries against a dummy.")]
        public bool ServerAlwaysParry;

        [Header("Parry")]
        [Tooltip("Seconds after pressing Block during which a blocked hit is parried instead.")]
        public float ParryWindow = 0.15f;
        [Tooltip("Seconds after a Block press before the next press can parry, so mashing Block doesn't. A successful parry resets it.")]
        public float ParryCooldown = 0.6f;
        [Tooltip("How long a parried attacker can't move, attack or block.")]
        public float ParryStunTime = 0.9f;
        [Tooltip("Horizontal speed a parried attacker is pushed back with.")]
        public float ParryKnockbackSpeed = 3f;

        [Header("Knockback")]
        [Tooltip("How fast received horizontal knockback slows down, in m/s per second.")]
        public float KnockbackDeceleration = 20f;
        [Tooltip("Rigidbodies that are hit get the knockback speed multiplied by this.")]
        public float RigidbodyKnockbackMultiplier = 1f;

        [Header("Input")]
        public InputAction LightAttackAction = Button("LightAttack", "<Mouse>/leftButton", "<Gamepad>/buttonWest");
        public InputAction MediumAttackAction = Button("MediumAttack", "<Mouse>/rightButton", "<Gamepad>/buttonNorth");
        public InputAction HeavyAttackAction = Button("HeavyAttack", "<Keyboard>/f", "<Gamepad>/rightTrigger");
        public InputAction SpecialAttackAction = Button("SpecialAttack", "<Keyboard>/r", "<Gamepad>/rightShoulder");
        public InputAction BlockAction = Button("Block", "<Keyboard>/q", "<Gamepad>/leftShoulder");

        // Synchronized state. Attack ids index Combos (see ComboSet.GetId); 0 is no attack.
        public GhostField<int> AttackId;
        public GhostField<float> AttackElapsed;
        /// <summary>Goes up by one each time an attack starts, so a repeated attack counts as a new swing.</summary>
        public GhostField<int> AttackSerial;
        /// <summary>The follow-up button pressed during the combo window, as an AttackKind, waiting for the cancel time.</summary>
        public GhostField<int> QueuedInput;
        // The attack this one was chained from and how far through it was (0-1), for blending the animation.
        public GhostField<int> PreviousAttackId;
        public GhostField<float> PreviousAttackTime;
        public GhostField<float> SpecialCooldownRemaining;
        public GhostField<bool> IsBlocking;
        /// <summary>Whether Block was held last tick, to find presses.</summary>
        public GhostField<bool> BlockHeld;
        public GhostField<float> ParryWindowRemaining;
        public GhostField<float> ParryCooldownRemaining;
        // Fields only used for effects are quantized to 1 cm (Quantization = 100). Fields the owner predicts with stay
        // unquantized, so its replays match the server exactly.
        // Parries this player made (ParryCount, at ParryPoint) and suffered (ParriedCount), for effects.
        public GhostField<int> ParryCount;
        public GhostField<Vector3> ParryPoint = new(default, new FieldConfig { Quantization = 100 });
        public GhostField<int> ParriedCount;
        public GhostField<Vector3> KnockbackVelocity;
        public GhostField<float> HitStunRemaining;
        // The last hit taken, for client-side reactions such as ActiveRagdoll. HitCount goes up by one per hit. Its
        // hit-stop and camera shake come from LastHitAttackId's AttackData.
        public GhostField<int> HitCount;
        public GhostField<int> LastHitHurtbox;
        public GhostField<int> LastHitAttackId;
        public GhostField<Vector3> LastHitImpulse = new(default, new FieldConfig { Quantization = 100 });
        // The last hit this player dealt, for impact effects. HitConfirmCount goes up by one per target hit.
        public GhostField<int> HitConfirmCount;
        public GhostField<int> HitConfirmAttackId;
        public GhostField<Vector3> HitConfirmPoint = new(default, new FieldConfig { Quantization = 100 });
        public GhostField<bool> HitConfirmBlocked;

        /// <summary>Server only. Raised for each player or rigidbody an attack connects with, blocked or not. Hook damage up here.</summary>
        public static event Action<CombatHit> ServerHit;

        static readonly List<CombatControllerGhost> s_All = new();
        /// <summary>Every enabled combat player, on the server and clients alike.</summary>
        public static IReadOnlyList<CombatControllerGhost> All => s_All;

        FPSControllerGhost m_Controller;
        PlayerInteractor m_Interactor;
        CombatDummy m_Dummy;
        Health m_Health;
        Hitbox[] m_Hitboxes;
        Hurtbox[] m_Hurtboxes;

        // Presses recorded by the input callbacks, consumed by the next PredictionGatherInput.
        // Polling WasPressedThisFrame there misses presses: input gathering runs before the Input System's update.
        bool m_LightPressed;
        bool m_MediumPressed;
        bool m_HeavyPressed;
        bool m_SpecialPressed;

        // Server only: what the current swing has already hit, so each target is hit once per swing.
        readonly HashSet<UnityEngine.Object> m_HitThisSwing = new();
        readonly Dictionary<CombatControllerGhost, (Hurtbox hurtbox, Collider collider)> m_PlayersInHitbox = new();
        readonly Collider[] m_Overlaps = new Collider[32];
        readonly HashSet<Rigidbody> m_PushedBodies = new();
        readonly RaycastHit[] m_AimHits = new RaycastHit[16];

        public AttackData CurrentAttack => Combos != null ? Combos.GetAttack(AttackId.Value) : null;
        public AttackData PreviousAttack => Combos != null ? Combos.GetAttack(PreviousAttackId.Value) : null;
        public bool IsAttacking => CurrentAttack != null;
        public bool IsStunned => HitStunRemaining.Value > 0f;
        /// <summary>Whether this player's <see cref="Health"/> has run out. A dead player can't act or be hit.</summary>
        public bool IsDead => m_Health != null && m_Health.IsDead.Value;
        public Health Health => m_Health;

        /// <summary>This player's hurtboxes. <see cref="LastHitHurtbox"/> indexes into it.</summary>
        public Hurtbox[] Hurtboxes => m_Hurtboxes;
        /// <summary>This player's hitboxes, for attaching effects.</summary>
        public Hitbox[] Hitboxes => m_Hitboxes;

        /// <summary>0 to 1 through the current attack's clip (see <see cref="AttackData.ToClipTime"/>), for driving its animation.</summary>
        public float AttackNormalizedTime
        {
            get
            {
                var attack = CurrentAttack;
                return attack == null ? 0f : attack.ToClipTime(AttackElapsed.Value);
            }
        }

        /// <summary>Multiplier for the player's move speed: 0 while stunned, reduced while attacking or blocking.</summary>
        public float MoveSpeedMultiplier
        {
            get
            {
                if (IsStunned || IsDead) return 0f;
                if (IsBlocking.Value) return BlockMoveSpeedMultiplier;
                var attack = CurrentAttack;
                return attack != null ? attack.MoveSpeedMultiplier : 1f;
            }
        }

        static InputAction Button(string name, params string[] bindings)
        {
            var action = new InputAction(name, InputActionType.Button);
            foreach (var binding in bindings) action.AddBinding(binding);
            return action;
        }

        public override void Awake()
        {
            base.Awake();
            m_Controller = GetComponent<FPSControllerGhost>();
            m_Interactor = GetComponent<PlayerInteractor>();
            m_Dummy = GetComponent<CombatDummy>();
            m_Health = GetComponent<Health>();
            m_Hitboxes = GetComponentsInChildren<Hitbox>(true);
            m_Hurtboxes = GetComponentsInChildren<Hurtbox>(true);
            if (Combos == null) Debug.LogWarning($"{name}: CombatControllerGhost has no Combo Set, so it can't attack.", this);
        }

        void OnEnable()
        {
            s_All.Add(this);
            LightAttackAction.performed += OnLightPerformed;
            MediumAttackAction.performed += OnMediumPerformed;
            HeavyAttackAction.performed += OnHeavyPerformed;
            SpecialAttackAction.performed += OnSpecialPerformed;
            LightAttackAction.Enable();
            MediumAttackAction.Enable();
            HeavyAttackAction.Enable();
            SpecialAttackAction.Enable();
            BlockAction.Enable();
        }

        void OnDisable()
        {
            s_All.Remove(this);
            LightAttackAction.performed -= OnLightPerformed;
            MediumAttackAction.performed -= OnMediumPerformed;
            HeavyAttackAction.performed -= OnHeavyPerformed;
            SpecialAttackAction.performed -= OnSpecialPerformed;
            LightAttackAction.Disable();
            MediumAttackAction.Disable();
            HeavyAttackAction.Disable();
            SpecialAttackAction.Disable();
            BlockAction.Disable();
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            // OnDisable doesn't always run first, such as for an Editor preview copy whose Awake failed.
            s_All.Remove(this);
        }

        void OnLightPerformed(InputAction.CallbackContext _) => m_LightPressed = true;
        void OnMediumPerformed(InputAction.CallbackContext _) => m_MediumPressed = true;
        void OnHeavyPerformed(InputAction.CallbackContext _) => m_HeavyPressed = true;
        void OnSpecialPerformed(InputAction.CallbackContext _) => m_SpecialPressed = true;

        public override void PredictionGatherInput(PredictionGatherInputContext context)
        {
            if (m_Controller == null) return;

            var input = m_Controller.InputComponent.Value;
            input.LightAttack = default;
            input.MediumAttack = default;
            input.HeavyAttack = default;
            input.SpecialAttack = default;

            // The left mouse button throws a carried item, so don't attack or block while carrying.
            bool carrying = m_Interactor != null && m_Interactor.IsLocallyCarrying;
            if (!carrying)
            {
                if (m_LightPressed) input.LightAttack.Set();
                if (m_MediumPressed) input.MediumAttack.Set();
                if (m_HeavyPressed) input.HeavyAttack.Set();
                if (m_SpecialPressed) input.SpecialAttack.Set();
            }
            m_LightPressed = m_MediumPressed = m_HeavyPressed = m_SpecialPressed = false;
            input.Block = !carrying && BlockAction.IsPressed();

            m_Controller.InputComponent.Value = input;
        }

        public override void PredictionUpdate(PredictionUpdateContext context)
        {
            if (m_Controller == null) return;
            float dt = context.TickedDeltaTime;

            // Received knockback and hit stun wear off over time.
            var knockback = KnockbackVelocity.Value;
            if (knockback != Vector3.zero)
                KnockbackVelocity.Value = Vector3.MoveTowards(knockback, Vector3.zero, KnockbackDeceleration * dt);
            if (HitStunRemaining.Value > 0f)
                HitStunRemaining.Value = Mathf.Max(0f, HitStunRemaining.Value - dt);
            if (ParryWindowRemaining.Value > 0f)
                ParryWindowRemaining.Value = Mathf.Max(0f, ParryWindowRemaining.Value - dt);
            if (ParryCooldownRemaining.Value > 0f)
                ParryCooldownRemaining.Value = Mathf.Max(0f, ParryCooldownRemaining.Value - dt);
            if (SpecialCooldownRemaining.Value > 0f)
                SpecialCooldownRemaining.Value = Mathf.Max(0f, SpecialCooldownRemaining.Value - dt);

            var input = m_Controller.InputComponent.Value;
            // A dummy has no client sending input; its brain presses the buttons on the server instead.
            if (IsServer && m_Dummy != null && m_Dummy.isActiveAndEnabled) m_Dummy.DriveCombat(ref input, dt);
            var pressed = input.SpecialAttack.IsSet ? AttackKind.Special
                : input.HeavyAttack.IsSet ? AttackKind.Heavy
                : input.MediumAttack.IsSet ? AttackKind.Medium
                : input.LightAttack.IsSet ? AttackKind.Light
                : AttackKind.None;
            bool blockPressed = input.Block && !BlockHeld.Value;
            BlockHeld.Value = input.Block;

            if (IsAttacking)
            {
                IsBlocking.Value = false;
                ParryWindowRemaining.Value = 0f;
                AdvanceAttack(dt, pressed);
                return;
            }

            IsBlocking.Value = (input.Block || (IsServer && ServerAlwaysBlock)) && CanAct();
            if (!IsBlocking.Value)
            {
                ParryWindowRemaining.Value = 0f;
            }
            else if (blockPressed && ParryCooldownRemaining.Value <= 0f)
            {
                // A fresh Block press opens the parry window. Holding Block, or pressing it during an attack or
                // stun, doesn't.
                ParryWindowRemaining.Value = ParryWindow;
                ParryCooldownRemaining.Value = ParryCooldown;
            }
            if (IsBlocking.Value || !CanAct() || pressed == AttackKind.None || Combos == null) return;

            if (pressed == AttackKind.Special && SpecialCooldownRemaining.Value > 0f) return;
            var starter = Combos.GetStarter(pressed);
            if (starter == null) return;
            StartAttack(starter, null, 0f);
            if (pressed == AttackKind.Special) SpecialCooldownRemaining.Value = SpecialCooldown;
        }

        /// <summary>Whether the player may start a dash: not mid-attack or stunned.</summary>
        public bool CanDash => !IsStunned && !IsAttacking && !IsDead;

        bool CanAct()
        {
            if (IsStunned || IsDead) return false;
            // No attacking or blocking until a dash's animation has played out.
            if (m_Controller != null && m_Controller.IsDashing) return false;
            // The server knows what the player carries; the owning client already skips combat input while carrying.
            if (IsServer && m_Interactor != null && m_Interactor.HeldItem != null) return false;
            return true;
        }

        void StartAttack(AttackData attack, AttackData chainedFrom, float chainedFromTime)
        {
            AttackId.Value = Combos.GetId(attack);
            AttackElapsed.Value = 0f;
            AttackSerial.Value++;
            QueuedInput.Value = (int)AttackKind.None;
            PreviousAttackId.Value = Combos.GetId(chainedFrom);
            PreviousAttackTime.Value = chainedFromTime;
            if (IsServer) m_HitThisSwing.Clear();
        }

        void EndAttack()
        {
            AttackId.Value = 0;
            AttackElapsed.Value = 0f;
            QueuedInput.Value = (int)AttackKind.None;
            PreviousAttackId.Value = 0;
        }

        void AdvanceAttack(float dt, AttackKind pressed)
        {
            var attack = CurrentAttack;
            float before = attack.ToClipTime(AttackElapsed.Value);
            float elapsed = AttackElapsed.Value + dt;
            float after = attack.ToClipTime(elapsed);

            // A press in the combo window queues the follow-up for that button; a later press replaces it.
            if (pressed != AttackKind.None && attack.InComboWindow(after) && attack.GetFollowUp(pressed) != null)
                QueuedInput.Value = (int)pressed;

            // Hits are only checked on the server, which runs each tick once. Clients see the result in snapshots.
            // A window counts if it overlaps this tick, so a window shorter than a tick isn't skipped.
            if (IsServer)
            {
                foreach (var window in attack.HitWindows)
                {
                    if (window.Start <= after && window.End > before)
                        ServerCheckHits(attack, window.Hitbox);
                }
                if (!IsAttacking) return; // parried: the attack is over
            }

            // The tick that passes Projectile Time fires it. Elapsed starts at 0, so a time of 0 fires on the first tick.
            if (attack.ProjectilePrefab != null && before <= attack.ProjectileTime && after > attack.ProjectileTime)
                FireProjectile(attack);

            var queued = (AttackKind)QueuedInput.Value;
            if (queued != AttackKind.None && after >= attack.CancelTime)
            {
                StartAttack(attack.GetFollowUp(queued), attack, Mathf.Clamp01(after));
                return;
            }

            if (elapsed >= attack.TotalDuration)
                EndAttack();
            else
                AttackElapsed.Value = elapsed;
        }

        /// <summary>
        /// Fires the attack's projectile straight ahead. The server spawns the real one; the owning client predict-spawns
        /// its own copy the first time it simulates this tick, so it appears straight away. Netcode then matches that copy
        /// to the server's by prefab and spawn tick. Rollback replays of the tick don't spawn it again.
        /// </summary>
        void FireProjectile(AttackData attack)
        {
            if (!IsServer && !Ghost.World.NetworkTime.IsFirstTimeFullyPredictingTick) return;
            var forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return;
            var yaw = Quaternion.LookRotation(forward.normalized);
            var origin = transform.position + yaw * attack.ProjectileOffset;
            var direction = ProjectileAimDirection(yaw, origin);
            var projectile = Instantiate(attack.ProjectilePrefab, origin, Quaternion.LookRotation(direction));
            projectile.Launch(this, Combos.GetId(attack), origin, direction);
        }

        /// <summary>
        /// Where a projectile fired from <paramref name="origin"/> should head to land under the crosshair. The look ray
        /// starts at the eye (the controller's CameraTarget) and uses the view pitch. The projectile is aimed at the first
        /// solid thing on that ray, or at the point Projectile Aim Distance along it, so it converges on the crosshair
        /// even though it's fired from below the eye.
        /// </summary>
        Vector3 ProjectileAimDirection(Quaternion yaw, Vector3 origin)
        {
            float pitch = m_Controller != null ? m_Controller.ViewPitch.Value : 0f;
            var look = yaw * Quaternion.Euler(pitch, 0f, 0f) * Vector3.forward;
            var eyeLocal = m_Controller != null && m_Controller.CameraTarget != null ? m_Controller.CameraTarget.localPosition : new Vector3(0f, 1.65f, 0f);
            var eye = transform.position + yaw * eyeLocal;

            var aimPoint = eye + look * ProjectileAimDistance;
            int count = UnityEngine.Physics.RaycastNonAlloc(eye, look, m_AimHits, ProjectileAimDistance, HitMask(), QueryTriggerInteraction.Collide);
            float nearest = ProjectileAimDistance;
            for (int i = 0; i < count; i++)
            {
                var hit = m_AimHits[i];
                if (hit.distance >= nearest || hit.collider.transform.IsChildOf(transform)) continue;
                // Other players are aimed at through their hurtboxes; other triggers are ignored.
                if (hit.collider.isTrigger && !hit.collider.TryGetComponent<Hurtbox>(out _)) continue;
                nearest = hit.distance;
                aimPoint = hit.point;
            }

            // Too close to converge on (such as the floor at your feet): fire along the look direction instead.
            var toAim = aimPoint - origin;
            if (toAim.sqrMagnitude < 1f || Vector3.Dot(toAim, look) <= 0f) return look;
            return toAim.normalized;
        }

        /// <summary>This player's ghost id, the same on the server and every client. 0 before it's spawned.</summary>
        public int GhostId
        {
            get
            {
                var ghost = Ghost;
                if (ghost == null || !ghost.IsSpawned) return 0;
                var entityManager = ghost.World.EntityManager;
                return entityManager.HasComponent<GhostInstance>(ghost.Entity) ? entityManager.GetComponentData<GhostInstance>(ghost.Entity).ghostId : 0;
            }
        }

        /// <summary>The combat player with the given <see cref="GhostId"/> in the same world as <paramref name="near"/>, or null.</summary>
        public static CombatControllerGhost FindByGhostId(int ghostId, GhostBehaviour near)
        {
            if (ghostId == 0) return null;
            foreach (var player in s_All)
            {
                if (player.IsServer == near.IsServer && player.IsClient == near.IsClient && player.GhostId == ghostId)
                    return player;
            }
            return null;
        }

        /// <summary>Whether a hitbox is inside one of the current attack's hit windows.</summary>
        bool IsHitboxActive(Hitbox hitbox)
        {
            var attack = CurrentAttack;
            if (attack == null) return false;
            float t = AttackNormalizedTime;
            foreach (var window in attack.HitWindows)
            {
                if (t >= window.Start && t < window.End && (string.IsNullOrEmpty(window.Hitbox) || window.Hitbox == hitbox.Name))
                    return true;
            }
            return false;
        }

        void Update()
        {
            // Visual only: show active hitboxes' trigger colliders. Hit tests don't depend on this.
            foreach (var hitbox in m_Hitboxes)
            {
                bool active = IsHitboxActive(hitbox);
                if (hitbox.Active != active) hitbox.Active = active;
            }
        }

        #region Server

        internal int HitMask()
        {
            // Matches FPSControllerGhost's layers: a server-only copy is on ServerWorld, everything else isn't.
            int serverLayer = LayerMask.NameToLayer("ServerWorld");
            if (serverLayer < 0) return UnityEngine.Physics.DefaultRaycastLayers;
            return IsServer && !IsClient ? 1 << serverLayer : UnityEngine.Physics.DefaultRaycastLayers & ~(1 << serverLayer);
        }

        void ServerCheckHits(AttackData attack, string hitboxName)
        {
            // Hurtboxes move with the animation; make sure the physics scene has this tick's poses.
            UnityEngine.Physics.SyncTransforms();

            foreach (var hitbox in m_Hitboxes)
            {
                if (!string.IsNullOrEmpty(hitboxName) && hitbox.Name != hitboxName) continue;

                var center = hitbox.WorldCenter;
                int count = UnityEngine.Physics.OverlapSphereNonAlloc(center, hitbox.WorldRadius, m_Overlaps, HitMask(), QueryTriggerInteraction.Collide);
                m_PlayersInHitbox.Clear();
                for (int i = 0; i < count; i++)
                {
                    var hit = m_Overlaps[i];
                    if (hit.transform.IsChildOf(transform)) continue;

                    // Players are hit through their hurtboxes; keep the most damaging part this hitbox touches.
                    if (hit.TryGetComponent<Hurtbox>(out var hurtbox))
                    {
                        var victim = hurtbox.Owner;
                        if (victim == null || !victim.IsServer || victim.IsDead || m_HitThisSwing.Contains(victim)) continue;
                        if (!m_PlayersInHitbox.TryGetValue(victim, out var best) || hurtbox.DamageMultiplier > best.hurtbox.DamageMultiplier)
                            m_PlayersInHitbox[victim] = (hurtbox, hit);
                        continue;
                    }

                    // Other physics objects, such as crates, get pushed.
                    if (hit.isTrigger || hit.GetComponentInParent<CombatControllerGhost>() != null) continue;
                    var body = hit.attachedRigidbody;
                    if (body == null || body.isKinematic || !m_HitThisSwing.Add(body)) continue;
                    var push = transform.forward * attack.KnockbackSpeed + Vector3.up * attack.KnockbackUpSpeed;
                    body.AddForce(push * RigidbodyKnockbackMultiplier, ForceMode.VelocityChange);
                    var bodyPoint = hit.ClosestPoint(center);
                    ServerConfirmHit(bodyPoint, false);
                    ServerHit?.Invoke(new CombatHit { Attacker = this, Target = body.gameObject, Attack = attack, Damage = attack.Damage, Point = bodyPoint });
                }

                foreach (var (victim, (hurtbox, collider)) in m_PlayersInHitbox)
                {
                    m_HitThisSwing.Add(victim);
                    var point = collider.ClosestPoint(center);
                    var result = new CombatHit { Attacker = this, Target = victim.gameObject, Hurtbox = hurtbox, Attack = attack, Point = point };
                    var block = victim.ServerBlocks(transform.position, attack);
                    if (block == BlockResult.Parried)
                    {
                        result.Blocked = result.Parried = true;
                        victim.ServerParry(this, point);
                        ServerHit?.Invoke(result);
                        return; // the parry ends this attack, so nothing else is hit
                    }
                    if (block == BlockResult.Blocked)
                    {
                        result.Blocked = true;
                    }
                    else
                    {
                        var away = victim.transform.position - transform.position;
                        away.y = 0f;
                        if (away.sqrMagnitude < 0.0001f) away = transform.forward;
                        victim.ServerReceiveHit(away.normalized, attack, hurtbox);
                        result.Damage = attack.Damage * hurtbox.DamageMultiplier;
                    }
                    ServerConfirmHit(point, result.Blocked);
                    ServerHit?.Invoke(result);
                }
            }
        }

        /// <summary>Server only. Records a hit this player dealt, so clients play its impact effects.</summary>
        void ServerConfirmHit(Vector3 point, bool blocked) => ServerConfirmHit(point, blocked, AttackId.Value);

        void ServerConfirmHit(Vector3 point, bool blocked, int attackId)
        {
            HitConfirmCount.Value++;
            HitConfirmAttackId.Value = attackId;
            HitConfirmPoint.Value = point;
            HitConfirmBlocked.Value = blocked;
        }

        /// <summary>
        /// Server only. A projectile from this player's <paramref name="attack"/> exploded at <paramref name="point"/>:
        /// hit every other player and push every rigidbody within <paramref name="radius"/>, away from the point.
        /// Players facing the explosion with their guard up block it. Explosions can't be parried.
        /// </summary>
        internal void ServerExplosion(AttackData attack, Vector3 point, float radius)
        {
            UnityEngine.Physics.SyncTransforms();
            int count = UnityEngine.Physics.OverlapSphereNonAlloc(point, radius, m_Overlaps, HitMask(), QueryTriggerInteraction.Collide);
            m_PlayersInHitbox.Clear();
            m_PushedBodies.Clear();
            for (int i = 0; i < count; i++)
            {
                var hit = m_Overlaps[i];
                if (hit.transform.IsChildOf(transform)) continue;

                // Keep the most damaging part of each player within the radius.
                if (hit.TryGetComponent<Hurtbox>(out var hurtbox))
                {
                    var victim = hurtbox.Owner;
                    if (victim == null || victim == this || !victim.IsServer || victim.IsDead) continue;
                    if (!m_PlayersInHitbox.TryGetValue(victim, out var best) || hurtbox.DamageMultiplier > best.hurtbox.DamageMultiplier)
                        m_PlayersInHitbox[victim] = (hurtbox, hit);
                    continue;
                }

                if (hit.isTrigger || hit.GetComponentInParent<CombatControllerGhost>() != null) continue;
                var body = hit.attachedRigidbody;
                if (body == null || body.isKinematic || !m_PushedBodies.Add(body)) continue;
                var away = body.worldCenterOfMass - point;
                away.y = 0f;
                var push = (away.sqrMagnitude > 0.0001f ? away.normalized : Vector3.zero) * attack.KnockbackSpeed + Vector3.up * attack.KnockbackUpSpeed;
                body.AddForce(push * RigidbodyKnockbackMultiplier, ForceMode.VelocityChange);
                ServerHit?.Invoke(new CombatHit { Attacker = this, Target = body.gameObject, Attack = attack, Damage = attack.Damage, Point = hit.ClosestPoint(point) });
            }

            int attackId = Combos != null ? Combos.GetId(attack) : 0;
            foreach (var (victim, (hurtbox, collider)) in m_PlayersInHitbox)
            {
                var hitPoint = collider.ClosestPoint(point);
                var result = new CombatHit { Attacker = this, Target = victim.gameObject, Hurtbox = hurtbox, Attack = attack, Point = hitPoint };
                if (victim.ServerBlocks(point, attack) != BlockResult.None)
                {
                    result.Blocked = true;
                }
                else
                {
                    var away = victim.transform.position - point;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.0001f) away = victim.transform.forward * -1f;
                    victim.ServerReceiveHit(away.normalized, attack, hurtbox);
                    result.Damage = attack.Damage * hurtbox.DamageMultiplier;
                }
                ServerConfirmHit(hitPoint, result.Blocked, attackId);
                ServerHit?.Invoke(result);
            }
        }

        enum BlockResult { None, Blocked, Parried }

        /// <summary>
        /// Whether this player blocks an attack from that position: it must be blocking and facing the attacker. A block
        /// inside the parry window parries. Decided on the server; clients use it only to predict projectile parries.
        /// </summary>
        BlockResult ServerBlocks(Vector3 attackerPosition, AttackData attack)
        {
            if (!IsBlocking.Value) return BlockResult.None;
            var toAttacker = attackerPosition - transform.position;
            toAttacker.y = 0f;
            bool facing = toAttacker.sqrMagnitude < 0.0001f || Vector3.Angle(transform.forward, toAttacker) <= BlockAngle * 0.5f;
            if (!facing) return BlockResult.None;
            bool inWindow = ParryWindowRemaining.Value > 0f || (IsServer && ServerAlwaysBlock && ServerAlwaysParry);
            return attack.CanBeParried && inWindow ? BlockResult.Parried : BlockResult.Blocked;
        }

        /// <summary>
        /// Whether this player parries a projectile from <paramref name="attack"/> coming from
        /// <paramref name="projectilePosition"/>. On a client it's a prediction from this player's latest state.
        /// </summary>
        internal bool ParriesProjectile(Vector3 projectilePosition, AttackData attack) =>
            ServerBlocks(projectilePosition, attack) == BlockResult.Parried;

        /// <summary>Server only. This player parried a projectile at <paramref name="point"/> and sent it back.</summary>
        internal void ServerParryProjectile(Vector3 point) => ServerRecordParry(point);

        /// <summary>Server only. Records a parry this player made, for its effects, and lets it parry again.</summary>
        void ServerRecordParry(Vector3 point)
        {
            ParryCount.Value++;
            ParryPoint.Value = point;
            // A clean parry is rewarded with being able to parry again straight away.
            ParryWindowRemaining.Value = 0f;
            ParryCooldownRemaining.Value = 0f;
        }

        /// <summary>Server only. This player parried <paramref name="attacker"/>: stagger it and push it back.</summary>
        void ServerParry(CombatControllerGhost attacker, Vector3 point)
        {
            ServerRecordParry(point);

            var away = attacker.transform.position - transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = -attacker.transform.forward;
            attacker.ParriedCount.Value++;
            attacker.KnockbackVelocity.Value = away.normalized * ParryKnockbackSpeed;
            attacker.HitStunRemaining.Value = Mathf.Max(attacker.HitStunRemaining.Value, ParryStunTime);
            attacker.EndAttack();
            attacker.IsBlocking.Value = false;
        }

        /// <summary>Server only. Ends this player's attack, guard and dash, such as when it dies.</summary>
        internal void ServerInterrupt()
        {
            EndAttack();
            IsBlocking.Value = false;
            ParryWindowRemaining.Value = 0f;
            if (m_Controller != null) m_Controller.CancelDash();
        }

        /// <summary>Server only. Knocks this player back, stuns it and interrupts its attack or combo.</summary>
        internal void ServerReceiveHit(Vector3 direction, AttackData attack, Hurtbox hurtbox)
        {
            HitCount.Value++;
            LastHitHurtbox.Value = Array.IndexOf(m_Hurtboxes, hurtbox);
            LastHitImpulse.Value = direction * attack.KnockbackSpeed + Vector3.up * attack.KnockbackUpSpeed;
            // Every player uses the same ComboSet, so the attacker's attack has the same id in this player's.
            LastHitAttackId.Value = Combos != null ? Combos.GetId(attack) : 0;

            KnockbackVelocity.Value = direction * attack.KnockbackSpeed;
            HitStunRemaining.Value = Mathf.Max(HitStunRemaining.Value, attack.HitStunTime);
            EndAttack();
            IsBlocking.Value = false;
            if (m_Controller != null) m_Controller.CancelDash();

            // Upward knockback goes into the movement velocity so gravity brings the player back down.
            if (attack.KnockbackUpSpeed > 0f && m_Controller != null)
            {
                var velocity = m_Controller.PredictedVelocity.Value;
                velocity.y = Mathf.Max(velocity.y, attack.KnockbackUpSpeed);
                m_Controller.PredictedVelocity.Value = velocity;
            }
        }

        #endregion
    }
}
