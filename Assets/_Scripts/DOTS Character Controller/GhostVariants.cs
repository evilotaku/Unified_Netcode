using System.Collections.Generic;
using Unity.CharacterController;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.NetCode;
using Unity.Physics;

public partial class DefaultVariantSystem : DefaultVariantSystemBase
{
    protected override void RegisterDefaultVariants(Dictionary<ComponentType, Rule> defaultVariants)
    {
        defaultVariants.Add(typeof(KinematicCharacterBody), Rule.ForAll(typeof(KinematicCharacterBody_DefaultVariant)));
        defaultVariants.Add(typeof(CharacterInterpolation), Rule.ForAll(typeof(CharacterInterpolation_GhostVariant)));
        defaultVariants.Add(typeof(TrackedTransform), Rule.ForAll(typeof(TrackedTransform_DefaultVariant)));

        //defaultVariants.Add(typeof(PhysicsVelocity), Rule.ForAll(typeof(PhysicsVelocity_DontSerializeVariant)));

    }
}

[GhostComponentVariation(typeof(KinematicCharacterBody))]
[GhostComponent()]
public struct KinematicCharacterBody_DefaultVariant
{
    // These two fields represent the basic synchronized state data that all networked characters will need.
    [GhostField()]
    public float3 RelativeVelocity;
    [GhostField()]
    public bool IsGrounded;

    // The following fields are only needed for characters that need to support parent entities (stand on moving platforms).
    // You can safely omit these from ghost sync if your game does not make use of character parent entities (any entities that have a TrackedTransform component).
    [GhostField()]
    public Entity ParentEntity;
    [GhostField()]
    public float3 ParentLocalAnchorPoint;
    [GhostField()]
    public float3 ParentVelocity;
}

// Character interpolation must only exist on predicted clients:
// - for remote interpolated ghost characters, interpolation is handled by netcode.
// - for server, interpolation is superfluous.
[GhostComponentVariation(typeof(CharacterInterpolation))]
[GhostComponent(PrefabType = GhostPrefabType.PredictedClient)]
public struct CharacterInterpolation_GhostVariant
{
}

[GhostComponentVariation(typeof(TrackedTransform))]
[GhostComponent()]
public struct TrackedTransform_DefaultVariant
{
    [GhostField()]
    public RigidTransform CurrentFixedRateTransform;
}

[CreateBefore(typeof(PhysicsDefaultVariantSystem))]
[GhostComponentVariation(typeof(PhysicsVelocity))]
[GhostComponent(SendTypeOptimization = GhostSendType.DontSend)] // Optimization: don't even try to send
public struct PhysicsVelocity_DontSerializeVariant
{
}