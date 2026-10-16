using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{

    [UxmlElement]
    public partial class TextChatLog : VisualElement
    {
        readonly VivoxObserver m_VivoxObserver;
        ListView m_MessageListView;
        ScrollView m_MessageScrollView;
        TextField m_TextInputField;
        Button m_SendButton;
        Label m_EmptyLabel;
        VisualElement m_InputContainer;

        string m_ChannelName;
        bool m_IsLoading;
        bool m_IsEndOfList;

        [CreateProperty]
        public string ChannelName
        {
            get => m_ChannelName;
            set
            {
                if (m_ChannelName == value)
                {
                    return;
                }
                m_ChannelName = value;
                UpdateInputState();
            }
        }

        [CreateProperty]
        public bool IsLoading
        {
            get => m_IsLoading;
            set => m_IsLoading = value;
        }

        [CreateProperty]
        public bool IsEndOflist
        {
            get => m_IsEndOfList;
            set => m_IsEndOfList = value;
        }

        [CreateProperty]
        public List<VivoxMessage> Messages
        {
            get => m_MessageListView?.itemsSource as List<VivoxMessage>;
            set
            {
                if (m_MessageListView == null || m_MessageScrollView == null)
                {
                    return;
                }

                var oldLastMessage = m_MessageListView.itemsSource?.Count > 0 ? m_MessageListView.itemsSource?[^1] as VivoxMessage : null;
                var isAtBottom = Mathf.Approximately(m_MessageScrollView.verticalScroller.value, m_MessageScrollView.verticalScroller.highValue);

                var lastMessage = value?.LastOrDefault();
                var isNewMessageFromCurrentUser = oldLastMessage?.MessageId != lastMessage?.MessageId && lastMessage?.SenderPlayerId == VivoxService.Instance.SignedInPlayerId;
                var isFirstLoad = m_MessageListView.itemsSource == null && value?.Count > 0;

                if (isNewMessageFromCurrentUser || isAtBottom || isFirstLoad)
                {
                    m_MessageListView.itemsSource = value?.ToList();
                    m_MessageListView.Rebuild();
                    schedule.Execute(() =>
                    {
                        if (m_MessageListView?.itemsSource?.Count > 0)
                        {
                            m_MessageListView.ScrollToItem(m_MessageListView.itemsSource.Count - 1);
                        }
                    });
                }
                else if (value?.Count > m_MessageListView.itemsSource?.Count + 1)
                {
                    // Preserve scroll position when loading previous messages
                    int newItemsCount = value.Count - m_MessageListView.itemsSource.Count;
                    float adjustmentDistance = newItemsCount * 80f; // Estimated average item height for scroll preservation
                    m_MessageListView.itemsSource = value?.ToList();
                    m_MessageListView.Rebuild();
                    m_MessageScrollView.scrollOffset = new Vector2(0, m_MessageScrollView.scrollOffset.y + adjustmentDistance);
                }
                else
                {
                    // Default replace
                    m_MessageListView.itemsSource = value?.ToList();
                    m_MessageListView.Rebuild();
                }
            }
        }

        public TextChatLog()
        {

            RegisterCallback<AttachToPanelEvent>(HandleAttachToPanelEvent);


            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                // Clean up UI callbacks
                if (m_SendButton != null)
                {
                    m_SendButton.clicked -= HandleSend;
                }
                if (m_MessageScrollView != null)
                {
                    m_MessageScrollView.verticalScroller.valueChanged -= HandleScrollValueChanged;
                }
                if (m_TextInputField != null)
                {
                    m_TextInputField.UnregisterValueChangedCallback(HandleKeyboardInput);
                }

                if (dataSource is IDisposable chatLogData)
                {
                    chatLogData.Dispose();
                }

                // Unsubscribe from observer events
                if (m_VivoxObserver != null)
                {
                    m_VivoxObserver.ServiceInitialized -= OnVivoxReady;
                    m_VivoxObserver.LoggedIn -= OnObserverLoggedIn;
                    m_VivoxObserver.LoggedOut -= OnObserverLoggedOut;
                    m_VivoxObserver.ParticipantJoined -= OnParticipantJoined;
                    m_VivoxObserver.ChannelLeft -= OnChannelLeft;
                    m_VivoxObserver.Dispose();
                }

            });

            m_VivoxObserver = new VivoxObserver(VivoxObserverType.Login | VivoxObserverType.Channel);
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

            // Bind data properties
            SetBinding(new BindingId(nameof(ChannelName)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(TextChatLogData.TargetChannelName)),
                bindingMode = BindingMode.ToTarget,
            });

            SetBinding(new BindingId(nameof(IsEndOflist)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(TextChatLogData.EndOfList)),
                bindingMode = BindingMode.ToTarget,
            });

            SetBinding(new BindingId(nameof(IsLoading)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(TextChatLogData.IsLoading)),
                bindingMode = BindingMode.ToTarget,
            });

            SetBinding(new BindingId(nameof(Messages)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(TextChatLogData.Messages)),
                bindingMode = BindingMode.ToTarget,
            });

            // subscribe to existing observer events
            m_VivoxObserver.LoggedIn += OnObserverLoggedIn;
            m_VivoxObserver.LoggedOut += OnObserverLoggedOut;
            m_VivoxObserver.ParticipantJoined += OnParticipantJoined;
            m_VivoxObserver.ChannelLeft += OnChannelLeft;
        }

        void OnObserverLoggedIn()
        {
            UpdateInputState();
        }

        void OnObserverLoggedOut()
        {
            UpdateInputState();
        }

        void OnParticipantJoined(VivoxParticipant participant)
        {
            if (participant.IsSelf && participant.ChannelName == ChannelName)
            {
                UpdateInputState();
            }
        }

        void OnChannelLeft(string channelName)
        {
            if (channelName == ChannelName)
            {
                UpdateInputState();
            }
        }

        void HandleAttachToPanelEvent(AttachToPanelEvent _)
        {
            dataSource = new TextChatLogData();
            m_SendButton = this.Q<Button>("SendButton");
            m_MessageListView = this.Q<ListView>("ChatLog");
            m_TextInputField = this.Q<TextField>("ChatInputField");
            m_MessageScrollView = m_MessageListView.Q<ScrollView>();
            m_EmptyLabel = this.Q<Label>("EmptyLabel");
            m_InputContainer = this.Q<VisualElement>("InputContainer");

            m_MessageListView.makeNoneElement = () => new Label();
            m_MessageListView.makeItem = () =>
            {
                var item = new VisualElement();
                item.AddToClassList(VivoxTheme.TextChatListItem);

                var playerName = new Label { name = "PlayerName" };
                playerName.AddToClassList(VivoxTheme.Label);
                playerName.AddToClassList(VivoxTheme.TextChatPlayerName);

                var message = new Label { name = "Message" };
                message.AddToClassList(VivoxTheme.Label);
                message.AddToClassList(VivoxTheme.TextChatMessage);

                item.Add(playerName);
                item.Add(message);
                return item;
            };
            m_MessageListView.bindItem = (e, i) =>
            {
                VivoxMessage message = m_MessageListView.itemsSource[i] as VivoxMessage;
                var isCurrentUser = message?.SenderPlayerId == VivoxService.Instance.SignedInPlayerId;
                e.Q<Label>("PlayerName").text = isCurrentUser ? $"\u2192 {message?.SenderDisplayName}" : message?.SenderDisplayName;
                e.Q<Label>("Message").text = message?.MessageText;
            };

            if (m_SendButton != null)
            {
                m_SendButton.clicked += HandleSend;
            }
            if (m_TextInputField != null)
            {
                m_TextInputField.isDelayed = true;
                m_TextInputField.RegisterValueChangedCallback(HandleKeyboardInput);
            }
            if (m_MessageScrollView != null)
            {
                m_MessageScrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
                m_MessageScrollView.verticalScroller.valueChanged += HandleScrollValueChanged;
            }

            // Ensure input state reflects current vivox and channel info
            UpdateInputState();
        }

        void HandleKeyboardInput(ChangeEvent<string> evt)
        {
            // Send on submit / value changed when delayed
            HandleSend();
        }

        async void HandleScrollValueChanged(float position)
        {
            if (IsEndOflist || IsLoading || position != 0)
            {
                return;
            }

            if (dataSource is TextChatLogData chatLogData)
            {
                await chatLogData.LoadPreviousMessages();
            }
        }

        async void HandleSend()
        {
            if (!m_VivoxObserver.IsLoggedIn || string.IsNullOrEmpty(ChannelName))
            {
                return;
            }

            if (m_TextInputField == null || m_SendButton == null)
            {
                return;
            }

            m_TextInputField.SetEnabled(false);
            m_SendButton.SetEnabled(false);
            if (string.IsNullOrEmpty(m_TextInputField.value))
            {
                m_TextInputField.SetEnabled(true);
                m_SendButton.SetEnabled(true);
                return;
            }

            await VivoxService.Instance.SendChannelTextMessageAsync(ChannelName, m_TextInputField.value);
            m_TextInputField.value = string.Empty;
            m_TextInputField.SetEnabled(true);
            m_TextInputField.Focus();
            m_SendButton.SetEnabled(true);
        }

        void UpdateInputState()
        {
            if (m_TextInputField == null || m_SendButton == null)
            {
                return;
            }

            bool enabled = true;

            if (!m_VivoxObserver.IsServiceInitialized)
            {
                enabled = false;
            }
            else if (string.IsNullOrEmpty(ChannelName))
            {
                enabled = false;
            }
            else
            {
                try
                {
                    var channels = m_VivoxObserver.VivoxService?.ActiveChannels;
                    if (m_VivoxObserver.IsLoggedIn && channels != null && channels.TryGetValue(ChannelName, out var participants))
                    {
                        var self = participants?.FirstOrDefault(p => p.IsSelf);
                        enabled = self?.IsInText == true;
                    }
                    else
                    {
                        enabled = false;
                    }
                }
                catch
                {
                    enabled = false;
                }
            }

            m_TextInputField.SetEnabled(enabled);
            m_TextInputField.textEdition.placeholder = enabled ? $"Message {ChannelName}" : string.Empty;
            m_SendButton.SetEnabled(enabled && !string.IsNullOrEmpty(m_TextInputField.value));
            m_SendButton.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;

            if (m_EmptyLabel != null)
            {
                m_EmptyLabel.EnableInClassList(VivoxTheme.TextChatEmptyLabelVisible, !enabled);
                m_MessageListView?.EnableInClassList(VivoxTheme.TextChatListHidden, !enabled);
            }
        }
    }

}
