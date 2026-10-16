using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server side: spawns a bridged player (NetworkObject + GhostObject) for every NGO client.
/// NGO does not map its client ids to Netcode for Entities NetworkIds, so this sets the ghost owner
/// itself before spawning. Ghost ownership drives prediction, GhostOwnerIsLocal and PlayerInteractor.
/// Leave NetworkConfig.PlayerPrefab empty so NGO doesn't also spawn a player.
/// </summary>
public class UnifiedPlayerSpawner : MonoBehaviour
{
    public NetworkObject PlayerPrefab;
    [Tooltip("Players spawn at a random point within this radius of this transform.")]
    public float SpawnRadius = 2f;

    NetworkManager m_NetworkManager;
    readonly List<ulong> m_Pending = new();
    Unity.Mathematics.Random m_Random = Unity.Mathematics.Random.CreateFromIndex(0);

    // Start, not OnEnable: NetworkManager.Singleton is only set once the NetworkManager itself has enabled.
    void Start()
    {
        m_NetworkManager = NetworkManager.Singleton;
        if (m_NetworkManager == null) return;
        m_NetworkManager.OnClientConnectedCallback += OnClientConnected;
        m_NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
    }

    void OnDestroy()
    {
        if (m_NetworkManager == null) return;
        m_NetworkManager.OnClientConnectedCallback -= OnClientConnected;
        m_NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    void OnClientConnected(ulong clientId)
    {
        if (m_NetworkManager.IsServer) m_Pending.Add(clientId);
    }

    void OnClientDisconnected(ulong clientId) => m_Pending.Remove(clientId);

    void Update()
    {
        if (m_Pending.Count == 0 || m_NetworkManager == null || !m_NetworkManager.IsServer) return;

        // Retry each frame until the client's N4E connection can be resolved.
        for (int i = m_Pending.Count - 1; i >= 0; i--)
        {
            var clientId = m_Pending[i];
            if (!TryGetNetworkId(clientId, out var networkId)) continue;
            m_Pending.RemoveAt(i);
            SpawnPlayer(clientId, networkId);
        }
    }

    void SpawnPlayer(ulong clientId, int networkId)
    {
        var offset = m_Random.NextFloat2Direction() * m_Random.NextFloat(0f, SpawnRadius);
        var position = transform.position + new Vector3(offset.x, 0f, offset.y);
        var player = Instantiate(PlayerPrefab, position, Quaternion.identity);

        // Must be set before the ghost is replicated, so owner prediction and GhostOwnerIsLocal are right from the first snapshot.
        player.GetComponent<GhostObject>().OwnerNetworkId = new NetworkId { Value = networkId };
        player.SpawnAsPlayerObject(clientId);
        Debug.Log($"[UnifiedPlayerSpawner] Spawned player for NGO client {clientId} (N4E NetworkId {networkId})");
    }

    /// <summary>
    /// Server only. The unified transport uses the N4E NetworkId as the transport id. The host has no
    /// transport id of its own, so its NetworkId comes from the world's local connection instead.
    /// </summary>
    bool TryGetNetworkId(ulong clientId, out int networkId)
    {
        networkId = 0;
        var world = m_NetworkManager.NetcodeWorld;
        if (world == null || !world.IsCreated) return false;

        if (clientId == NetworkManager.ServerClientId)
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<LocalConnection>());
            using var ids = query.ToComponentDataArray<NetworkId>(Allocator.Temp);
            if (ids.Length == 0) return false;
            networkId = ids[0].Value;
        }
        else
        {
            networkId = (int)m_NetworkManager.GetTransportIdFromClientId(clientId);
        }
        return networkId > 0;
    }
}
