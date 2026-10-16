using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;

[GhostComponent]
public struct ThirdPersonPlayer : IComponentData
{
    [GhostField]
    public Entity ControlledCharacter;

    [GhostField]
    public Entity ControlledCamera;
}

[Serializable]
public struct ThirdPersonPlayerInputs : IInputComponentData
{
    public float2 MoveInput;
    public float2 CameraLookInput;
    public float CameraZoomInput;
    public InputEvent JumpPressed; // This is now an InputEvent
}

[Serializable]
[GhostComponent(SendTypeOptimization = GhostSendType.OnlyPredictedClients)]
public struct ThirdPersonPlayerNetworkInput : IComponentData
{
    [GhostField()]
    public float2 LastProcessedCameraLookInput;
    [GhostField()]
    public float LastProcessedCameraZoomInput;
}