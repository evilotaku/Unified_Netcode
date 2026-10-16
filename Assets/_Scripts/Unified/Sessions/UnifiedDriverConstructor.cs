using Unity.Netcode;
using Unity.Networking.Transport.Relay;

/// <summary>
/// Builds Netcode for Entities drivers for a Multiplayer Services session in NGO unified mode.
/// Registers one UDP driver per world, using the Relay allocation when there is one.
/// A Single World host has no client driver (its own connection is in-world), so no IPC driver is needed.
/// Installed by <see cref="UnifiedNetworkHandler"/> before NGO creates the world, because drivers are built when the world is created.
/// </summary>
class UnifiedDriverConstructor : INetworkStreamDriverConstructor
{
    public bool UseRelay;
    public RelayServerData RelayData;

    public void CreateClientDriver(NetcodeWorld world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkClientSettings();
        if (UseRelay) settings = settings.WithRelayParameters(ref RelayData);
        DefaultDriverBuilder.RegisterClientUdpDriver(world, ref driverStore, netDebug, settings);
    }

    public void CreateServerDriver(NetcodeWorld world, ref NetworkDriverStore driverStore, NetDebug netDebug)
    {
        var settings = DefaultDriverBuilder.GetNetworkServerSettings();
        if (UseRelay) settings = settings.WithRelayParameters(ref RelayData);
        DefaultDriverBuilder.RegisterServerUdpDriver(world, ref driverStore, netDebug, settings);
    }
}
