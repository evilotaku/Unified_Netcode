using System.Reflection;
using System.Threading.Tasks;
using Unity.Entities;
using Unity.Netcode;
using Unity.Networking.Transport;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// Multiplayer Services network handler for NGO 3.0 unified mode (NGO + Netcode for Entities in one Single World).
/// The built-in NGO handler sets Relay data on UnityTransport, which unified mode replaces with UnifiedNetcodeTransport,
/// and the built-in Entities handler expects separate client and server worlds. This handler instead:
/// 1. installs a <see cref="UnifiedDriverConstructor"/> carrying the Relay allocation,
/// 2. turns off UnifiedBootstrap's auto listen/connect on port 7979,
/// 3. starts NGO (which creates the world and its drivers), then listens or connects on the session's endpoint.
/// Use it with <c>options.WithNetworkHandler(UnifiedNetworkHandler.Instance)</c>.
/// </summary>
public class UnifiedNetworkHandler : INetworkHandler
{
    public static readonly UnifiedNetworkHandler Instance = new();

    // Makes the Blocks session UI (Quick Join, Create, Join by code, Browser) use this handler.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void RegisterWithSessionBlocks() => Blocks.Sessions.Common.SessionSettings.NetworkHandlerOverride = Instance;

    const float k_ConnectTimeoutSeconds = 15f;

    // UnifiedBootstrap is internal to NGO; its Port is copied into ClientServerBootstrap.AutoConnectPort when the world is created.
    static readonly FieldInfo s_BootstrapPortField = typeof(NetworkManager).Assembly
        .GetType("Unity.Netcode.UnifiedBootstrap")?
        .GetField("Port", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

    readonly UnifiedDriverConstructor m_DriverConstructor = new();
    INetworkStreamDriverConstructor m_PreviousDriverConstructor;
    ushort m_PreviousBootstrapPort;
    bool m_Overridden;

    public async Task StartAsync(NetworkConfiguration configuration)
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null)
            throw Fail("No NetworkManager in the scene.");
        if (networkManager.IsListening)
            throw Fail("NetworkManager is already running.");

        m_DriverConstructor.UseRelay = configuration.Type == NetworkType.Relay;
        m_DriverConstructor.RelayData = configuration.Role == NetworkRole.Client ? configuration.RelayClientData : configuration.RelayServerData;
        ApplyOverrides();

        try
        {
            switch (configuration.Role)
            {
                case NetworkRole.Host:
                    if (!networkManager.StartHost()) throw Fail("NetworkManager.StartHost failed.");
                    Listen(networkManager, configuration);
                    break;
                case NetworkRole.Server:
                    if (!networkManager.StartServer()) throw Fail("NetworkManager.StartServer failed.");
                    Listen(networkManager, configuration);
                    break;
                case NetworkRole.Client:
                    if (!networkManager.StartClient()) throw Fail("NetworkManager.StartClient failed.");
                    await ConnectAsync(networkManager, configuration);
                    break;
            }
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager != null && (networkManager.IsListening || networkManager.ShutdownInProgress))
        {
            networkManager.Shutdown();
            while (networkManager != null && networkManager.ShutdownInProgress)
                await Awaitable.NextFrameAsync();
        }
        RestoreOverrides();
    }

    static void Listen(NetworkManager networkManager, NetworkConfiguration configuration)
    {
        var endpoint = configuration.Type == NetworkType.Relay
            ? NetworkEndpoint.AnyIpv4 // Relay binds any local port; the relay server is the public address.
            : configuration.DirectNetworkListenAddress;

        ref var driver = ref GetDriver(networkManager).ValueRW;
        if (!driver.Listen(endpoint))
            throw Fail($"Failed to listen on {endpoint}.");

        if (configuration.Type == NetworkType.Direct)
            configuration.UpdatePublishPort(driver.GetLocalEndPoint().Port);
    }

    static async Task ConnectAsync(NetworkManager networkManager, NetworkConfiguration configuration)
    {
        var endpoint = configuration.Type == NetworkType.Relay
            ? configuration.RelayClientData.Endpoint
            : configuration.DirectNetworkPublishAddress;

        Connect(networkManager, endpoint);

        // IsConnectedClient turns true once the Entities connection is up and NGO's own handshake has completed.
        var deadline = Time.realtimeSinceStartup + k_ConnectTimeoutSeconds;
        while (!networkManager.IsConnectedClient)
        {
            if (!networkManager.IsListening)
                throw Fail($"Disconnected while connecting to {endpoint}.");
            if (Time.realtimeSinceStartup > deadline)
                throw Fail($"Timed out connecting to {endpoint}.");
            await Awaitable.NextFrameAsync();
        }
    }

    // Not inside ConnectAsync: async methods can't hold ref locals in Unity's C# version.
    static void Connect(NetworkManager networkManager, NetworkEndpoint endpoint)
    {
        ref var driver = ref GetDriver(networkManager).ValueRW;
        if (driver.Connect(networkManager.NetcodeWorld.EntityManager, endpoint) == Entity.Null)
            throw Fail($"Failed to connect to {endpoint}.");
    }

    static RefRW<NetworkStreamDriver> GetDriver(NetworkManager networkManager)
    {
        var world = networkManager.NetcodeWorld;
        if (world == null || !world.IsCreated)
            throw Fail("NGO didn't create a Netcode world. Check that the NetworkConfig has ghost prefabs and NetcodeConfig is Single World.");
        using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
        return query.GetSingletonRW<NetworkStreamDriver>();
    }

    void ApplyOverrides()
    {
        if (m_Overridden) return;
        if (s_BootstrapPortField == null)
            throw Fail("Couldn't find UnifiedBootstrap.Port in NGO, so this NGO version isn't supported.");

        m_PreviousDriverConstructor = NetworkStreamReceiveSystem.DriverConstructor;
        NetworkStreamReceiveSystem.DriverConstructor = m_DriverConstructor;

        // Port 0 stops the new world from auto-listening/auto-connecting on 7979; this handler does it on the session's endpoint.
        m_PreviousBootstrapPort = (ushort)s_BootstrapPortField.GetValue(null);
        s_BootstrapPortField.SetValue(null, (ushort)0);
        m_Overridden = true;
    }

    void RestoreOverrides()
    {
        if (!m_Overridden) return;
        NetworkStreamReceiveSystem.DriverConstructor = m_PreviousDriverConstructor;
        s_BootstrapPortField.SetValue(null, m_PreviousBootstrapPort);
        m_Overridden = false;
    }

    static SessionException Fail(string message) =>
        new($"[{nameof(UnifiedNetworkHandler)}] {message}", SessionError.NetworkSetupFailed, null);
}
