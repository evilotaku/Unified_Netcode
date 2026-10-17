using Unity.Burst;
using Unity.Entities;
using Unity.Netcode;

[DisableAutoCreation]
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
public partial struct ClientFPSGameSetupSystem : ISystem
{
    [BurstCompile]
    void OnUpdate(ref SystemState state)
    {
        EntityCommandBuffer ecb = SystemAPI.GetSingletonRW<EndSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

        // Send a join request to the server if we haven't done so yet
        foreach (var (netId, entity) in SystemAPI.Query<NetworkId>().WithNone<NetworkStreamInGame>().WithEntityAccess())
        {
            // Mark our connection as ready to go in game
            ecb.AddComponent(entity, new NetworkStreamInGame());

            // Send an RPC that asks the server if we can join
            Entity joinRPC = ecb.CreateEntity();
            ecb.AddComponent(joinRPC, new FPSClientJoinRequest());
            ecb.AddComponent(joinRPC, new SendRpcCommandRequest { TargetConnection = entity });
        }

        // Handle initialization for our local character (mark main camera entity)
        foreach (var (character, entity) in SystemAPI.Query<FirstPersonCharacterComponent>().WithAll<GhostOwnerIsLocal>().WithNone<LocalInitialized>().WithEntityAccess())
        {
            ecb.AddComponent(character.ViewEntity, new MainEntityCamera());
            ecb.AddComponent(entity, new LocalInitialized());
        }
    }
}
