using Unity.Burst;
using Unity.Entities;
using Unity.Netcode;

[DisableAutoCreation]
[BurstCompile]
[WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
public partial struct Client3rdPSGameSetupSystem : ISystem
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
            ecb.AddComponent(joinRPC, new TPSClientJoinRequest());
            ecb.AddComponent(joinRPC, new SendRpcCommandRequest { TargetConnection = entity });
        }

        // Handle initialization for our local character camera (mark main camera entity)
        foreach (var (camera, entity) in SystemAPI.Query<OrbitCamera>().WithAll<GhostOwnerIsLocal>().WithNone<LocalInitialized>().WithEntityAccess())
        {
            ecb.AddComponent(entity, new MainEntityCamera());
            ecb.AddComponent(entity, new LocalInitialized());
        }
    }
}
public struct TPSClientJoinRequest : IRpcCommand
{ }