using System;
using System.Runtime.CompilerServices;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    public class VivoxRosterListItemData : IDisposable, INotifyBindablePropertyChanged
    {
        string m_PlayerName;
        string m_IconClass;
        string m_ButtonText;
        bool m_IsMuted;
        bool m_HasVoice;
        bool m_IsInAudio;
        VivoxParticipant m_Participant;
        bool m_IsSpeaking;
        bool m_LocalUserIsInAudio;

        [CreateProperty]
        public string PlayerName
        {
            get => m_PlayerName;
            set
            {
                if (m_PlayerName == value)
                {
                    return;
                }
                m_PlayerName = value;
                Notify();
            }
        }

        [CreateProperty]
        public VivoxParticipant Participant
        {
            get => m_Participant;
            set
            {
                if (m_Participant == value)
                {
                    return;
                }

                UnsubscribeFromParticipantEvents();

                m_Participant = value;

                SubscribeToParticipantEvents();

                UpdateParticipant();
                Notify();
            }
        }

        [CreateProperty]
        public bool IsMuted
        {
            get => m_IsMuted;
            set
            {
                if (m_IsMuted == value)
                {
                    return;
                }
                m_IsMuted = value;
                Notify();
            }
        }

        [CreateProperty]
        public bool IsSpeaking
        {
            get => m_IsSpeaking;
            set
            {
                if (m_IsSpeaking == value)
                {
                    return;
                }
                m_IsSpeaking = value;
                SetIconClass();
                Notify();
            }
        }

        [CreateProperty]
        public string IconClass
        {
            get => m_IconClass;
            set
            {
                if (m_IconClass == value)
                {
                    return;
                }
                m_IconClass = value;
                Notify();
            }
        }

        [CreateProperty]
        public bool HasVoice
        {
            get => m_HasVoice;
            set
            {
                if (m_HasVoice == value)
                {
                    return;
                }
                m_HasVoice = value;
                Notify();
            }
        }

        [CreateProperty]
        public bool IsInAudio
        {
            get => m_IsInAudio;
            set
            {
                if (m_IsInAudio == value)
                {
                    return;
                }
                m_IsInAudio = value;
                Notify();
            }
        }

        public bool LocalUserIsInAudio
        {
            get => m_LocalUserIsInAudio;
            set
            {
                if (m_LocalUserIsInAudio == value)
                {
                    return;
                }
                m_LocalUserIsInAudio = value;
                UpdateParticipant();
            }
        }

        [CreateProperty]
        public string ButtonText
        {
            get => m_ButtonText;
            set
            {
                if (m_ButtonText == value)
                {
                    return;
                }
                m_ButtonText = value;
                Notify();
            }
        }

        void SubscribeToParticipantEvents()
        {
            if (m_Participant != null)
            {
                m_Participant.ParticipantSpeechDetected += OnSpeechDetected;
                m_Participant.ParticipantMuteStateChanged += OnMuteStateChanged;
                m_Participant.ParticipantAudioStateChanged += UpdateParticipant;
            }
        }

        void UnsubscribeFromParticipantEvents()
        {
            if (m_Participant != null)
            {
                m_Participant.ParticipantSpeechDetected -= OnSpeechDetected;
                m_Participant.ParticipantMuteStateChanged -= OnMuteStateChanged;
                m_Participant.ParticipantAudioStateChanged -= UpdateParticipant;
            }
        }

        void OnSpeechDetected()
        {
            if (m_Participant != null)
            {
                IsSpeaking = m_Participant.SpeechDetected;
            }
        }

        void OnMuteStateChanged()
        {
            if (m_Participant != null)
            {
                IsMuted = m_Participant.IsMuted;
                UpdateParticipant();
            }
        }

        void UpdateParticipant()
        {
            if (m_Participant == null)
            {
                PlayerName = string.Empty;
                HasVoice = false;
                IsInAudio = false;
                IconClass = string.Empty;
                ButtonText = string.Empty;
                IsMuted = false;
                IsSpeaking = false;
                return;
            }

            PlayerName = m_Participant.DisplayName ?? m_Participant.PlayerId;
            IsInAudio = m_Participant.IsInAudio;

            if (m_Participant.IsInAudio && m_LocalUserIsInAudio)
            {
                IsMuted = m_Participant.IsMuted;
                IsSpeaking = m_Participant.SpeechDetected;
                HasVoice = true;
                ButtonText = IsMuted ? "Unmute" : "Mute";
            }
            else
            {
                HasVoice = false;
                ButtonText = string.Empty;
                IsMuted = false;
                IsSpeaking = false;
            }

            SetIconClass();
        }

        void SetIconClass()
        {
            if (m_Participant == null || !m_Participant.IsInAudio)
            {
                IconClass = string.Empty;
                return;
            }

            if (IsMuted)
            {
                IconClass = VivoxTheme.IconSpeakerMuted;
                return;
            }

            IconClass = IsSpeaking ? VivoxTheme.IconSpeakerActive : VivoxTheme.IconSpeaker;
        }

        public void ToggleMute()
        {
            if (m_Participant == null || !m_Participant.IsInAudio || !m_LocalUserIsInAudio)
            {
                return;
            }

            try
            {
                if (m_Participant.IsMuted)
                {
                    m_Participant.UnmutePlayerLocally();
                }
                else
                {
                    m_Participant.MutePlayerLocally();
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"Failed to toggle mute for participant {PlayerName}: {ex.Message}");
            }
        }

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        void Notify([CallerMemberName] string property = null)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
        }

        public void Dispose()
        {
            UnsubscribeFromParticipantEvents();
            m_Participant = null;
        }
    }
}
