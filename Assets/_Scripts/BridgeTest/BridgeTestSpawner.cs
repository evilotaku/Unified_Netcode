using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Starts an NGO session from on-screen buttons and, on the server, spawns the bridge test prefab
/// through NGO (Instantiate + NetworkObject.Spawn). N4E replicates it as a ghost.
/// </summary>
public class BridgeTestSpawner : MonoBehaviour
{
    public NetworkObject Prefab;
    public int Count = 3;
    public float Spacing = 5f;

    NetworkManager m_NetworkManager;

    // Start, not OnEnable: NetworkManager.Singleton is only set once the NetworkManager itself has enabled.
    void Start()
    {
        m_NetworkManager = NetworkManager.Singleton;
        if (m_NetworkManager != null) m_NetworkManager.OnServerStarted += SpawnAll;
    }

    void OnDestroy()
    {
        if (m_NetworkManager != null) m_NetworkManager.OnServerStarted -= SpawnAll;
    }

    public void SpawnAll()
    {
        for (int i = 0; i < Count; i++)
        {
            var position = transform.position + Vector3.right * (i - (Count - 1) * 0.5f) * Spacing;
            var instance = Instantiate(Prefab, position, Quaternion.identity);
            instance.Spawn();
        }
    }

    void OnGUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        GUILayout.BeginArea(new Rect(10, 10, 220, 120));
        if (!nm.IsListening)
        {
            if (GUILayout.Button("Host")) nm.StartHost();
            if (GUILayout.Button("Client")) nm.StartClient();
        }
        else
        {
            GUILayout.Label(nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client");
            if (GUILayout.Button("Shutdown")) nm.Shutdown();
        }
        GUILayout.EndArea();
    }
}
