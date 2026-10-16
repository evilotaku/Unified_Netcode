using UnityEngine;
using Unity.Services.Vivox;

namespace Blocks.Vivox
{
    [CreateAssetMenu(fileName = nameof(ChannelSettings), menuName = "Services/Blocks/Vivox/" + nameof(ChannelSettings))]
    public class ChannelSettings : ScriptableObject
    {
        [Header("Vivox Channel options")]
        [Tooltip("Group: all participants hear and speak with each other. Echo: your audio is looped back to yourself only — useful for microphone testing.")]
        public ChannelType ChannelType;
        [Tooltip("Controls whether the channel supports voice, text, or both. Use AudioOnly for voice-only sessions, TextOnly for chat-only, or TextAndAudio to enable both.")]
        public ChatCapability ChatCapability;
    }

    public enum ChannelType
    {
        Echo,
        Group
    }

}

