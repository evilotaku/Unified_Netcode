namespace Unity.Netcode
{
    /// <summary>What the client wants to do with an item.</summary>
    public enum InteractAction : byte
    {
        PickUp = 0,
        Drop = 1,
        Throw = 2,
        PickUpPlayer = 3,
    }

    /// <summary>
    /// Client-to-server request to pick up, drop, or throw an item.
    /// Sent with Netcode.RPC.Invoke and consumed per connection by <see cref="InteractionServerSystem"/>,
    /// so the server always knows which client sent it.
    /// </summary>
    public struct InteractRequest : ICustomMessage
    {
        public byte Action;          // InteractAction, stored as a byte for RPC serialization
        public int ItemGhostId;      // only used for PickUp and PickUpPlayer (the target player's ghost)
        public uint ItemSpawnTick;   // NetworkTick.SerializedData, pairs with ItemGhostId to identify the ghost
        public int Hurtbox;          // only used for PickUpPlayer: the grabbed body part (index into the target's hurtboxes), -1 for none
    }
}
