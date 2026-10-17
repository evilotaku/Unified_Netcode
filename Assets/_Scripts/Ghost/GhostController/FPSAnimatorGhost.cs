using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Unity.Netcode;
using Unity.Mathematics;
using Unity.Entities;

namespace Unity.Netcode
{
    public partial class FPSAnimatorGhost : GhostBehaviour
    {
        [Header("Parameters to Sync")]
        public string SpeedParam = "Speed";
        public string GroundedParam = "Grounded";
        public string JumpParam = "Jump";
        public string MotionSpeedParam = "MotionSpeed";
        public string AttackTypeParam = "AttackType";
        public string AttackTimeParam = "AttackTime";
        public string StunnedParam = "Stunned";
        public string BlockingParam = "Blocking";

        [Header("Dash")]
        [Tooltip("Played over a dash in each direction, stretched to the controller's Dash Animation Time. Use in-place clips.")]
        public AnimationClip DashForwardClip;
        public AnimationClip DashBackClip;
        public AnimationClip DashLeftClip;
        public AnimationClip DashRightClip;
        public float DashBlendInTime = 0.05f;
        public float DashBlendOutTime = 0.12f;

        [Header("References")]
        public Animator TargetAnimator;
        public FPSControllerGhost Controller;
        public CombatControllerGhost Combat;

        // --- Synchronized Network State ---
        // Locomotion speed isn't synced separately: it's the horizontal part of the controller's PredictedVelocity.
        public GhostField<bool> NetworkGrounded;

        // Set by a jump and cleared once the graph has evaluated it (the same tick on the server, the next frame on the
        // owning client), so it's never in a snapshot.
        private bool _jumped;

        private PlayableGraph _graph;
        private AnimatorControllerPlayable _controllerPlayable;

        // Attack clips from AttackData play over the controller: mixer input 0 is the controller, 1 the attack a combo
        // came from (held at the time it was cancelled, while the next blends in), 2 the current attack. 3 is the dash.
        private const int ControllerInput = 0, PreviousAttackInput = 1, CurrentAttackInput = 2, DashInput = 3;
        private AnimationMixerPlayable _mixer;
        private readonly AnimationClip[] _clips = new AnimationClip[4];

        // Hit-stop: the pose holds until this time. Visual only; the simulation keeps running.
        private float _hitStopUntil;

        // Remote players' attack and dash times. Snapshots arrive a few ticks apart, and sometimes in bursts, so the
        // replicated times step; these clocks advance every frame instead. They restart with each new swing or dash.
        private RemoteClock _attackClock, _dashClock;
        private bool _remote; // ApplyParametersToGraph is running for a remote player, from Update

        private struct RemoteClock
        {
            // Behind the replicated time means the clock is late, since the replicated time is a past snapshot: it
            // catches up over a few frames. Further off than MaxDrift, such as after a hitch, it jumps.
            private const float CatchUpRate = 15f;
            private const float MaxDrift = 0.25f;
            private int _key;
            private bool _started;
            public float Value;

            public void Advance(int key, float replicated, float deltaTime)
            {
                if (!_started || key != _key)
                {
                    _started = true;
                    _key = key;
                    Value = replicated;
                    return;
                }
                Value += deltaTime;
                float behind = replicated - Value;
                if (Mathf.Abs(behind) > MaxDrift) Value = replicated;
                else if (behind > 0f) Value += behind * Mathf.Min(1f, deltaTime * CatchUpRate);
            }
        }

        private float AttackElapsed => _remote ? _attackClock.Value : Combat.AttackElapsed.Value;

        private float AttackNormalizedTime
        {
            get
            {
                var attack = Combat.CurrentAttack;
                return attack == null ? 0f : attack.ToClipTime(AttackElapsed);
            }
        }

        private float DashElapsed => _remote ? _dashClock.Value : Controller.DashTime.Value;

        // Whether this client owns the ghost. GhostOwnerIsLocal is on every owned ghost and only enabled on the owner's
        // client, so it has to be checked for enabled, not just present.
        public bool IsLocalOwner
        {
            get
            {
                if (Ghost == null || !(Ghost.IsClient || Ghost.IsServer)) return false;
                var entityManager = Ghost.World.EntityManager;
                return entityManager.HasComponent<GhostOwnerIsLocal>(Ghost.Entity) &&
                       entityManager.IsComponentEnabled<GhostOwnerIsLocal>(Ghost.Entity);
            }
        }

        public override void Awake()
        {
            base.Awake();

            if (TargetAnimator == null) TargetAnimator = GetComponentInChildren<Animator>();
            if (Controller == null) Controller = GetComponent<FPSControllerGhost>();
            if (Combat == null) Combat = GetComponent<CombatControllerGhost>();

            if (TargetAnimator == null || TargetAnimator.runtimeAnimatorController == null) return;

            // 1. Setup the Graph
            _graph = PlayableGraph.Create("MecanimPredictor_" + name);

            // Manual: the graph only advances when Evaluate is called (per tick on the server, per frame on clients).
            // With GameTime, Unity also advances a playing graph by itself every frame, which ran animations at about
            // twice their speed and kept hit-stop from freezing anything.
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            // 2. Setup Animator
            TargetAnimator.updateMode = AnimatorUpdateMode.Normal;
            TargetAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            _controllerPlayable = AnimatorControllerPlayable.Create(_graph, TargetAnimator.runtimeAnimatorController);

            // With its controller still assigned, the Animator also runs its own graph, which never gets the parameters
            // and overwrites this graph's pose every frame. This graph has its own copy of the controller.
            TargetAnimator.runtimeAnimatorController = null;

            _mixer = AnimationMixerPlayable.Create(_graph, 4);
            _graph.Connect(_controllerPlayable, 0, _mixer, ControllerInput);
            _mixer.SetInputWeight(ControllerInput, 1f);

            var output = AnimationPlayableOutput.Create(_graph, "Animation", TargetAnimator);
            output.SetSourcePlayable(_mixer);

            _graph.Play();
        }

        public override void PredictionUpdate(PredictionUpdateContext context)
        {
            // A. Update parameters from physics (Server and Owner only)
            if (Controller != null)
            {
                NetworkGrounded.Value = Controller.IsGrounded;

                // The owning client replays ticks, so only count a jump the first time its tick is predicted in full.
                var input = Controller.InputComponent.Value;
                if (input.Jump.IsSet && (IsServer || Ghost.World.NetworkTime.IsFirstTimeFullyPredictingTick)) _jumped = true;
            }

            // The graph isn't rolled back, so a client that replays ticks would play its animation too fast. Clients
            // evaluate it once per frame in Update instead. The server runs each tick once and evaluates it here,
            // because hit detection uses the animated bones of every tick.
            if (!IsServer) return;

            // B. Apply synchronized parameters to the State Machine
            ApplyParametersToGraph();

            // C. Step the animation
            if (!InHitStop && _graph.IsValid()) _graph.Evaluate(context.TickedDeltaTime);

            // Reset the Jump flag locally after evaluation
            _jumped = false;
        }

        /// <summary>Horizontal speed, for locomotion. Interpolated on remote players, from PredictedVelocity.</summary>
        private float Speed
        {
            get
            {
                if (Controller == null) return 0f;
                var velocity = Controller.PredictedVelocity.Value;
                return new Vector2(velocity.x, velocity.z).magnitude;
            }
        }

        private void Update()
        {
            // The server evaluates per tick in PredictionUpdate.
            if (IsServer || !_graph.IsValid()) return;
            float deltaTime = Time.deltaTime;

            if (IsLocalOwner)
            {
                // The owning client: its predicted values are current, so they're used as they are. Update runs before
                // this frame's prediction, so this shows last frame's state, and the ragdoll (LateUpdate) reads a finished pose.
                ApplyParametersToGraph();
                if (!InHitStop) _graph.Evaluate(deltaTime);
                _jumped = false;
                return;
            }

            // Remote players (interpolated) don't run PredictionUpdate. Their times come from snapshots, smoothed by clocks.
            if (Combat != null) _attackClock.Advance(Combat.AttackSerial.Value, Combat.AttackElapsed.Value, deltaTime);
            if (Controller != null) _dashClock.Advance(Controller.DashCount.Value, Controller.DashTime.Value, deltaTime);
            _remote = true;
            ApplyParametersToGraph();
            _remote = false;
            if (!InHitStop) _graph.Evaluate(deltaTime);
        }

        private void ApplyParametersToGraph()
        {
            if (!_controllerPlayable.IsValid()) return;

            float speed = Speed;
            _controllerPlayable.SetFloat(SpeedParam, speed);
            _controllerPlayable.SetBool(GroundedParam, NetworkGrounded.Value);

            // MotionSpeed is the locomotion blend tree's speed multiplier, so it must stay above 0 or idle freezes.
            _controllerPlayable.SetFloat(MotionSpeedParam, speed > 0.1f ? 0.5f : 1f);

            // Apply Jump state
            _controllerPlayable.SetBool(JumpParam, _jumped);

            // Attacks: the attack states use AttackTime as their motion time, so the pose follows the replicated
            // attack progress and stays correct when prediction rolls back.
            if (Combat != null)
            {
                var attack = Combat.CurrentAttack;
                bool viaAnimator = attack != null && (!attack.PlayClipDirectly || attack.Clip == null);
                _controllerPlayable.SetInteger(AttackTypeParam, viaAnimator ? attack.AnimatorAttackType : 0);
                _controllerPlayable.SetFloat(AttackTimeParam, AttackNormalizedTime);
                _controllerPlayable.SetBool(StunnedParam, Combat.IsStunned);
                _controllerPlayable.SetBool(BlockingParam, Combat.IsBlocking.Value);
                ApplyAttackClips(attack);
            }
        }

        /// <summary>Freezes the pose for a moment when an attack lands. Visual only.</summary>
        public void HitStop(float seconds)
        {
            if (seconds > 0f) _hitStopUntil = Mathf.Max(_hitStopUntil, Time.unscaledTime + seconds);
        }

        private bool InHitStop => Time.unscaledTime < _hitStopUntil;

        /// <summary>
        /// Poses the current attack's clip, and the one it was chained from, from the attack time, so the pose is right
        /// after a rollback. The current attack blends in over its BlendInTime and out over its BlendOutTime.
        /// </summary>
        private void ApplyAttackClips(AttackData attack)
        {
            if (!_mixer.IsValid()) return;

            float currentWeight = 0f, previousWeight = 0f;
            if (attack != null && attack.PlayClipDirectly && attack.Clip != null)
            {
                float elapsed = AttackElapsed;
                float blendIn = attack.BlendInTime > 0f ? Mathf.Clamp01(elapsed / attack.BlendInTime) : 1f;
                float blendOut = attack.BlendOutTime > 0f ? Mathf.Clamp01((attack.TotalDuration - elapsed) / attack.BlendOutTime) : 1f;
                currentWeight = Mathf.Min(blendIn, blendOut);
                SetClip(CurrentAttackInput, attack.Clip, AttackNormalizedTime);

                // While a chained attack blends in, the attack it came from fills the rest instead of locomotion.
                var previous = Combat.PreviousAttack;
                if (blendIn < 1f && previous != null && previous.PlayClipDirectly && previous.Clip != null)
                {
                    previousWeight = 1f - blendIn;
                    SetClip(PreviousAttackInput, previous.Clip, Combat.PreviousAttackTime.Value);
                }
            }

            // The dash fills what the attacks leave, so an attack started as a dash's animation ends takes over.
            float dashWeight = Mathf.Min(DashWeight(), 1f - currentWeight - previousWeight);

            _mixer.SetInputWeight(CurrentAttackInput, currentWeight);
            _mixer.SetInputWeight(PreviousAttackInput, previousWeight);
            _mixer.SetInputWeight(DashInput, dashWeight);
            _mixer.SetInputWeight(ControllerInput, 1f - currentWeight - previousWeight - dashWeight);
        }

        /// <summary>Poses the dash clip from the dash time, and returns how much of it to show.</summary>
        private float DashWeight()
        {
            if (Controller == null || !Controller.IsDashing) return 0f;
            var clip = (DashDirection)Controller.DashDirectionState.Value switch
            {
                DashDirection.Forward => DashForwardClip,
                DashDirection.Back => DashBackClip,
                DashDirection.Left => DashLeftClip,
                DashDirection.Right => DashRightClip,
                _ => null,
            };
            if (clip == null) return 0f;

            float elapsed = DashElapsed;
            float length = Controller.DashAnimationTime;
            float remaining = length - elapsed;
            float blendIn = DashBlendInTime > 0f ? Mathf.Clamp01(elapsed / DashBlendInTime) : 1f;
            float blendOut = DashBlendOutTime > 0f ? Mathf.Clamp01(remaining / DashBlendOutTime) : 1f;
            SetClip(DashInput, clip, length > 0f ? Mathf.Clamp01(elapsed / length) : 1f);
            return Mathf.Min(blendIn, blendOut);
        }

        private void SetClip(int input, AnimationClip clip, float normalizedTime)
        {
            if (_clips[input] != clip)
            {
                var old = _mixer.GetInput(input);
                if (old.IsValid())
                {
                    _graph.Disconnect(_mixer, input);
                    old.Destroy();
                }
                var playable = AnimationClipPlayable.Create(_graph, clip);
                // Time is set from the attack state each update rather than advanced by Evaluate.
                playable.SetSpeed(0);
                playable.SetApplyFootIK(false);
                _graph.Connect(playable, 0, _mixer, input);
                _clips[input] = clip;
            }
            _mixer.GetInput(input).SetTime(normalizedTime * clip.length);
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (_graph.IsValid()) _graph.Destroy();
        }
    }
}