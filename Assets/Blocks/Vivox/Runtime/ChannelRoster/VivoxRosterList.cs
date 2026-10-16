using System.Collections.Generic;
using System.Linq;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class VivoxRosterList : VisualElement
    {
        ScrollView m_ScrollView;
        VisualElement m_ItemContainer;
        Label m_EmptyLabel;

        VivoxObserver m_VivoxObserver;

        readonly List<VivoxParticipant> m_Participants = new();
        readonly Dictionary<string, VivoxRosterListItem> m_PlayerItems = new();
        VivoxParticipant m_SelfParticipant;

        [CreateProperty]
        public string EmptyMessage { get; set; } = "No players in channel";

        public VivoxRosterList()
        {
            CreateUI();

            m_VivoxObserver = new VivoxObserver(VivoxObserverType.Login | VivoxObserverType.Channel);

            if (m_VivoxObserver.IsServiceInitialized)
            {
                OnVivoxReady();
            }
            else
            {
                m_VivoxObserver.ServiceInitialized += OnVivoxReady;
            }

            RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
        }

        void CreateUI()
        {
            if (m_ScrollView != null)
            {
                return;
            }

            AddToClassList(VivoxTheme.RosterList);
            AddToClassList(VivoxTheme.FullWidth);

            m_ScrollView = new ScrollView();
            m_ScrollView.AddToClassList(VivoxTheme.RosterListScrollView);

            m_ItemContainer = new VisualElement();
            m_ItemContainer.AddToClassList(VivoxTheme.RosterListItemContainer);

            m_EmptyLabel = new Label(EmptyMessage);
            m_EmptyLabel.AddToClassList(VivoxTheme.RosterListEmptyLabel);

            m_ScrollView.Add(m_ItemContainer);
            Add(m_ScrollView);

            UpdateEmptyState();
        }

        void OnAttachToPanel(AttachToPanelEvent evt)
        {
            if (!UnityEngine.Application.isPlaying)
            {
                return;
            }

            if (m_VivoxObserver != null && m_VivoxObserver.IsServiceInitialized)
            {
                RefreshParticipantList();
            }
        }

        void OnDetachFromPanel(DetachFromPanelEvent evt)
        {
            ClearParticipants();
            CleanupVivox();
        }

        void OnVivoxReady()
        {
            m_VivoxObserver.ServiceInitialized -= OnVivoxReady;

            m_VivoxObserver.ParticipantJoined += OnParticipantJoined;
            m_VivoxObserver.ParticipantLeft += OnParticipantLeft;

            m_VivoxObserver.ChannelLeft += OnChannelLeft;
            m_VivoxObserver.LoggedOut += OnLoggedOut;

            RefreshParticipantList();
        }

        void OnParticipantJoined(VivoxParticipant participant)
        {
            AddParticipant(participant);
        }

        void OnParticipantLeft(VivoxParticipant participant)
        {
            if (participant != null)
            {
                RemoveParticipant(participant.PlayerId);
            }
        }

        void OnChannelLeft(string channelName)
        {
            ClearParticipants();
        }

        void OnLoggedOut()
        {
            ClearParticipants();
        }

        void RefreshParticipantList()
        {
            ClearParticipants();

            if (m_VivoxObserver?.VivoxService?.ActiveChannels == null)
            {
                return;
            }

            var activeChannel = m_VivoxObserver.VivoxService.ActiveChannels.FirstOrDefault();
            if (activeChannel.Value == null)
            {
                return;
            }

            foreach (var participant in activeChannel.Value)
            {
                AddParticipant(participant);
            }
        }

        void AddParticipant(VivoxParticipant participant)
        {
            if (participant == null || m_PlayerItems.ContainsKey(participant.PlayerId))
            {
                return;
            }

            if (participant.IsSelf)
            {
                m_SelfParticipant = participant;
                m_SelfParticipant.ParticipantAudioStateChanged += OnSelfAudioStateChanged;
                SetLocalUserIsInAudio(m_SelfParticipant.IsInAudio);
            }

            m_Participants.Add(participant);

            var listItem = new VivoxRosterListItem();
            listItem.AddToClassList(VivoxTheme.RosterListListItem);
            listItem.LocalUserIsInAudio = m_SelfParticipant?.IsInAudio ?? false;
            m_PlayerItems[participant.PlayerId] = listItem;

            m_ItemContainer.Add(listItem);

            schedule.Execute(() =>
            {
                listItem.Participant = participant;
            });

            UpdateEmptyState();
        }

        void OnSelfAudioStateChanged()
        {
            if (m_SelfParticipant != null)
            {
                SetLocalUserIsInAudio(m_SelfParticipant.IsInAudio);
            }
        }

        void SetLocalUserIsInAudio(bool isInAudio)
        {
            foreach (var item in m_PlayerItems.Values)
            {
                item.LocalUserIsInAudio = isInAudio;
            }
        }

        void RemoveParticipant(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || !m_PlayerItems.ContainsKey(playerId))
            {
                return;
            }

            var listItem = m_PlayerItems[playerId];
            if (listItem != null)
            {
                m_ItemContainer.Remove(listItem);
            }

            m_PlayerItems.Remove(playerId);

            var participant = m_Participants.FirstOrDefault(p => p.PlayerId == playerId);
            if (participant != null)
            {
                m_Participants.Remove(participant);
            }

            UpdateEmptyState();
        }

        void ClearParticipants()
        {
            if (m_SelfParticipant != null)
            {
                m_SelfParticipant.ParticipantAudioStateChanged -= OnSelfAudioStateChanged;
                m_SelfParticipant = null;
            }

            if (m_ItemContainer != null)
            {
                m_ItemContainer.Clear();
            }

            m_PlayerItems.Clear();
            m_Participants.Clear();

            UpdateEmptyState();
        }

        void UpdateEmptyState()
        {
            if (m_EmptyLabel == null || m_ScrollView == null)
            {
                return;
            }

            bool isEmpty = m_Participants.Count == 0;

            if (isEmpty)
            {
                if (m_EmptyLabel.parent == null)
                {
                    Add(m_EmptyLabel);
                }

                if (m_ScrollView.parent != null)
                {
                    Remove(m_ScrollView);
                }
            }
            else
            {
                if (m_EmptyLabel.parent != null)
                {
                    Remove(m_EmptyLabel);
                }

                if (m_ScrollView.parent == null)
                {
                    Add(m_ScrollView);
                }
            }
        }

        void CleanupVivox()
        {
            if (m_VivoxObserver != null)
            {
                m_VivoxObserver.ServiceInitialized -= OnVivoxReady;
                m_VivoxObserver.ParticipantJoined -= OnParticipantJoined;
                m_VivoxObserver.ParticipantLeft -= OnParticipantLeft;
                m_VivoxObserver.ChannelLeft -= OnChannelLeft;
                m_VivoxObserver.LoggedOut -= OnLoggedOut;
                m_VivoxObserver.Dispose();
                m_VivoxObserver = null;
            }
        }
    }
}
