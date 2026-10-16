using System;
using System.Collections.Generic;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>A button that starts or continues a combo.</summary>
    public enum AttackKind
    {
        None = 0,
        Light = 1,
        Medium = 2,
        Heavy = 3,
        Special = 4,
    }

    /// <summary>A span of an attack, as 0-1 fractions of its duration, during which a hitbox can hit.</summary>
    [Serializable]
    public struct HitWindow
    {
        public float Start;
        public float End;
        [Tooltip("Hitbox name. Empty means every hitbox.")]
        public string Hitbox;
    }

    /// <summary>Pressing <see cref="Input"/> goes on to <see cref="Attack"/>.</summary>
    [Serializable]
    public struct ComboLink
    {
        public AttackKind Input;
        public AttackData Attack;
    }

    /// <summary>A particle prefab and/or sound played at a point in an attack, such as a swing trail and whoosh.</summary>
    [Serializable]
    public struct AttackEffect
    {
        [Tooltip("When to play it, as a 0-1 fraction of the attack's duration.")]
        [Range(0f, 1f)] public float Time;

        public GameObject Prefab;
        [Tooltip("Hitbox to spawn the prefab at, for example RightHand. Empty means the player's root.")]
        public string AttachTo;
        [Tooltip("Parent the prefab to the attach point so it follows it, for trails.")]
        public bool FollowAttachPoint;
        public Vector3 PositionOffset;
        public Vector3 RotationOffset;
        [Tooltip("Seconds before the spawned prefab is destroyed.")]
        public float Lifetime;

        public AudioClip Sound;
        [Range(0f, 1f)] public float Volume;
    }

    /// <summary>
    /// One attack in a combo: its animation, timing, hit windows, damage, knockback, the attacks it can chain into,
    /// and its effects. Create with Assets > Create > Combat > Attack and reach it from a <see cref="ComboSet"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Combat/Attack", fileName = "Attack")]
    public class AttackData : ScriptableObject
    {
        [Header("Animation")]
        [Tooltip("The attack's animation, played directly over the Animator. Its HitboxOn/HitboxOff events (string parameter: hitbox name) set Hit Windows.")]
        public AnimationClip Clip;
        [Tooltip("Off: don't play Clip; set the Animator's AttackType parameter to Animator Attack Type and let the controller's own state play it.")]
        public bool PlayClipDirectly = true;
        [Tooltip("Value for the Animator's AttackType parameter when Play Clip Directly is off.")]
        public int AnimatorAttackType;
        [Tooltip("Seconds to blend in from the previous pose or attack.")]
        public float BlendInTime = 0.08f;
        [Tooltip("Seconds to blend out to locomotion at the end, when nothing follows.")]
        public float BlendOutTime = 0.15f;

        [Header("Timing")]
        [Tooltip("Length of the attack in seconds, not counting Startup Delay. The clip is stretched to fit.")]
        public float Duration = 0.5f;
        [Tooltip("Extra seconds of wind-up. The clip up to its first hit window plays slower to fill them, so the swing " +
                 "is easier to read and parry. The rest of the attack keeps its speed.")]
        public float StartupDelay = 0f;
        [Tooltip("When hitboxes are active, as 0-1 fractions of the clip. Read from the clip's events when it has them.")]
        public List<HitWindow> HitWindows = new();
        [Tooltip("Off: ignore the clip's HitboxOn/HitboxOff events and keep Hit Windows as set here. For an attack that " +
                 "borrows a melee clip but shouldn't hit with it, such as a fireball cast.")]
        public bool UseClipHitWindows = true;
        [Tooltip("Attacker's move speed multiplier while this attack plays.")]
        public float MoveSpeedMultiplier = 0.7f;

        [Header("Hit")]
        public float Damage = 5f;
        [Tooltip("Horizontal speed given to the target, away from the attacker.")]
        public float KnockbackSpeed = 4f;
        [Tooltip("Upward speed given to the target.")]
        public float KnockbackUpSpeed = 0f;
        [Tooltip("How long the target can't move, attack or block.")]
        public float HitStunTime = 0.25f;
        [Tooltip("Off: a timed block only blocks this attack, it can't parry it. For unparryable finishers.")]
        public bool CanBeParried = true;

        [Header("Combo")]
        [Tooltip("Pressing one of these buttons during the combo window queues the linked attack.")]
        public List<ComboLink> FollowUps = new();
        [Tooltip("When a follow-up press is accepted, as 0-1 fractions of Duration. Presses outside it are ignored.")]
        [Range(0f, 1f)] public float ComboWindowStart = 0.25f;
        [Range(0f, 1f)] public float ComboWindowEnd = 1f;
        [Tooltip("Earliest point (0-1) a queued follow-up cuts this attack short and starts.")]
        [Range(0f, 1f)] public float CancelTime = 0.7f;

        [Header("Projectile")]
        [Tooltip("Fired straight ahead when the attack reaches Projectile Time, such as a fireball. Its explosion uses " +
                 "this attack's Hit settings. Empty for a melee-only attack.")]
        public Projectile ProjectilePrefab;
        [Tooltip("When to fire it, as a 0-1 fraction of the clip.")]
        [Range(0f, 1f)] public float ProjectileTime = 0.3f;
        [Tooltip("Where it's fired from, relative to the player's root.")]
        public Vector3 ProjectileOffset = new(0f, 1.3f, 0.8f);

        [Header("Effects")]
        [Tooltip("Played as the attack passes each effect's Time: swing trails, whooshes, shouts.")]
        public List<AttackEffect> Effects = new();

        [Header("On Hit")]
        [Tooltip("Spawned where the attack connects.")]
        public GameObject ImpactPrefab;
        public AudioClip ImpactSound;
        [Tooltip("Spawned instead when the target blocks.")]
        public GameObject BlockedPrefab;
        public AudioClip BlockedSound;
        [Range(0f, 1f)] public float ImpactVolume = 1f;
        [Tooltip("Seconds the attacker's and target's animation freeze on a hit, to sell the impact. Visual only.")]
        public float HitStop = 0.06f;
        [Tooltip("Camera shake force for the attacker and the target when they're the local player.")]
        public float CameraShake = 0.3f;

        /// <summary>The attack the given button leads to from this one, or null.</summary>
        public AttackData GetFollowUp(AttackKind input)
        {
            foreach (var link in FollowUps)
                if (link.Input == input && link.Attack != null) return link.Attack;
            return null;
        }

        /// <summary>Seconds from the attack's start to its end, including <see cref="StartupDelay"/>.</summary>
        public float TotalDuration => Duration + Mathf.Max(0f, StartupDelay);

        /// <summary>Where the wind-up ends, as a 0-1 fraction of the clip: the first hit window's start.</summary>
        public float WindupEnd
        {
            get
            {
                if (HitWindows.Count == 0) return 0f;
                float start = 1f;
                foreach (var window in HitWindows) start = Mathf.Min(start, window.Start);
                return Mathf.Clamp01(start);
            }
        }

        /// <summary>
        /// The 0-1 point in the clip reached <paramref name="elapsed"/> seconds into the attack. Hit windows, effect
        /// times, the combo window and the cancel time are all in these units. The wind-up is slowed by
        /// <see cref="StartupDelay"/>; the rest plays over its share of <see cref="Duration"/>.
        /// </summary>
        public float ToClipTime(float elapsed)
        {
            float duration = Mathf.Max(Duration, 0.0001f);
            float windup = WindupEnd;
            float windupSeconds = windup * duration + Mathf.Max(0f, StartupDelay);
            if (elapsed < windupSeconds)
                return windupSeconds > 0f ? windup * Mathf.Max(0f, elapsed) / windupSeconds : 0f;
            return Mathf.Clamp01(windup + (elapsed - windupSeconds) / duration);
        }

        /// <summary>The inverse of <see cref="ToClipTime"/>: seconds into the attack at which the clip reaches <paramref name="clipTime"/>.</summary>
        public float ToElapsed(float clipTime)
        {
            float windup = WindupEnd;
            float startup = Mathf.Max(0f, StartupDelay);
            if (clipTime < windup) return clipTime / windup * (windup * Duration + startup);
            return startup + clipTime * Duration;
        }

        /// <summary>Whether a follow-up press at 0-1 time <paramref name="t"/> is accepted.</summary>
        public bool InComboWindow(float t) => t >= ComboWindowStart && t <= ComboWindowEnd;

        /// <summary>Replaces <see cref="HitWindows"/> with the clip's HitboxOn/HitboxOff event pairs, if it has any.</summary>
        public void LoadHitWindowsFromClip()
        {
            if (!UseClipHitWindows || Clip == null || Clip.length <= 0f) return;
            var events = Clip.events;
            if (Array.FindIndex(events, e => e.functionName == "HitboxOn") < 0) return;

            Array.Sort(events, (a, b) => a.time.CompareTo(b.time));
            var open = new Dictionary<string, float>();
            HitWindows.Clear();
            foreach (var e in events)
            {
                var hitbox = e.stringParameter ?? "";
                float t = e.time / Clip.length;
                if (e.functionName == "HitboxOn")
                    open[hitbox] = t;
                else if (e.functionName == "HitboxOff" && open.Remove(hitbox, out var start))
                    HitWindows.Add(new HitWindow { Start = start, End = t, Hitbox = hitbox });
            }
        }

        void OnEnable() => LoadHitWindowsFromClip();

        void OnValidate()
        {
            // Shows the clip's hit windows in the Inspector.
            LoadHitWindowsFromClip();
            if (ComboWindowEnd < ComboWindowStart) ComboWindowEnd = ComboWindowStart;
            for (int i = 0; i < Effects.Count; i++)
            {
                // New list entries start zeroed; give them usable defaults.
                var effect = Effects[i];
                if (effect.Lifetime <= 0f) effect.Lifetime = 2f;
                if (effect.Volume <= 0f) effect.Volume = 1f;
                Effects[i] = effect;
            }
        }
    }
}
