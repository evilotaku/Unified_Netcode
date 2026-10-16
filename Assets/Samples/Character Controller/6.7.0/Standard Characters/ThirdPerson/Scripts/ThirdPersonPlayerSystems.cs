
using Unity.Burst;
using Unity.CharacterController;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Netcode.NetcodeTime;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;

[UpdateInGroup(typeof(GhostInputSystemGroup))]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
public partial class ThirdPersonPlayerInputsSystem : SystemBase
{
    protected override void OnCreate()
    {
        RequireForUpdate<NetworkTime>();
        RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs>().Build());
    }

    protected override void OnUpdate()
    {
        foreach (var (playerInputs, player) in SystemAPI.Query<RefRW<ThirdPersonPlayerInputs>, ThirdPersonPlayer>().WithAll<GhostOwnerIsLocal>())
        {
            playerInputs.ValueRW.MoveInput = new float2
            {
                x = (Keyboard.current.dKey.isPressed ? 1f : 0f) + (Keyboard.current.aKey.isPressed ? -1f : 0f),
                y = (Keyboard.current.wKey.isPressed ? 1f : 0f) + (Keyboard.current.sKey.isPressed ? -1f : 0f),
            };

            InputDeltaUtilities.AddInputDelta(ref playerInputs.ValueRW.CameraLookInput, Mouse.current.delta.ReadValue());
            InputDeltaUtilities.AddInputDelta(ref playerInputs.ValueRW.CameraZoomInput, -Mouse.current.scroll.ReadValue().y);

            playerInputs.ValueRW.JumpPressed = default;
            if (Keyboard.current.spaceKey.wasPressedThisFrame)
            {
                playerInputs.ValueRW.JumpPressed.Set();
            }
        }
    }
}

/// <summary>
/// Apply inputs that need to be read at a variable rate
/// </summary>
[UpdateInGroup(typeof(PredictedSimulationSystemGroup))]
[UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
[BurstCompile]
public partial struct ThirdPersonPlayerVariableStepControlSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs>().Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (playerInputs, playerNetworkInput, player) in SystemAPI.Query<ThirdPersonPlayerInputs, RefRW<ThirdPersonPlayerNetworkInput>, ThirdPersonPlayer>().WithAll<Simulate>())
        {
            // Compute input deltas, compared to last known values
            float2 lookInputDelta = InputDeltaUtilities.GetInputDelta(
                playerInputs.CameraLookInput,
                playerNetworkInput.ValueRO.LastProcessedCameraLookInput);
            float zoomInputDelta = InputDeltaUtilities.GetInputDelta(
                playerInputs.CameraZoomInput,
                playerNetworkInput.ValueRO.LastProcessedCameraZoomInput);
            playerNetworkInput.ValueRW.LastProcessedCameraLookInput = playerInputs.CameraLookInput;
            playerNetworkInput.ValueRW.LastProcessedCameraZoomInput = playerInputs.CameraZoomInput;

            if (SystemAPI.HasComponent<OrbitCameraControl>(player.ControlledCamera))
            {
                OrbitCameraControl cameraControl = SystemAPI.GetComponent<OrbitCameraControl>(player.ControlledCamera);
                cameraControl.FollowedCharacterEntity = player.ControlledCharacter;
                cameraControl.LookDegreesDelta = lookInputDelta;
                cameraControl.ZoomDelta = zoomInputDelta;
                SystemAPI.SetComponent(player.ControlledCamera, cameraControl);
            }
        }
    }
}

/// <summary>
/// Apply inputs that need to be read at a fixed rate.
/// It is necessary to handle this as part of the fixed step group, in case your framerate is lower than the fixed step rate.
/// </summary>
[UpdateInGroup(typeof(PredictedFixedStepSimulationSystemGroup), OrderFirst = true)]
[BurstCompile]
public partial struct ThirdPersonPlayerFixedStepControlSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate(SystemAPI.QueryBuilder().WithAll<ThirdPersonPlayer, ThirdPersonPlayerInputs>().Build());
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        foreach (var (playerInputs, player) in SystemAPI.Query<ThirdPersonPlayerInputs, ThirdPersonPlayer>().WithAll<Simulate>())
        {
            if (SystemAPI.HasComponent<ThirdPersonCharacterControl>(player.ControlledCharacter))
            {
                ThirdPersonCharacterControl characterControl = SystemAPI.GetComponent<ThirdPersonCharacterControl>(player.ControlledCharacter);

                float3 characterUp = MathUtilities.GetUpFromRotation(SystemAPI.GetComponent<LocalTransform>(player.ControlledCharacter).Rotation);

                // Get camera rotation, since our movement is relative to it.
                quaternion cameraRotation = quaternion.identity;
                if (SystemAPI.HasComponent<OrbitCamera>(player.ControlledCamera))
                {
                    // Camera rotation is calculated rather than gotten from transform, because this allows us to 
                    // reduce the size of the camera ghost state in a netcode prediction context.
                    // If not using netcode prediction, we could simply get rotation from transform here instead.
                    OrbitCamera orbitCamera = SystemAPI.GetComponent<OrbitCamera>(player.ControlledCamera);
                    cameraRotation = OrbitCameraUtilities.CalculateCameraRotation(characterUp, orbitCamera.PlanarForward, orbitCamera.PitchAngle);
                }
                float3 cameraForwardOnUpPlane = math.normalizesafe(MathUtilities.ProjectOnPlane(MathUtilities.GetForwardFromRotation(cameraRotation), characterUp));
                float3 cameraRight = MathUtilities.GetRightFromRotation(cameraRotation);

                // Move
                characterControl.MoveVector = (playerInputs.MoveInput.y * cameraForwardOnUpPlane) + (playerInputs.MoveInput.x * cameraRight);
                characterControl.MoveVector = MathUtilities.ClampToMaxLength(characterControl.MoveVector, 1f);

                // Jump
                characterControl.Jump = playerInputs.JumpPressed.IsSet;

                SystemAPI.SetComponent(player.ControlledCharacter, characterControl);
            }
        }
    }
}