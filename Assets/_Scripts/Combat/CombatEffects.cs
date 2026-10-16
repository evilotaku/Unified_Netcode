using Unity.Cinemachine;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Plays the effects in each <see cref="AttackData"/>: swing effects as the attack passes their times, and impact
    /// effects, hit-stop and camera shake when an attack lands. Also plays parries, pausing both players' animations
    /// for a moment, and flashes a parry cue on attackers just before their swings can hit. It is cosmetic and runs on clients only, driven by the
    /// state <see cref="CombatControllerGhost"/> replicates, so it isn't affected by prediction replays.
    /// Hits are decided by the server, so impact effects play once the server's snapshot arrives.
    /// Put this on the player ghost prefab next to <see cref="CombatControllerGhost"/>.
    /// </summary>
    public class CombatEffects : MonoBehaviour
    {
        [Header("References")]
        public CombatControllerGhost Combat;
        [Tooltip("Optional. Frozen briefly on hits for hit-stop.")]
        public FPSAnimatorGhost Animator;
        [Tooltip("Optional. Shakes the camera when the local player hits or is hit. Created if left empty.")]
        public CinemachineImpulseSource ImpulseSource;

        [Header("Parry")]
        [Tooltip("Spawned where a parry happens.")]
        public GameObject ParryPrefab;
        public AudioClip ParrySound;
        [Range(0f, 1f)] public float ParryVolume = 1f;
        [Tooltip("Seconds both players' animations pause on a parry.")]
        public float ParryHitStop = 0.25f;
        [Tooltip("Camera shake force on a parry, for the local player involved.")]
        public float ParryShake = 0.8f;

        [Header("Parry Indicator")]
        [Tooltip("Flash on an attacker shortly before its swing can hit, as the cue to parry.")]
        public bool ShowParryIndicator = true;
        [Tooltip("Seconds before the attack's first hit window that the flash appears.")]
        public float ParryIndicatorLead = 0.25f;
        [Tooltip("Also flash on the local player's own attacks.")]
        public bool ParryIndicatorOnOwnAttacks;
        [Tooltip("Flash color for attacks that can be parried.")]
        public Color ParryableColor = new(1f, 0.8f, 0.15f, 1f);
        [Tooltip("Flash color for attacks that can't be parried, only blocked or dodged.")]
        public Color UnparryableColor = new(1f, 0.15f, 0.1f, 1f);
        [Tooltip("Width of the flash in meters.")]
        public float ParryIndicatorSize = 1f;
        public float ParryIndicatorDuration = 0.3f;
        [Tooltip("Optional. Spawned at the flash point instead of the built-in flash.")]
        public GameObject ParryIndicatorPrefab;
        public AudioClip ParryIndicatorSound;
        [Range(0f, 1f)] public float ParryIndicatorVolume = 1f;

        [Header("Dash")]
        [Tooltip("Spawned at the player's feet when it dashes, facing the dash direction.")]
        public GameObject DashPrefab;
        public AudioClip DashSound;
        [Range(0f, 1f)] public float DashVolume = 1f;

        FPSControllerGhost m_Controller;
        int m_LastDashCount;
        bool m_Initialized;
        int m_LastSerial;
        float m_LastTime;
        int m_LastHitConfirmCount;
        int m_LastHitCount;
        int m_LastParryCount;
        int m_LastParriedCount;

        void Update()
        {
            if (!m_Initialized && !Initialize()) return;

            PlaySwingEffects();

            if (m_Controller != null && m_Controller.DashCount.Value != m_LastDashCount)
            {
                m_LastDashCount = m_Controller.DashCount.Value;
                OnDash();
            }

            if (Combat.HitConfirmCount.Value != m_LastHitConfirmCount)
            {
                // Several hits between frames show once, at the latest.
                m_LastHitConfirmCount = Combat.HitConfirmCount.Value;
                OnHitDealt();
            }

            if (Combat.HitCount.Value != m_LastHitCount)
            {
                m_LastHitCount = Combat.HitCount.Value;
                var attack = Combat.Combos != null ? Combat.Combos.GetAttack(Combat.LastHitAttackId.Value) : null;
                if (attack != null)
                {
                    if (Animator != null) Animator.HitStop(attack.HitStop);
                    if (IsLocalPlayer) Shake(attack.CameraShake);
                }
            }

            if (Combat.ParryCount.Value != m_LastParryCount)
            {
                m_LastParryCount = Combat.ParryCount.Value;
                OnParry();
            }

            if (Combat.ParriedCount.Value != m_LastParriedCount)
            {
                // Got parried. The parrying player's copy plays the impact; this side only reacts.
                m_LastParriedCount = Combat.ParriedCount.Value;
                if (Animator != null) Animator.HitStop(ParryHitStop);
                if (IsLocalPlayer) Shake(ParryShake);
            }
        }

        void OnDash()
        {
            var position = Combat.transform.position;
            if (DashPrefab != null)
            {
                var direction = m_Controller.DashVelocity.Value;
                direction.y = 0f;
                var rotation = direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : Combat.transform.rotation;
                Destroy(Instantiate(DashPrefab, position, rotation), 2f);
            }
            if (DashSound != null) AudioSource.PlayClipAtPoint(DashSound, position, DashVolume);
        }

        void OnParry()
        {
            var point = Combat.ParryPoint.Value;
            if (ParryPrefab != null)
            {
                var toParrier = Combat.transform.position - point;
                toParrier.y = 0f;
                var rotation = toParrier.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(-toParrier) : Quaternion.identity;
                Destroy(Instantiate(ParryPrefab, point, rotation), 3f);
            }
            if (ParrySound != null) AudioSource.PlayClipAtPoint(ParrySound, point, ParryVolume);
            if (Animator != null) Animator.HitStop(ParryHitStop);

            if (IsLocalPlayer) Shake(ParryShake);
        }

        bool Initialize()
        {
            if (Combat == null) Combat = GetComponent<CombatControllerGhost>();
            if (Combat == null) { enabled = false; return false; }
            var ghost = Combat.Ghost;
            if (ghost == null || !(ghost.IsClient || ghost.IsServer)) return false; // not spawned yet
            if (!ghost.IsClient) { enabled = false; return false; } // server-only copy: nothing to show

            if (Animator == null) Animator = GetComponent<FPSAnimatorGhost>();
            m_Controller = GetComponent<FPSControllerGhost>();
            // Don't replay what happened before we joined.
            if (m_Controller != null) m_LastDashCount = m_Controller.DashCount.Value;
            m_LastSerial = Combat.AttackSerial.Value;
            m_LastTime = Combat.AttackNormalizedTime;
            m_LastHitConfirmCount = Combat.HitConfirmCount.Value;
            m_LastHitCount = Combat.HitCount.Value;
            m_LastParryCount = Combat.ParryCount.Value;
            m_LastParriedCount = Combat.ParriedCount.Value;
            m_Initialized = true;
            return true;
        }

        bool IsLocalPlayer => Animator != null && Animator.IsLocalOwner;

        void PlaySwingEffects()
        {
            var attack = Combat.CurrentAttack;
            int serial = Combat.AttackSerial.Value;
            float time = Combat.AttackNormalizedTime;
            if (serial != m_LastSerial)
            {
                // A new swing: play everything from its start.
                m_LastSerial = serial;
                m_LastTime = -1f;
            }
            if (attack == null || time <= m_LastTime) return; // a rollback can briefly move time back; don't replay

            foreach (var effect in attack.Effects)
            {
                if (effect.Time > m_LastTime && effect.Time <= time)
                    PlayEffect(effect);
            }

            if (ShowParryIndicator && attack.HitWindows.Count > 0 && (ParryIndicatorOnOwnAttacks || !IsLocalPlayer))
            {
                // The flash time is in seconds before the first hit, converted to clip time like the effects'.
                float flashTime = attack.ToClipTime(attack.ToElapsed(attack.WindupEnd) - ParryIndicatorLead);
                if (flashTime > m_LastTime && flashTime <= time)
                    PlayParryIndicator(attack);
            }
            m_LastTime = time;
        }

        void PlayParryIndicator(AttackData attack)
        {
            // Flash on the hitbox that hits first, such as the striking fist.
            string hitbox = attack.HitWindows[0].Hitbox;
            foreach (var window in attack.HitWindows)
                if (window.Start < attack.HitWindows[0].Start) hitbox = window.Hitbox;
            var attachPoint = FindAttachPoint(hitbox);
            // No hitbox to follow: flash at chest height.
            var offset = attachPoint == Combat.transform ? Vector3.up * 1.4f : Vector3.zero;

            var color = attack.CanBeParried ? ParryableColor : UnparryableColor;
            if (ParryIndicatorPrefab != null)
                Destroy(Instantiate(ParryIndicatorPrefab, attachPoint.position + offset, attachPoint.rotation, attachPoint), 2f);
            else
                ParryIndicatorFlash.Spawn(attachPoint, offset, color, ParryIndicatorSize, ParryIndicatorDuration);
            if (ParryIndicatorSound != null)
                AudioSource.PlayClipAtPoint(ParryIndicatorSound, attachPoint.position, ParryIndicatorVolume);
        }

        void PlayEffect(in AttackEffect effect)
        {
            var attachPoint = FindAttachPoint(effect.AttachTo);
            if (effect.Prefab != null)
            {
                var rotation = attachPoint.rotation * Quaternion.Euler(effect.RotationOffset);
                var position = attachPoint.TransformPoint(effect.PositionOffset);
                var instance = Instantiate(effect.Prefab, position, rotation, effect.FollowAttachPoint ? attachPoint : null);
                Destroy(instance, effect.Lifetime > 0f ? effect.Lifetime : 2f);
            }
            if (effect.Sound != null)
                AudioSource.PlayClipAtPoint(effect.Sound, attachPoint.position, effect.Volume);
        }

        Transform FindAttachPoint(string hitboxName)
        {
            if (!string.IsNullOrEmpty(hitboxName) && Combat.Hitboxes != null)
            {
                foreach (var hitbox in Combat.Hitboxes)
                    if (hitbox.Name == hitboxName) return hitbox.transform;
            }
            return Combat.transform;
        }

        void OnHitDealt()
        {
            var attack = Combat.Combos != null ? Combat.Combos.GetAttack(Combat.HitConfirmAttackId.Value) : null;
            if (attack == null) return;

            bool blocked = Combat.HitConfirmBlocked.Value;
            var point = Combat.HitConfirmPoint.Value;
            var prefab = blocked ? attack.BlockedPrefab : attack.ImpactPrefab;
            var sound = blocked ? attack.BlockedSound : attack.ImpactSound;

            if (prefab != null)
            {
                // Face the impact back toward the attacker.
                var toAttacker = Combat.transform.position - point;
                toAttacker.y = 0f;
                var rotation = toAttacker.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(toAttacker) : Quaternion.identity;
                Destroy(Instantiate(prefab, point, rotation), 3f);
            }
            if (sound != null) AudioSource.PlayClipAtPoint(sound, point, attack.ImpactVolume);

            if (!blocked && Animator != null) Animator.HitStop(attack.HitStop);
            if (IsLocalPlayer) Shake(blocked ? attack.CameraShake * 0.5f : attack.CameraShake);
        }

        void Shake(float force)
        {
            if (force <= 0f) return;
            if (ImpulseSource == null)
            {
                ImpulseSource = gameObject.AddComponent<CinemachineImpulseSource>();
                ImpulseSource.ImpulseDefinition.ImpulseDuration = 0.15f;
            }
            EnsureListener();
            ImpulseSource.GenerateImpulseWithForce(force);
        }

        /// <summary>Impulses only move a camera that has a listener; add one to the scene's camera if it has none.</summary>
        static void EnsureListener()
        {
            var vcam = FindAnyObjectByType<CinemachineCamera>();
            if (vcam != null && vcam.GetComponent<CinemachineImpulseListener>() == null)
                vcam.gameObject.AddComponent<CinemachineImpulseListener>();
        }
    }
}
