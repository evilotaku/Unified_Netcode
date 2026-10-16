using System.Collections.Generic;
using Unity.Entities;
using Unity.Netcode;

/// <summary>
/// Client-to-server request to be put in game. Sent once the client connects.
/// The server reads it per connection, so it knows which connection asked without the client naming one.
/// </summary>
public struct GoInGameRequest : ICustomMessage
{
    public byte Unused; // the RPC serializer needs at least one field
}

/// <summary>
/// Server side of <see cref="GoInGameRequest"/>: marks the sending connection in game and raises
/// <see cref="NetworkManager.OnInGame"/> with the server's own connection entity.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class GoInGameServerSystem : SystemBase
{
    readonly List<GoInGameRequest> m_Requests = new();

    protected override void OnUpdate()
    {
        if (World is not NetcodeWorld world || !world.IsServer()) return;
        var connections = world.AllConnections;
        if (connections == null) return;

        foreach (var connection in connections)
        {
            m_Requests.Clear();
            Netcode.RPC.Query(connection, m_Requests); // consumes this connection's requests
            if (m_Requests.Count == 0) continue;

            var connectionEntity = connection.ConnectionEntity;
            if (EntityManager.HasComponent<NetworkStreamInGame>(connectionEntity)) continue; // already in game

            EntityManager.AddComponent<NetworkStreamInGame>(connectionEntity);
            DOTS_NetworkManager.OnInGame?.Invoke(connectionEntity);
        }
    }
}
