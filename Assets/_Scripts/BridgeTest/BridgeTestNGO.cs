using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NGO half of the bridge test prefab: a NetworkVariable the server bumps every second,
/// and a server RPC each client sends on spawn so the server logs who sent it.
/// </summary>
public class BridgeTestNGO : NetworkBehaviour
{
    public NetworkVariable<int> Counter = new();

    float m_NextTick;

    public override void OnNetworkSpawn()
    {
        Debug.Log($"[BridgeTestNGO] {name} spawned: NetworkObjectId={NetworkObjectId} IsServer={IsServer} IsClient={IsClient} Owner={OwnerClientId}");
        Counter.OnValueChanged += OnCounterChanged;
        if (IsClient) PingRpc(Time.frameCount);
    }

    public override void OnNetworkDespawn()
    {
        Counter.OnValueChanged -= OnCounterChanged;
    }

    void Update()
    {
        if (!IsServer || Time.time < m_NextTick) return;
        m_NextTick = Time.time + 1f;
        Counter.Value++;
    }

    void OnCounterChanged(int previous, int current)
    {
        if (!IsServer) Debug.Log($"[BridgeTestNGO] {name} Counter {previous} -> {current}");
    }

    [Rpc(SendTo.Server)]
    void PingRpc(int frame, RpcParams rpcParams = default)
    {
        Debug.Log($"[BridgeTestNGO] {name} ping from client {rpcParams.Receive.SenderClientId} (frame {frame})");
    }
}
