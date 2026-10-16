using UnityEngine;
using Unity.Entities;

public class GhostPrefabRegistry : MonoBehaviour
{
    public GameObject[] Prefabs;

    class Baker : Baker<GhostPrefabRegistry>
    {
        public override void Bake(GhostPrefabRegistry authoring)
        {
            // This tells Unity: "Bake these GameObjects as Entity Prefabs"
            // They won't appear in the scene, but their metadata will be ready.
            foreach (var prefab in authoring.Prefabs)
            {
                GetEntity(prefab, TransformUsageFlags.Dynamic);
            }
        }
    }
}