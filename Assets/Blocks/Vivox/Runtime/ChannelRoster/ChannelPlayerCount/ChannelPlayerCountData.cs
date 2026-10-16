using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    public class ChannelPlayerCountData : INotifyBindablePropertyChanged, IDataSourceViewHashProvider, IDisposable
    {
        const string k_DefaultDisplayText = "- Players";

        VivoxObserver m_VivoxObserver;
        long m_UpdateVersion;
        string m_DisplayText = k_DefaultDisplayText;
        string m_TargetChannelName = string.Empty;

        /// <summary>
        /// This property is bound to <see cref="ChannelPlayerCountLabel.text"/> so that the label displays the number of
        /// players in the session.
        /// It is a property using [CreateProperty] attribute to allow for data binding in UIToolkit
        /// <summary>
        [CreateProperty]
        public string DisplayText
        {
            get => m_DisplayText;
            set
            {
                if (m_DisplayText == value)
                {
                    return;
                }

                m_DisplayText = value;
                ++m_UpdateVersion;
                Notify();
            }
        }

        public ChannelPlayerCountData(string sessionType)
        {
            m_VivoxObserver = new VivoxObserver(VivoxObserverType.Channel);
            m_VivoxObserver.ChannelJoined += OnChannelJoined;
            m_VivoxObserver.ChannelLeft += OnChannelLeft;
            m_VivoxObserver.ParticipantJoined += OnParticipantJoined;
            m_VivoxObserver.ParticipantLeft += OnParticipantLeft;

            if (m_VivoxObserver.IsServiceInitialized && m_VivoxObserver.VivoxService?.ActiveChannels.Count > 0)
            {
                // If we already have an active channel, designate it as the target channel.
                OnChannelJoined(m_VivoxObserver.VivoxService?.ActiveChannels.First().Key);
                UpdatePlayerCountDisplayText();
            }
        }

        void OnChannelJoined(string channelName)
        {
            if (string.IsNullOrEmpty(m_TargetChannelName))
            {
                m_TargetChannelName = channelName;
            }
        }

        void OnChannelLeft(string channelName)
        {
            if (m_TargetChannelName == channelName)
            {
                m_TargetChannelName = string.Empty;
                DisplayText = k_DefaultDisplayText;
            }
        }

        void OnParticipantJoined(VivoxParticipant participant)
        {
            if (participant.ChannelName == m_TargetChannelName)
            {
                UpdatePlayerCountDisplayText();
            }
        }

        void OnParticipantLeft(VivoxParticipant participant)
        {
            if (participant.ChannelName == m_TargetChannelName)
            {
                UpdatePlayerCountDisplayText();
            }
        }

        void UpdatePlayerCountDisplayText()
        {
            if (!string.IsNullOrEmpty(m_TargetChannelName))
            {
                DisplayText = $"{m_VivoxObserver.VivoxService?.ActiveChannels[m_TargetChannelName].Count} Player(s)";
            }
        }

        public void Dispose()
        {
            if (m_VivoxObserver != null)
            {
                m_VivoxObserver.ChannelJoined -= OnChannelJoined;
                m_VivoxObserver.ChannelLeft -= OnChannelLeft;
                m_VivoxObserver.ParticipantJoined -= OnParticipantJoined;
                m_VivoxObserver.ParticipantLeft -= OnParticipantLeft;

                m_VivoxObserver.Dispose();
                m_VivoxObserver = null;
            }
        }

        /// <summary>
        /// This method is used by UIToolkit to determine if any data bound to the UI has changed.
        /// Instead of hashing the data, an m_UpdateVersion counter is incremented when changes occur.
        /// </summary>
        public long GetViewHashCode() => m_UpdateVersion;

        /// <summary>
        /// Suggested implementation of INotifyBindablePropertyChanged from UIToolkit.
        /// </summary>
        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;
        void Notify([CallerMemberName] string property = null)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
        }
    }
}
