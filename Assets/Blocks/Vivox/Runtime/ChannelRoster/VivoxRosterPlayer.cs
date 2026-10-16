using System;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [Serializable]
    public class VivoxRosterPlayer : INotifyBindablePropertyChanged
    {
        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        private string m_PlayerId;
        private string m_PlayerName;
        private VivoxParticipant m_Participant;

        [CreateProperty]
        public string PlayerId
        {
            get => m_PlayerId;
            set
            {
                if (m_PlayerId == value)
                {
                    return;
                }
                m_PlayerId = value;
                NotifyPropertyChanged();
            }
        }

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
                NotifyPropertyChanged();
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
                m_Participant = value;
                NotifyPropertyChanged();
            }
        }

        public bool IsSpeaking => m_Participant?.SpeechDetected ?? false;
        public bool IsMuted => m_Participant?.IsMuted ?? false;
        public bool IsSelf => m_Participant?.IsSelf ?? false;
        public double AudioEnergy => m_Participant?.AudioEnergy ?? 0.0;

        private void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(propertyName));
        }
    }
}
