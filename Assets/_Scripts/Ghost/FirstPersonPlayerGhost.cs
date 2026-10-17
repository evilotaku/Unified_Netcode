using Unity.Entities;
using Unity.Netcode;
using UnityEngine;

namespace Unity.Netcode
{
    public partial class FirstPersonPlayerGhost : GhostBehaviour
    {
        public GhostComponentRef<FirstPersonPlayer> PlayerComponent;

        void Start()
        {
            if (IsServer)
            {
                var em = Ghost.World.EntityManager;

                // Add simulation components that are needed for prediction/simulation but not synced
                if (!em.HasComponent<FirstPersonPlayerInputs>(Ghost.Entity))
                    em.AddComponentData(Ghost.Entity, new FirstPersonPlayerInputs());

                if (!em.HasComponent<FirstPersonPlayerNetworkInput>(Ghost.Entity))
                    em.AddComponentData(Ghost.Entity, new FirstPersonPlayerNetworkInput());
            }
        }
    }
}