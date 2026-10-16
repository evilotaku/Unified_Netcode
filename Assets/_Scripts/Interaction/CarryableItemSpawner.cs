using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Spawns carryable items once the server is running. GameObject ghosts can't be placed directly in a
    /// scene (they must be spawned from a prefab at runtime), so this stands in for scene placement.
    /// Each child transform is used as a spawn point. With no children, items are laid out in a row.
    /// </summary>
    public class CarryableItemSpawner : MonoBehaviour
    {
        public CarryableItem ItemPrefab;
        [Tooltip("Only used when this object has no child spawn points.")]
        public int Count = 5;
        [Tooltip("Only used when this object has no child spawn points.")]
        public float Spacing = 1.5f;

        bool m_Spawned;

        void Update()
        {
            if (m_Spawned || ItemPrefab == null) return;

            // A bridged prefab (NetworkObject + GhostObject) must be spawned through NGO, otherwise NGO
            // treats it as unspawned and blocks the re-parenting used to carry it.
            bool bridged = ItemPrefab.TryGetComponent<NetworkObject>(out _);
            if (bridged)
            {
                var networkManager = NetworkManager.Singleton;
                if (networkManager == null || !networkManager.IsListening || !networkManager.IsServer) return;
            }
            else
            {
                if (!Netcode.IsActive || !Netcode.IsServerRole) return;
                if (ClientServerBootstrap.ServerWorld == null) return;
            }

            m_Spawned = true;

            if (transform.childCount > 0)
            {
                foreach (Transform point in transform)
                    Spawn(point.position, point.rotation, bridged);
            }
            else
            {
                for (int i = 0; i < Count; i++)
                    Spawn(transform.position + transform.right * (i * Spacing), transform.rotation, bridged);
            }
        }

        void Spawn(Vector3 position, Quaternion rotation, bool bridged)
        {
            var item = Instantiate(ItemPrefab, position, rotation);
            if (bridged) item.GetComponent<NetworkObject>().Spawn();
        }
    }
}
