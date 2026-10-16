using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Receives the HitboxOn/HitboxOff animation events so Unity doesn't log "has no receiver" errors.
    /// The events aren't acted on here: <see cref="CombatControllerGhost"/> reads them from the clips once and runs
    /// the hit windows from the replicated attack time, which stays correct when prediction rolls back and replays.
    /// Put this on the GameObject with the Animator.
    /// </summary>
    public class CombatAnimationEvents : MonoBehaviour
    {
        public void HitboxOn(string hitbox) { }
        public void HitboxOff(string hitbox) { }
    }
}
