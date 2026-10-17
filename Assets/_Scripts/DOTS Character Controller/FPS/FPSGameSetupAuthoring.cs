using System;
using Unity.Entities;
using Unity.Netcode;
using UnityEngine;



public class FPSGameSetupAuthoring : MonoBehaviour
{
    public GameObject CharacterPrefab;
    public GameObject PlayerPrefab;

    class Baker : Baker<FPSGameSetupAuthoring>
    {
        public override void Bake(FPSGameSetupAuthoring authoring)
        {
            AddComponent(GetEntity(authoring, TransformUsageFlags.None), new FPSGameSetup
            {
                CharacterPrefab = GetEntity(authoring.CharacterPrefab, TransformUsageFlags.None),
                PlayerPrefab = GetEntity(authoring.PlayerPrefab, TransformUsageFlags.None),
            });
        }
    }
}

[Serializable]
public struct FPSGameSetup : IComponentData
{
    public Entity CharacterPrefab;
    public Entity PlayerPrefab;
}

public struct FPSClientJoinRequest : IRpcCommand
{ }

public struct LocalInitialized : IComponentData
{ }
