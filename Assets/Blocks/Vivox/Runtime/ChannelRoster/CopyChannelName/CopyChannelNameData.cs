using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    public class CopyChannelNameData : IDisposable, IDataSourceViewHashProvider, INotifyBindablePropertyChanged
    {
        const string k_NoChannelText = "No channel joined";

        VivoxObserver m_VivoxObserver;
        bool m_HasChannelName;
        long m_UpdateVersion;

        [CreateProperty]
        public bool HasChannelName
        {
            get => m_HasChannelName;
            private set
            {
                if (m_HasChannelName == value)
                {
                    return;
                }

                m_HasChannelName = value;
                Notify();
            }
        }

        [CreateProperty]
        public string TargetChannelName
        {
            get => m_TargetChannelName;
            private set
            {
                if (m_TargetChannelName == value)
                {
                    return;
                }

                m_TargetChannelName = value;
                HasChannelName = m_TargetChannelName != k_NoChannelText;
                ++m_UpdateVersion;

                Notify();
                Notify(nameof(DisplayChannelName));
            }
        }
        string m_TargetChannelName = k_NoChannelText;

        [CreateProperty]
        public string DisplayChannelName => HasChannelName ? $"Channel Name: {m_TargetChannelName}" : k_NoChannelText;

        public CopyChannelNameData()
        {
            m_VivoxObserver = new VivoxObserver(VivoxObserverType.Channel);

            m_VivoxObserver.ChannelJoined += OnChannelJoined;
            m_VivoxObserver.ChannelLeft += OnChannelLeft;

            if (m_VivoxObserver.IsServiceInitialized && m_VivoxObserver.VivoxService?.ActiveChannels.Count > 0)
            {
                // If we already have an active channel, designate it as the target channel.
                OnChannelJoined(m_VivoxObserver.VivoxService?.ActiveChannels.First().Key);
            }
        }

        void OnChannelJoined(string channelName)
        {
            if (TargetChannelName == k_NoChannelText)
            {
                TargetChannelName = channelName;
            }
        }

        void OnChannelLeft(string channelName)
        {
            if (channelName == TargetChannelName)
            {
                TargetChannelName = k_NoChannelText;
            }
        }

        public void Dispose()
        {
            if (m_VivoxObserver != null)
            {
                m_VivoxObserver.ChannelJoined -= OnChannelJoined;
                m_VivoxObserver.ChannelLeft -= OnChannelLeft;
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
        void Notify([CallerMemberName] string property = null) =>
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
    }
}


