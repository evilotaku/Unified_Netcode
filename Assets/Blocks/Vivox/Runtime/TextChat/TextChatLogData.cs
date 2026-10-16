using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    public class TextChatLogData : IDisposable, INotifyBindablePropertyChanged
    {
        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        const int k_ListSize = 10;
        const int k_Interval = -10;
        VivoxObserver m_VivoxObserver;
        HashSet<string> m_UniqueMessageIds = new HashSet<string>();
        string m_ChannelName;
        bool m_IsLoading;
        List<VivoxMessage> m_Messages = new List<VivoxMessage>();
        bool m_EndOfList = false;
        bool m_IsDisposed = false;

        [CreateProperty]
        public bool EndOfList
        {
            get => m_EndOfList;
            set
            {
                if (m_EndOfList == value)
                {
                    return;
                }

                m_EndOfList = value;
                Notify();
            }
        }

        [CreateProperty]
        public bool IsLoading
        {
            get => m_IsLoading;
            set
            {
                if (m_IsLoading == value)
                {
                    return;
                }

                m_IsLoading = value;
                Notify();
            }
        }

        [CreateProperty]
        public List<VivoxMessage> Messages
        {
            get => m_Messages;
            set
            {
                if (!m_VivoxObserver.IsServiceInitialized || m_Messages == value)
                {
                    return;
                }

                m_Messages = value;
                Notify();
            }
        }

        [CreateProperty]
        public string TargetChannelName
        {
            get => m_ChannelName;
            set
            {
                if (!m_VivoxObserver.IsServiceInitialized || m_ChannelName == value)
                {
                    return;
                }

                m_ChannelName = value;
                Notify();
            }
        }

        public TextChatLogData()
        {
            m_VivoxObserver = new VivoxObserver(VivoxObserverType.Channel | VivoxObserverType.ChannelMessages);
            if (m_VivoxObserver.IsServiceInitialized)
            {
                OnVivoxReady();
            }
            else
            {
                m_VivoxObserver.ServiceInitialized += OnVivoxReady;
            }
        }

        void OnVivoxReady()
        {
            m_VivoxObserver.ServiceInitialized -= OnVivoxReady;

            m_VivoxObserver.ChannelMessageReceived += MessageReceived;
            m_VivoxObserver.ChannelJoined += OnChannelJoined;
            m_VivoxObserver.ChannelLeft += OnChannelLeft;
            m_VivoxObserver.ParticipantJoined += OnParticipantJoined;

            // Replay any channel already joined when this data source was created
            if (m_VivoxObserver.VivoxService.ActiveChannels.Count > 0)
            {
                OnChannelJoined(m_VivoxObserver.VivoxService.ActiveChannels.Keys.First());
            }
        }

        void OnChannelJoined(string channelName)
        {
            if (!string.IsNullOrEmpty(TargetChannelName))
            {
                return;
            }

            // ChannelJoined fires before participants are added, so self may not be present yet.
            // Check anyway to handle the replay case (data created after channel was already joined).
            var channels = m_VivoxObserver.VivoxService?.ActiveChannels;
            if (channels == null || !channels.TryGetValue(channelName, out var participants))
            {
                return;
            }

            var self = participants?.FirstOrDefault(p => p.IsSelf);
            if (self?.IsInText != true)
            {
                return;
            }

            TargetChannelName = channelName;
            _ = LoadPreviousMessages();
        }

        void OnParticipantJoined(VivoxParticipant participant)
        {
            // ChannelJoined fires before participants are populated; handle the live-join case here.
            if (!participant.IsSelf || !string.IsNullOrEmpty(TargetChannelName))
            {
                return;
            }

            if (participant.IsInText != true)
            {
                return;
            }

            TargetChannelName = participant.ChannelName;
            _ = LoadPreviousMessages();
        }

        void OnChannelLeft(string channelName)
        {
            if (TargetChannelName != channelName)
            {
                return;
            }

            m_ChannelName = string.Empty;
            Notify(nameof(TargetChannelName));
            m_Messages = new List<VivoxMessage>();
            Notify(nameof(Messages));
            m_UniqueMessageIds = new HashSet<string>();
            m_EndOfList = false;
        }

        public async Task LoadPreviousMessages()
        {
            if (m_EndOfList || m_IsLoading || !UnityEngine.Application.isPlaying)
            {
                return;
            }

            IsLoading = true;

            try
            {
                var alreadyHasRecords = Messages.Count > 0;
                var options = new ChatHistoryQueryOptions()
                {
                    TimeEnd = alreadyHasRecords ? Messages.First().ReceivedTime : DateTime.Now
                };

                var messages = await m_VivoxObserver.VivoxService.GetChannelTextMessageHistoryAsync(TargetChannelName, k_ListSize, options);

                if (m_IsDisposed)
                {
                    return;
                }

                if (messages.Count == 0)
                {
                    m_EndOfList = true;
                    EndOfList = true;
                    IsLoading = false;
                    return;
                }

                if (alreadyHasRecords)
                {
                    Messages.InsertRange(0, messages);
                }
                else
                {
                    Messages.AddRange(messages);
                }

                IsLoading = false;
                Messages = Messages.ToList();
            }
            catch (Exception e)
            {
                if (!m_IsDisposed)
                {
                    Debug.LogError(e);
                    IsLoading = false;
                }
            }
        }

        void MessageReceived(VivoxMessage message)
        {
            if (message.ChannelName != TargetChannelName || !m_UniqueMessageIds.Add(message.MessageId))
            {
                return;
            }

            Messages.Add(message);
            Messages = Messages.ToList();
        }

        void Notify([CallerMemberName] string property = null)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
        }

        public void Dispose()
        {
            m_IsDisposed = true;
            m_ChannelName = string.Empty;
            m_Messages = new List<VivoxMessage>();
            m_UniqueMessageIds = new HashSet<string>();
            m_EndOfList = false;
            m_IsLoading = false;

            if (m_VivoxObserver != null)
            {
                m_VivoxObserver.ServiceInitialized -= OnVivoxReady;
                m_VivoxObserver.ChannelMessageReceived -= MessageReceived;
                m_VivoxObserver.ChannelJoined -= OnChannelJoined;
                m_VivoxObserver.ChannelLeft -= OnChannelLeft;
                m_VivoxObserver.ParticipantJoined -= OnParticipantJoined;
                m_VivoxObserver.Dispose();
                m_VivoxObserver = null;
            }
        }
    }
}
