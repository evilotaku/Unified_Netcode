using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Netcode;

namespace Unity.Netcode
{
    /// <summary>The four dash directions, relative to where the player faces. None is not dashing.</summary>
    public enum DashDirection { None, Forward, Back, Left, Right }

    // This holds the data gathered from the keyboard/mouse.
    // Every field is sent to the server as a command. None is a [GhostField]: that would also replicate each player's
    // input to every other client, which is only needed to predict remote players, and they're interpolated here.
    [Serializable]
    public struct FPSInputs : IInputComponentData
    {
        public float2 Move;
        public float2 Look;
        public InputEvent Jump;
        public InputEvent LightAttack;
        public InputEvent MediumAttack;
        public InputEvent HeavyAttack;
        public InputEvent SpecialAttack;
        public bool Block;          // held, not a one-shot event
        public InputEvent Dash;     // a direction was double-tapped
        public int DashDirection;   // which one, as a DashDirection
    }
}
