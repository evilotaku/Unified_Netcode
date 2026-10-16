using Unity.Entities;
using Unity.Netcode;

/// <summary>
/// Workaround for NGO 3.0 unified mode on Netcode for Entities 7.1 Single World hosts.
/// NGO's UnifiedUpdateConnections only marks connections that have NetworkStreamConnection as in game,
/// but a Single World host's own connection only has LocalConnection. Without NetworkStreamInGame on it,
/// the host's ghosts never get ghost ids or spawn ticks. Remote clients are unaffected.
/// Remove this once NGO handles the host's local connection itself.
/// </summary>
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class HostLocalConnectionInGameSystem : SystemBase
{
    EntityQuery m_HostConnectionQuery;

    protected override void OnCreate()
    {
        m_HostConnectionQuery = GetEntityQuery(
            ComponentType.ReadOnly<NetworkId>(),
            ComponentType.ReadOnly<LocalConnection>(),
            ComponentType.Exclude<NetworkStreamInGame>());
        RequireForUpdate(m_HostConnectionQuery);
    }

    protected override void OnUpdate()
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsHost) return;

        EntityManager.AddComponent<NetworkStreamInGame>(m_HostConnectionQuery);
    }
}
