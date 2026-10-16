using System;
using Unity.Cinemachine;
using Unity.Entities;
using Unity.Netcode;
using UnityEngine;

public class LocalCameraLink : MonoBehaviour
{
    private bool _linked;

    void Update()
    {
        if (_linked) return;

        var ghost = GetComponent<GhostObject>();
        if(ghost.OwnerNetworkId == Netcode.LocalConnection.NetworkId)
        {
            if (ghost != null && ghost.World != null && ghost.World.IsCreated)
            {
                var em = ghost.World.EntityManager;
                if (em.HasComponent<GhostOwnerIsLocal>(ghost.Entity) && em.IsComponentEnabled<GhostOwnerIsLocal>(ghost.Entity))
                {
                    var vcam = FindAnyObjectByType<CinemachineCamera>();
                    if (vcam != null)
                    {
                        var target = GetComponent<FPSControllerGhost>()?.CameraTarget;
                        if (target != null)
                        {
                            vcam.Follow = target;
                            vcam.LookAt = target;
                            _linked = true;
                        }
                    }
                }
            }
        }
    }
}
