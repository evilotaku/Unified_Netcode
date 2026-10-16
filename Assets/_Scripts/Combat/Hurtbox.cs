using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// A trigger volume that can be hit, attached to a bone of a player with a <see cref="CombatControllerGhost"/>.
    /// It follows the animated pose, so a hit lands where the body actually is.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class Hurtbox : MonoBehaviour
    {
        [Tooltip("Multiplies the attack's damage when this is the part that's hit, for example 1.5 for the head.")]
        public float DamageMultiplier = 1f;

        /// <summary>The player this hurtbox belongs to.</summary>
        public CombatControllerGhost Owner { get; private set; }

        void Awake()
        {
            GetComponent<Collider>().isTrigger = true;
            Owner = GetComponentInParent<CombatControllerGhost>();
        }

        void Reset() => GetComponent<Collider>().isTrigger = true;
    }
}
