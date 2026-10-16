using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// A volume that deals hits, attached to a bone such as a fist or foot.
    /// An attack's animation turns hitboxes on and off by name with HitboxOn/HitboxOff events
    /// (see <see cref="AttackData.HitWindows"/>). The server tests the sphere against <see cref="Hurtbox"/>es.
    /// The trigger collider is enabled only while the hitbox is active, so it shows in the Scene view during a swing.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class Hitbox : MonoBehaviour
    {
        [Tooltip("Name used by the HitboxOn/HitboxOff animation events, for example RightHand.")]
        public string Name;

        SphereCollider m_Sphere;

        public Vector3 WorldCenter => transform.TransformPoint(m_Sphere.center);
        public float WorldRadius => m_Sphere.radius * Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.y), Mathf.Abs(transform.lossyScale.z));

        public bool Active
        {
            get => m_Sphere.enabled;
            set => m_Sphere.enabled = value;
        }

        void Awake()
        {
            m_Sphere = GetComponent<SphereCollider>();
            m_Sphere.isTrigger = true;
            m_Sphere.enabled = false;
        }

        void Reset()
        {
            var sphere = GetComponent<SphereCollider>();
            sphere.isTrigger = true;
            sphere.radius = 0.12f;
        }

        void OnDrawGizmosSelected()
        {
            if (m_Sphere == null) m_Sphere = GetComponent<SphereCollider>();
            Gizmos.color = Application.isPlaying && Active ? Color.red : new Color(1f, 0.5f, 0f);
            Gizmos.DrawWireSphere(WorldCenter, WorldRadius);
        }
    }
}
