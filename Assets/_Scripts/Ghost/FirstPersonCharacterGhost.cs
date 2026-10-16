using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;
using UnityEngine;
using Unity.CharacterController;

namespace Unity.Netcode
{
    public partial class FirstPersonCharacterGhost : GhostBehaviour
    {
        public GhostComponentRef<FirstPersonCharacterComponent> CharacterComponent;
        public Transform ViewChild;

        void Start()
        {
            // Server-side initialization
            if (IsServer)
            {
                var em = Ghost.World.EntityManager;
                var auth = GetComponent<FirstPersonCharacterAuthoring>();

                if (auth != null)
                {
                    // Copy settings from the Authoring component to the networked Ghost Component
                    CharacterComponent.Value = new FirstPersonCharacterComponent
                    {
                        GroundMaxSpeed = auth.GroundMaxSpeed,
                        GroundedMovementSharpness = auth.GroundedMovementSharpness,
                        AirAcceleration = auth.AirAcceleration,
                        AirMaxSpeed = auth.AirMaxSpeed,
                        AirDrag = auth.AirDrag,
                        JumpSpeed = auth.JumpSpeed,
                        Gravity = auth.Gravity,
                        PreventAirAccelerationAgainstUngroundedHits = auth.PreventAirAccelerationAgainstUngroundedHits,
                        StepAndSlopeHandling = auth.StepAndSlopeHandling,
                        MinViewAngle = auth.MinViewAngle,
                        MaxViewAngle = auth.MaxViewAngle,

                        ViewPitchDegrees = 0f,
                        ViewLocalRotation = quaternion.identity
                    };
                }

                // Ensure the control component used by systems exists on the entity
                if (!em.HasComponent<FirstPersonCharacterControl>(Ghost.Entity))
                {
                    em.AddComponentData(Ghost.Entity, new FirstPersonCharacterControl());
                }
            }
        }

        void Update()
        {
            // Apply network-synced rotation to the view child
            if (ViewChild != null)
            {
                ViewChild.localRotation = CharacterComponent.Value.ViewLocalRotation;
            }

            // Local Player Only: Snap the Main Camera to the View child
            if (Ghost.World.EntityManager.HasComponent<GhostOwnerIsLocal>(Ghost.Entity))
            {
                if (Camera.main != null && ViewChild != null)
                {
                    Camera.main.transform.SetPositionAndRotation(
                        ViewChild.position,
                        ViewChild.rotation
                    );
                }
            }
        }
    }
}