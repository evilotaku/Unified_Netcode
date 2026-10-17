using Unity.Entities;
using UnityEngine;

public class ThirdPersonGameSetupAuthoring : MonoBehaviour
{
    public GameObject CharacterPrefab;
    public GameObject PlayerPrefab;
    public GameObject CameraPrefab;

    class Baker : Baker<ThirdPersonGameSetupAuthoring>
    {
        public override void Bake(ThirdPersonGameSetupAuthoring authoring)
        {
            AddComponent(GetEntity(authoring, TransformUsageFlags.None), new TPSGameSetup
            {
                CharacterPrefab = GetEntity(authoring.CharacterPrefab, TransformUsageFlags.None),
                PlayerPrefab = GetEntity(authoring.PlayerPrefab, TransformUsageFlags.None),
                CameraPrefab = GetEntity(authoring.CameraPrefab, TransformUsageFlags.None),
            });
        }
    }
}

public struct TPSGameSetup : IComponentData
{
    public Entity CharacterPrefab;
    public Entity PlayerPrefab;
    public Entity CameraPrefab;
}
