using Unity.Netcode;

// Part of the RPC attribute test (see RpcAttributeTest.cs). In its own file because Unity only
// serializes a MonoBehaviour whose class name matches its file name.

/// <summary>[RPC] methods on a GhostBehaviour. The body runs on the same ghost on the receiving side.</summary>
public partial class RpcTestGhost : GhostBehaviour
{
    [RPC(SendDirection.ClientToServer)]
    public void ClientToServer(int value)
    {
        RpcTestLog.Add($"RpcTestGhost.ClientToServer value={value} ghost={Ghost.GhostId} world={Ghost.World.Name} server={IsServer} client={IsClient}");
    }

    [RPC(SendDirection.ServerToClient)]
    public void ServerToClient(int value)
    {
        RpcTestLog.Add($"RpcTestGhost.ServerToClient value={value} ghost={Ghost.GhostId} world={Ghost.World.Name} server={IsServer} client={IsClient}");
    }
}
