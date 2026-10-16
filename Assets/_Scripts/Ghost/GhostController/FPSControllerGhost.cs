using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Unity.Netcode
{
    public partial class FPSControllerGhost : GhostBehaviour
    {
        public GhostComponentRef<FPSInputs> InputComponent;

        [Header("Movement Settings")]
        public float MoveSpeed = 8f;
        public float Acceleration = 100f; // Increased for snappier response
        public float LookSensitivity = 0.15f;
        public float JumpForce = 5f;
        public float Gravity = -20f; // Slightly stronger gravity for better feel

        [Header("Dash")]
        [Tooltip("Seconds between the two taps of a direction for them to count as a double tap.")]
        public float DoubleTapWindow = 0.25f;
        [Tooltip("How far a stick must be pushed to count as a tap. Keys always count.")]
        [Range(0.1f, 1f)] public float TapThreshold = 0.6f;
        public float DashSpeed = 12f;
        [Tooltip("Seconds the dash moves the player for. Distance is Dash Speed x this.")]
        public float DashDuration = 0.2f;
        [Tooltip("Seconds the dash animation takes, from the start of the dash. While it plays the player can't attack or block.")]
        public float DashAnimationTime = 0.4f;
        [Tooltip("Seconds after a dash starts before the next can.")]
        public float DashCooldown = 0.5f;
        public bool AllowAirDash;

        [Header("References")]
        public Transform CameraTarget;
        public CapsuleCollider CharacterCollider;

        // Synchronized Fields
        public GhostField<float> ViewPitch;
        // Interpolated on remote players, where it drives the locomotion animation. The owner predicts with it unsmoothed.
        public GhostField<Vector3> PredictedVelocity = new(default, new FieldConfig { Smoothing = Unity.Netcode.SmoothingAction.Interpolate });
        // The current dash: its direction (a DashDirection, None when not dashing), seconds since it started, and its
        // world velocity, fixed at the start so turning mid-dash doesn't steer it.
        public GhostField<int> DashDirectionState;
        public GhostField<float> DashTime;
        public GhostField<Vector3> DashVelocity;
        public GhostField<float> DashCooldownRemaining;
        /// <summary>Goes up by one per dash, for effects.</summary>
        public GhostField<int> DashCount;

        private Rigidbody _rb;
        private CombatControllerGhost _combat;
        private CarryablePlayer _carryable;
        private CombatDummy _dummy;
        public bool IsGrounded;
        private bool _jumpBuffered; // Local flag to prevent multi-tick jumping

        // Double-tap detection, on the owning client. Each direction is tracked separately, so a direction can be
        // double-tapped while another is held.
        private readonly bool[] _tapHeld = new bool[5];
        private DashDirection _lastTap;
        private float _lastTapTime;
        private DashDirection _dashBuffered;


        // Input Actions cache
        private InputAction _moveAction;
        private InputAction _lookAction;
        private InputAction _jumpAction;

        public override void Awake()
        {
            base.Awake();
            _rb = GetComponent<Rigidbody>();
            _combat = GetComponent<CombatControllerGhost>();
            _carryable = GetComponent<CarryablePlayer>();
            _dummy = GetComponent<CombatDummy>();

            // Ensure Rigidbody doesn't tip and doesn't use auto-gravity
            _rb.constraints = RigidbodyConstraints.FreezeRotation;
            _rb.useGravity = false;

            // If not assigned, try to find the collider
            if (CharacterCollider == null) CharacterCollider = GetComponentInChildren<CapsuleCollider>();

            InputSystem.actions.Enable();
            _moveAction = InputSystem.actions.FindAction("Move");
            _lookAction = InputSystem.actions.FindAction("Look");
            _jumpAction = InputSystem.actions.FindAction("Jump");
            // Subscribe to the jump performed event for reliable one-shot triggers
            if (_jumpAction != null) _jumpAction.performed += OnJumpPerformed;

            // Only a server-only copy (binary-world host or dedicated server) goes on the ServerWorld layer.
            // In Single World host mode one copy is both server and client, and must be on ClientWorld.
            if (IsServer && !IsClient)
                SetLayerRecursively(gameObject, LayerMask.NameToLayer("ServerWorld"));
            else
                SetLayerRecursively(gameObject, LayerMask.NameToLayer("ClientWorld"));
        }

        private void SetLayerRecursively(GameObject obj, int newLayer)
        {
            if (newLayer == -1) return;
            obj.layer = newLayer;
            foreach (Transform child in obj.transform)
                SetLayerRecursively(child.gameObject, newLayer);
        }

        private void Start()
        {
            // 1. Visual Fix: Hide the server-only copy so a binary-world host doesn't see "double".
            // A Single World host has only one copy, which must stay visible.
            if (IsServer && !IsClient) foreach (var r in GetComponentsInChildren<Renderer>()) r.enabled = false;            
        }

        public override void PredictionGatherInput(PredictionGatherInputContext context)
        {
            var input = InputComponent.Value;
            input.Jump = default;
            input.Dash = default;
            if (_dashBuffered != DashDirection.None)
            {
                input.Dash.Set();
                input.DashDirection = (int)_dashBuffered;
                _dashBuffered = DashDirection.None;
            }
            input.Move = _moveAction?.ReadValue<Vector2>() ?? Vector2.zero;
            input.Look = _lookAction?.ReadValue<Vector2>() ?? Vector2.zero;

            // If a jump was recorded by the input system since the last tick
            if (_jumpBuffered)
            {
                input.Jump.Set();
                _jumpBuffered = false; // CONSUME the jump so it only applies to this one tick
            }

            InputComponent.Value = input;
        }

        public override void PredictionUpdate(PredictionUpdateContext context)
        {
            // Being carried: the server places us at the carrier's hold pose, so don't simulate movement.
            if (_carryable != null && _carryable.IsCarried)
            {
                PredictedVelocity.Value = Vector3.zero;
                return;
            }

            var input = InputComponent.Value;
            var tickedDeltaTime = context.TickedDeltaTime;
            // A dummy has no client sending input; its brain steers it on the server instead.
            if (IsServer && _dummy != null && _dummy.isActiveAndEnabled) _dummy.DriveMovement(ref input, tickedDeltaTime);

            // 1. ROTATION (Yaw)
            float yawDelta = input.Look.x * LookSensitivity;
            Quaternion nextRot = transform.rotation * Quaternion.Euler(0f, yawDelta, 0f);
            _rb.MoveRotation(nextRot);

            // 2. VIEW PITCH
            float pitch = ViewPitch.Value;
            pitch -= input.Look.y * LookSensitivity;
            pitch = Mathf.Clamp(pitch, -89f, 89f);
            ViewPitch.Value = pitch;

            // 3. GROUND CHECK (SphereCast)
            // Check slightly below the capsule
            float rayLength = 0.2f;
            Vector3 sphereCenter = transform.position + Vector3.up * (CharacterCollider.height * 0.5f);
            // Ignore triggers: combat hurtboxes on the player's own bones would otherwise count as ground.
            IsGrounded = UnityEngine.Physics.SphereCast(sphereCenter, CharacterCollider.radius * 0.9f, Vector3.down, out _,
                (CharacterCollider.height * 0.5f) - CharacterCollider.radius + rayLength,
                UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            // 3b. DASH
            UpdateDash(input, tickedDeltaTime);

            // 4. VELOCITY CALCULATION
            Vector3 currentVel = PredictedVelocity.Value;

            // Horizontal Movement
            Vector3 moveInput = new Vector3(input.Move.x, 0, input.Move.y);
            Vector3 moveDir = transform.TransformDirection(moveInput);
            if (moveDir.sqrMagnitude > 1f) moveDir.Normalize();

            // Attacks slow the player down and hit stun stops it.
            float speedMultiplier = _combat != null ? _combat.MoveSpeedMultiplier : 1f;
            Vector3 targetHorizVel = moveDir * MoveSpeed * speedMultiplier;

            // Apply Horizontal Acceleration
            float newX = Mathf.MoveTowards(currentVel.x, targetHorizVel.x, Acceleration * tickedDeltaTime);
            float newZ = Mathf.MoveTowards(currentVel.z, targetHorizVel.z, Acceleration * tickedDeltaTime);

            // A dash replaces the horizontal movement while it lasts.
            if (IsDashMoving)
            {
                newX = DashVelocity.Value.x;
                newZ = DashVelocity.Value.z;
            }

            // Vertical Movement
            float newY = currentVel.y;
            if (IsGrounded && newY < 0)
            {
                newY = -1f; // Small downward force to stay glued to slopes
            }
            else
            {
                newY += Gravity * tickedDeltaTime;
            }

            // Jump
            if (input.Jump.IsSet && IsGrounded && speedMultiplier > 0f && !IsDashMoving)
                newY = JumpForce;

            Vector3 finalVel = new Vector3(newX, newY, newZ);
            PredictedVelocity.Value = finalVel;

            // 5. APPLY MOVEMENT
            // Knockback from CombatControllerGhost is added on top and decays separately, so acceleration doesn't cancel it.
            Vector3 appliedVel = finalVel + (_combat != null ? _combat.KnockbackVelocity.Value : Vector3.zero);

            // A dynamic body (the server and the predicting client alike) is moved by the tick's physics step from its
            // velocity, which also handles collisions. Moving it as well with MovePosition would move it twice.
            // Only a kinematic body needs moving by hand.
            if (!_rb.isKinematic)
                _rb.linearVelocity = appliedVel;
            else
                _rb.MovePosition(transform.position + appliedVel * tickedDeltaTime);
        }

        /// <summary>Whether a dash is underway, including its animation after the movement ends.</summary>
        public bool IsDashing => DashDirectionState.Value != (int)DashDirection.None;
        /// <summary>Whether a dash is moving the player.</summary>
        public bool IsDashMoving => IsDashing && DashTime.Value < DashDuration;

        private bool CanDash => DashCooldownRemaining.Value <= 0f && (IsGrounded || AllowAirDash) && (_combat == null || _combat.CanDash);

        /// <summary>Advances the current dash, and starts one if a direction was double-tapped.</summary>
        private void UpdateDash(in FPSInputs input, float dt)
        {
            if (DashCooldownRemaining.Value > 0f)
                DashCooldownRemaining.Value = Mathf.Max(0f, DashCooldownRemaining.Value - dt);

            if (IsDashing)
            {
                bool wasMoving = IsDashMoving;
                DashTime.Value += dt;
                if (wasMoving && !IsDashMoving) ClampToMoveSpeed();
                if (DashTime.Value >= Mathf.Max(DashDuration, DashAnimationTime))
                    DashDirectionState.Value = (int)DashDirection.None;
            }

            if (!input.Dash.IsSet || !CanDash) return;
            var direction = (DashDirection)input.DashDirection;
            Vector3 local = direction switch
            {
                DashDirection.Forward => Vector3.forward,
                DashDirection.Back => Vector3.back,
                DashDirection.Left => Vector3.left,
                DashDirection.Right => Vector3.right,
                _ => Vector3.zero,
            };
            if (local == Vector3.zero) return;

            DashDirectionState.Value = (int)direction;
            DashTime.Value = 0f;
            DashVelocity.Value = transform.TransformDirection(local) * DashSpeed;
            DashCooldownRemaining.Value = DashCooldown;
            DashCount.Value++;
        }

        /// <summary>Ends the current dash, such as when the player is hit.</summary>
        public void CancelDash()
        {
            if (!IsDashing) return;
            if (IsDashMoving) ClampToMoveSpeed();
            DashDirectionState.Value = (int)DashDirection.None;
        }

        /// <summary>Drops dash speed at the end of a dash, so the player doesn't slide on afterwards.</summary>
        private void ClampToMoveSpeed()
        {
            var velocity = PredictedVelocity.Value;
            var horizontal = Vector3.ClampMagnitude(new Vector3(velocity.x, 0f, velocity.z), MoveSpeed);
            PredictedVelocity.Value = new Vector3(horizontal.x, velocity.y, horizontal.z);
        }

        /// <summary>Owning client: turns two quick presses of the same direction into a buffered dash.</summary>
        private void DetectDoubleTap()
        {
            if (_moveAction == null) return;
            var move = _moveAction.ReadValue<Vector2>();
            DetectTap(DashDirection.Forward, move.y);
            DetectTap(DashDirection.Back, -move.y);
            DetectTap(DashDirection.Left, -move.x);
            DetectTap(DashDirection.Right, move.x);
        }

        private void DetectTap(DashDirection direction, float amount)
        {
            int i = (int)direction;
            if (_tapHeld[i])
            {
                // Released once it drops well below the threshold, so a stick resting near it doesn't flicker.
                if (amount < TapThreshold * 0.5f) _tapHeld[i] = false;
                return;
            }
            if (amount < TapThreshold) return;

            _tapHeld[i] = true;
            float now = Time.unscaledTime;
            if (_lastTap == direction && now - _lastTapTime <= DoubleTapWindow)
            {
                _dashBuffered = direction;
                _lastTap = DashDirection.None;
            }
            else
            {
                _lastTap = direction;
                _lastTapTime = now;
            }
        }

        //Animation Events (called from animation clips)
        public void OnLand()
        {

        }

        public void OnFootstep()
        {
            print("Step");
        }

        void OnJumpPerformed(InputAction.CallbackContext _) => _jumpBuffered = true;

        void Update()
        {
            DetectDoubleTap();
        }

        void LateUpdate()
        {
            // In LateUpdate, after this frame's prediction has run: Update sees last frame's pitch, a frame behind the yaw.
            if (CameraTarget != null)
            {
                CameraTarget.localRotation = Quaternion.Euler(ViewPitch.Value, 0f, 0f);
            }
        }

        public override void OnDestroy()
        {
            // base.OnDestroy releases this behaviour's reference on the ghost entity; without it the entity outlives the player.
            base.OnDestroy();
            if (_jumpAction != null) _jumpAction.performed -= OnJumpPerformed;
        }
    }
}