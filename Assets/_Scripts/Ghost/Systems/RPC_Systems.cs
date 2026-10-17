using Unity.Collections;
using Unity.Entities;
using Unity.Netcode;
using UnityEngine;

public partial struct RPC_Systems : ISystem
{
    void OnUpdate(ref SystemState state)
    {      
        if(!state.World.IsServer()) return;

        var ecb = SystemAPI.GetSingletonRW<EndSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer(state.WorldUnmanaged);

        // 1. Collect pending connections into a temporary list
        var pendingConnections = new NativeList<Entity>(Allocator.Temp);

        foreach (var (rpc, join, entity) in SystemAPI.Query<ReceiveRpcCommandRequest, FPSClientJoinRequest>().WithEntityAccess())
        {
            if (!state.EntityManager.HasComponent<NetworkStreamInGame>(rpc.SourceConnection))
            {
                pendingConnections.Add(rpc.SourceConnection);
            }
            ecb.DestroyEntity(entity);
        }

        // 2. Perform instantiation OUTSIDE the query loop (World is now unlocked)
        foreach (var connectionEntity in pendingConnections)
        {
            // Mark as in-game so we don't process them again
            state.EntityManager.AddComponent<NetworkStreamInGame>(connectionEntity);

            // Trigger the GameObject spawning
            DOTS_NetworkManager.OnInGame?.Invoke(connectionEntity);
        }

        pendingConnections.Dispose();
    }
}
