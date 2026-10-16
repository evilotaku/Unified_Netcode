using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Transforms;
using UnityEngine;
using Random = Unity.Mathematics.Random;

[DisableAutoCreation]
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
public partial struct ServerTPSGameSetupSystem : ISystem
{
    private Unity.Mathematics.Random _random;

    [BurstCompile]
    void OnCreate(ref SystemState state)
    {
        _random = Random.CreateFromIndex(0);
    }

    [BurstCompile]
    void OnUpdate(ref SystemState state)
    {
        if (SystemAPI.HasSingleton<TPSGameSetup>())
        {
            EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<EndSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

            // Get our GameSetup singleton, which contains the prefabs we'll spawn
            TPSGameSetup gameSetup = SystemAPI.GetSingleton<TPSGameSetup>();

            // When a client wants to join, spawn and setup a character for them
            foreach (var (receiveRPC, joinRequest, entity) in SystemAPI.Query<ReceiveRpcCommandRequest, TPSClientJoinRequest>().WithEntityAccess())
            {
                // Spawn character, player, and camera ghost prefabs
                Entity characterEntity = ecb.Instantiate(gameSetup.CharacterPrefab);
                Entity playerEntity = ecb.Instantiate(gameSetup.PlayerPrefab);
                Entity cameraEntity = ecb.Instantiate(gameSetup.CameraPrefab);

                // Add spawned prefabs to the connection entity's linked entities, so they get destroyed along with it
                ecb.AppendToBuffer(receiveRPC.SourceConnection, new LinkedEntityGroup { Value = characterEntity });
                ecb.AppendToBuffer(receiveRPC.SourceConnection, new LinkedEntityGroup { Value = playerEntity });
                ecb.AppendToBuffer(receiveRPC.SourceConnection, new LinkedEntityGroup { Value = cameraEntity });

                // Setup the owners of the ghost prefabs (which are all owner-predicted) 
                // The owner is the client connection that sent the join request
                int clientConnectionId = SystemAPI.GetComponent<NetworkId>(receiveRPC.SourceConnection).Value;
                ecb.SetComponent(characterEntity, new GhostOwner { NetworkId = clientConnectionId });
                ecb.SetComponent(playerEntity, new GhostOwner { NetworkId = clientConnectionId });
                ecb.SetComponent(cameraEntity, new GhostOwner { NetworkId = clientConnectionId });

                // Setup links between the prefabs
                ThirdPersonPlayer player = SystemAPI.GetComponent<ThirdPersonPlayer>(gameSetup.PlayerPrefab);
                player.ControlledCharacter = characterEntity;
                player.ControlledCamera = cameraEntity;
                ecb.SetComponent(playerEntity, player);

                // Place character at a random point around world origin
                ecb.SetComponent(characterEntity, LocalTransform.FromPosition(_random.NextFloat3(new float3(-5f, 0f, -5f), new float3(5f, 0f, 5f))));

                // Allow this client to stream in game
                ecb.AddComponent<NetworkStreamInGame>(receiveRPC.SourceConnection);

                // Destroy the RPC since we've processed it
                ecb.DestroyEntity(entity);
            }
        }
    }
}
