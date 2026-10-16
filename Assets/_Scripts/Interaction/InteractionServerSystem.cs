using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Unity.Netcode
{
    /// <summary>
    /// Server-side consumer for <see cref="InteractRequest"/>.
    /// Queries each connection separately so every request is matched to the player that connection owns.
    /// A client can therefore only ever act through its own player.
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class InteractionServerSystem : SystemBase
    {
        readonly List<InteractRequest> m_Requests = new();
        readonly HashSet<int> m_ConnectedIds = new();
        readonly List<Connection> m_Connections = new();

        protected override void OnUpdate()
        {
            if (World is not NetcodeWorld world || !world.IsServer()) return;
            // AllConnections only holds remote clients. A host's own player sends through the world's
            // LocalConnection (Single World host), so include it too.
            m_Connections.Clear();
            if (world.AllConnections != null) m_Connections.AddRange(world.AllConnections);
            if (world.IsHost()) m_Connections.Add(world.LocalConnection);

            m_ConnectedIds.Clear();
            foreach (var connection in m_Connections)
            {
                var networkId = connection.NetworkId;
                m_ConnectedIds.Add(networkId.Value);

                m_Requests.Clear();
                Netcode.RPC.Query(connection, m_Requests); // consumes this connection's requests
                if (m_Requests.Count == 0) continue;

                var player = PlayerInteractor.FindServerPlayer(world, networkId);
                if (player == null) continue;

                foreach (var request in m_Requests)
                    player.ServerHandleRequest(request);
            }

            // If a carrier's client disconnected, drop its item so it isn't stuck to (or destroyed with) the player.
            foreach (var player in PlayerInteractor.All)
            {
                if (!player.IsServer || player.Ghost.World != world || (player.HeldItem == null && player.HeldPlayer == null)) continue;
                if (!m_ConnectedIds.Contains(player.Ghost.OwnerNetworkId.Value))
                    player.ServerDrop(Vector3.zero);
            }
        }
    }
}
