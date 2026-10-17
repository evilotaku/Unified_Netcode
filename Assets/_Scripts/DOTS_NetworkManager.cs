using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using Unity.Networking.Transport;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.UIElements;
using Random = Unity.Mathematics.Random;

public partial class DOTS_NetworkManager : MonoBehaviour
{
  


    [Header("Ghost Prefabs")]
    public List<GhostObject> GhostPrefabs;

    public GhostObject PlayerPrefab;

    [AutoStaticsCleanup]
    public static Action<Entity> OnInGame;

    private Random _random;

    void Start()
    {
        _random = Random.CreateFromIndex(0);
        Netcode.OnConnectionEvent += OnConnectionEvent;
        OnInGame += SpawnPlayerForClient;
    }

   /* public void StartHost(NetworkEndpoint endpoint)
    {
        Netcode.StartAsHost(endpoint);
    }

    public void StartClient(NetworkEndpoint endpoint)
    {
        Netcode.Connect(endpoint);
    }*/

    private void OnConnectionEvent(Connection connection, NetcodeConnectionEvent connectionEvent)
    {        
        if (connection.World.IsClient() && connectionEvent.State == ConnectionState.State.Connected)
        {            
            FindAnyObjectByType<PanelRenderer>().enabled = false;
            print("Client connected to server, sending join request");
            /* var rpcEntity = connection.World.EntityManager.CreateEntity();
            // Add the command and the request component
            connection.World.EntityManager.AddComponentData(rpcEntity, new FPSClientJoinRequest());
            connection.World.EntityManager.AddComponentData(rpcEntity, new SendRpcCommandRequest
            {
                TargetConnection = Entity.Null, // Send to server
            });*/
            // The server works out which connection sent this, so no client-side entity is passed.
            Netcode.RPC.Invoke(new GoInGameRequest(), connection.World, SendDirection.ClientToServer);
            connection.World.EntityManager.AddComponentData(connection.ConnectionEntity, new NetworkStreamInGame());
            connection.EnableGhostReplication(true);
        }
    }


    private void SpawnPlayerForClient(Entity connectionEntity)
    {        
        if(!Netcode.IsServerRole) return;
        print("Spawning player for new client");
        var connection = Netcode.AllConnections.Find(c => c.ConnectionEntity == connectionEntity);
        var player = Instantiate(PlayerPrefab,(Vector3)_random.NextFloat3(), quaternion.identity);
        var ghostPlayer = player.GetComponent<GhostObject>();
        ghostPlayer.OwnerNetworkId = connection.NetworkId;       
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}

