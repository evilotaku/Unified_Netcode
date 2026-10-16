using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine.Serialization;

[GhostComponent]
public struct FirstPersonPlayer : IComponentData
{
    [GhostField]
    public Entity ControlledCharacter;
    [FormerlySerializedAs("LookRotationSpeed")]
    [GhostField]
    public float LookInputSensitivity;
}

[Serializable]
public struct FirstPersonPlayerInputs : IInputComponentData
{
    public float2 MoveInput;
    public float2 LookInput;
    public InputEvent JumpPressed; // This is now an InputEvent
}

[Serializable]
[GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
public struct FirstPersonPlayerNetworkInput : IComponentData
{
    [GhostField()]
    public float2 LastProcessedLookInput;
}