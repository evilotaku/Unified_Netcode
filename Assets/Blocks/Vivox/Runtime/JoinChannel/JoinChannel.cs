using System;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class JoinChannel : VisualElement
    {
        VivoxObserver m_VivoxObserver;

        Button m_JoinButton;

        enum ChannelState { NotJoined, Joining, Joined }
        ChannelState m_ChannelState = ChannelState.NotJoined;
        string m_pendingChannelName;

        ChannelSettings m_ChannelSettings;
        ChannelSettings m_DefaultSettings;

        [CreateProperty, UxmlAttribute]
        public ChannelSettings ChannelSettings
        {
            get => m_ChannelSettings;
            set
            {
                if (m_ChannelSettings == value)
                {
                    return;
                }

                m_ChannelSettings = value;
            }
        }

        ChannelSettings ActiveChannelSettings
        {
            get
            {
                if (ChannelSettings != null)
                {
                    return ChannelSettings;
                }

                // Create default once and cache it
                if (m_DefaultSettings == null)
                {
                    m_DefaultSettings = ScriptableObject.CreateInstance<ChannelSettings>();
                    m_DefaultSettings.ChannelType = ChannelType.Group;
                    m_DefaultSettings.ChatCapability = ChatCapability.TextAndAudio;
                }

                return m_DefaultSettings;
            }
        }

        public JoinChannel()
        {
            RegisterCallback<AttachToPanelEvent>(OnAttachToPanelEvent);
            RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanelEvent);
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
            // Subscribe to observer channel events so UI reflects service state
            m_VivoxObserver.ChannelJoined += OnChannelJoined;
            m_VivoxObserver.ChannelLeft += OnChannelLeft;

            // If the service already has the enteredChannelName channel active, reflect that
            var enteredChannelName = this.Q<TextField>("ChannelName")?.text;
            if (!string.IsNullOrEmpty(enteredChannelName) && m_VivoxObserver.VivoxService != null)
            {
                try
                {
                    if (m_VivoxObserver.VivoxService.ActiveChannels != null && m_VivoxObserver.VivoxService.ActiveChannels.ContainsKey(enteredChannelName))
                    {
                        m_ChannelState = ChannelState.Joined;
                    }
                }
                catch
                {
                    // ignore any access exceptions
                }
            }

            UpdateButtonText();
        }

        void OnAttachToPanelEvent(AttachToPanelEvent panelEvent)
        {
            m_JoinButton = this.Q<Button>("JoinRoom");
            if (m_JoinButton != null)
            {
                m_JoinButton.clicked += OnClickJoinChannelButton;
            }

            // Ensure button text matches current state
            UpdateButtonText();
        }

        void OnDetachFromPanelEvent(DetachFromPanelEvent panelEvent)
        {
            if (m_JoinButton != null)
            {
                m_JoinButton.clicked -= OnClickJoinChannelButton;
                m_JoinButton = null;
            }

            if (m_VivoxObserver != null)
            {
                m_VivoxObserver.ChannelJoined -= OnChannelJoined;
                m_VivoxObserver.ChannelLeft -= OnChannelLeft;
                m_VivoxObserver.ServiceInitialized -= OnVivoxReady;
                m_VivoxObserver.Dispose();
                m_VivoxObserver = null;
            }
        }

        string GetPlayerName()
        {
            return this.Q<TextField>("PlayerName").value;
        }

        async void OnClickJoinChannelButton()
        {
            try
            {
                var playerName = GetPlayerName();
                var channelName = this.Q<TextField>("ChannelName").text;

                if (string.IsNullOrEmpty(channelName))
                {
                    return;
                }

                // If currently joined, clicking should leave the channel
                if (m_ChannelState == ChannelState.Joined)
                {
                    await m_VivoxObserver.VivoxService.LeaveChannelAsync(channelName);
                    // ChannelLeft event or the awaited call should update state; ensure fallback
                    m_ChannelState = ChannelState.NotJoined;
                    UpdateButtonText();
                    return;
                }

                // Start joining flow
                m_pendingChannelName = channelName;
                m_ChannelState = ChannelState.Joining;
                UpdateButtonText();

                if (!m_VivoxObserver.IsLoggedIn)
                {
                    await m_VivoxObserver.VivoxService.LoginAsync(new LoginOptions { DisplayName = playerName });
                }
                else
                {
                    // If logged in, try to ensure display name is updated
                    try
                    {
                        await m_VivoxObserver.VivoxService.LogoutAsync();
                        await m_VivoxObserver.VivoxService.LoginAsync(new LoginOptions { DisplayName = playerName });
                    }
                    catch
                    {
                        // ignore and continue
                    }
                }

                switch (ActiveChannelSettings.ChannelType)
                {
                    case ChannelType.Echo:
                        await m_VivoxObserver.VivoxService.JoinEchoChannelAsync(channelName, ActiveChannelSettings.ChatCapability);
                        break;
                    case ChannelType.Group:
                        await m_VivoxObserver.VivoxService.JoinGroupChannelAsync(channelName, ActiveChannelSettings.ChatCapability);
                        break;
                }

                // If join completes successfully, mark joined. The observer event will also fire and keep state consistent.
                m_ChannelState = ChannelState.Joined;
                m_pendingChannelName = null;
                UpdateButtonText();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                m_ChannelState = ChannelState.NotJoined;
                m_pendingChannelName = null;
                UpdateButtonText();
            }
        }

        void OnChannelJoined(string channelName)
        {
            if (!string.IsNullOrEmpty(m_pendingChannelName) && channelName == m_pendingChannelName)
            {
                m_ChannelState = ChannelState.Joined;
                m_pendingChannelName = null;
                UpdateButtonText();
                return;
            }
        }

        void OnChannelLeft(string channelName)
        {
            var enteredChannelName = this.Q<TextField>("ChannelName")?.text;
            if (channelName == enteredChannelName || (!string.IsNullOrEmpty(m_pendingChannelName) && channelName == m_pendingChannelName))
            {
                m_ChannelState = ChannelState.NotJoined;
                m_pendingChannelName = null;
                UpdateButtonText();
            }
        }

        void UpdateButtonText()
        {
            if (m_JoinButton == null)
            {
                return;
            }

            var playerNameField = this.Q<TextField>("PlayerName");
            var channelNameField = this.Q<TextField>("ChannelName");

            switch (m_ChannelState)
            {
                case ChannelState.NotJoined:
                    m_JoinButton.text = "Join Channel";
                    m_JoinButton.SetEnabled(true);
                    playerNameField?.SetEnabled(true);
                    channelNameField?.SetEnabled(true);
                    break;
                case ChannelState.Joining:
                    m_JoinButton.text = "Joining...";
                    m_JoinButton.SetEnabled(false);
                    playerNameField?.SetEnabled(false);
                    channelNameField?.SetEnabled(false);
                    break;
                case ChannelState.Joined:
                    m_JoinButton.text = "Leave Channel";
                    m_JoinButton.SetEnabled(true);
                    playerNameField?.SetEnabled(false);
                    channelNameField?.SetEnabled(false);
                    break;
            }
        }
    }
}
