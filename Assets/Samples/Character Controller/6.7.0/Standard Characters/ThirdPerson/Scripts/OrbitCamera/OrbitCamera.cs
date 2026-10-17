using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;

[GhostComponent]
public struct OrbitCamera : IComponentData
{
    public float RotationSpeed;
    public float MaxVAngle;
    public float MinVAngle;
    public bool RotateWithCharacterParent;

    public float MinDistance;
    public float MaxDistance;
    public float DistanceMovementSpeed;
    public float DistanceMovementSharpness;

    public float ObstructionRadius;
    public float ObstructionInnerSmoothingSharpness;
    public float ObstructionOuterSmoothingSharpness;
    public bool PreventFixedUpdateJitter;

    [GhostField]
    public float TargetDistance;
    public float SmoothedTargetDistance;
    public float ObstructedDistance;
    [GhostField]
    public float PitchAngle;
    [GhostField]
    public float3 PlanarForward;
}

[Serializable]
public struct OrbitCameraControl : IComponentData
{
    [NonSerialized]
    public Entity FollowedCharacterEntity;
    public float2 LookDegreesDelta;
    public float ZoomDelta;
}

[Serializable]
public struct OrbitCameraIgnoredEntityBufferElement : IBufferElementData
{
    [NonSerialized]
    public Entity Entity;
}
