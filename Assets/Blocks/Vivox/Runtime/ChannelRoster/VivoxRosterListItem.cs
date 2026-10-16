using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class VivoxRosterListItem : VisualElement
    {
        readonly VivoxObserver m_VivoxObserver;
        Label m_PlayerNameLabel = new Label();
        VisualElement m_SpeakingIndicatorIcon = new VisualElement();
        VisualElement m_LeftContainer = new VisualElement();
        Button m_MuteButton;
        VivoxParticipant m_Participant;
        string m_IconClass;
        bool m_IsRowPopulated = false;
        bool m_HasVoice;
        bool m_IsInAudio;
        bool m_LocalUserIsInAudio;

        [UxmlAttribute, CreateProperty]
        public string PlayerName
        {
            get => m_PlayerNameLabel.text;
            set => m_PlayerNameLabel.text = value;
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
                UpdateVoiceControlsVisibility();
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
                UpdateVoiceControlsVisibility();
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

                if (dataSource is VivoxRosterListItemData participantListData)
                {
                    participantListData.Participant = value;
                }

                if (value != null)
                {
                    PlayerName = value.DisplayName ?? value.PlayerId;
                }
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

                if (!string.IsNullOrEmpty(m_IconClass))
                {
                    m_SpeakingIndicatorIcon.RemoveFromClassList(m_IconClass);
                    m_SpeakingIndicatorIcon.RemoveFromClassList(VivoxTheme.SpeakingIndicatorVisible);
                }

                m_IconClass = value;

                if (!string.IsNullOrEmpty(m_IconClass))
                {
                    m_SpeakingIndicatorIcon.AddToClassList(m_IconClass);
                    m_SpeakingIndicatorIcon.AddToClassList(VivoxTheme.SpeakingIndicatorVisible);
                }
                else
                {
                    m_SpeakingIndicatorIcon.RemoveFromClassList(VivoxTheme.SpeakingIndicatorVisible);
                }
            }
        }

        public bool LocalUserIsInAudio
        {
            get => m_LocalUserIsInAudio;
            set
            {
                m_LocalUserIsInAudio = value;
                if (dataSource is VivoxRosterListItemData data)
                {
                    data.LocalUserIsInAudio = value;
                }
            }
        }

        [CreateProperty]
        public string ButtonText
        {
            get => m_MuteButton?.text;
            set
            {
                if (m_MuteButton != null)
                {
                    m_MuteButton.text = value;
                    UpdateVoiceControlsVisibility();
                }
            }
        }

        public VivoxRosterListItem()
        {
            m_VivoxObserver = new VivoxObserver(VivoxObserverType.Login);

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
            AddToClassList(VivoxTheme.RosterItem);

            SetBinding(new BindingId(nameof(PlayerName)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxRosterListItemData.PlayerName)),
                bindingMode = BindingMode.ToTarget
            });

            SetBinding(new BindingId(nameof(HasVoice)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxRosterListItemData.HasVoice)),
                bindingMode = BindingMode.ToTarget
            });

            SetBinding(new BindingId(nameof(IsInAudio)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxRosterListItemData.IsInAudio)),
                bindingMode = BindingMode.ToTarget
            });

            SetBinding(new BindingId(nameof(ButtonText)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxRosterListItemData.ButtonText)),
                bindingMode = BindingMode.ToTarget
            });

            SetBinding(new BindingId(nameof(Participant)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxRosterListItemData.Participant)),
                bindingMode = BindingMode.TwoWay
            });

            SetBinding(new BindingId(nameof(IconClass)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxRosterListItemData.IconClass)),
                bindingMode = BindingMode.ToTarget
            });

            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                if (!m_IsRowPopulated)
                {
                    if (dataSource == null)
                    {
                        dataSource = new VivoxRosterListItemData();
                    }

                    if (dataSource is VivoxRosterListItemData data)
                    {
                        data.LocalUserIsInAudio = m_LocalUserIsInAudio;
                        if (m_Participant != null)
                        {
                            data.Participant = m_Participant;
                        }
                    }

                    PopulateRow();
                    m_IsRowPopulated = true;

                    // Re-apply current icon state since PopulateRow() always initializes to hidden.
                    // Without this, a recycled item whose IconClass was set while detached
                    // will never receive a binding update to correct the display style.
                    if (!string.IsNullOrEmpty(m_IconClass))
                    {
                        m_SpeakingIndicatorIcon.AddToClassList(VivoxTheme.SpeakingIndicatorVisible);
                    }
                }
            });

            RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (dataSource is VivoxRosterListItemData participantListData)
                {
                    participantListData.Dispose();
                }

                dataSource = null;
                Clear();
                m_IsRowPopulated = false;
                m_VivoxObserver.Dispose();
            });

        }

        void PopulateRow()
        {
            if (m_LeftContainer.parent != null || m_MuteButton?.parent != null)
            {
                return;
            }

            m_LeftContainer.AddToClassList(VivoxTheme.RosterItemLeft);

            m_PlayerNameLabel.AddToClassList(VivoxTheme.Label);
            m_PlayerNameLabel.AddToClassList(VivoxTheme.RosterItemName);

            m_SpeakingIndicatorIcon.AddToClassList(VivoxTheme.SpeakingIndicator);

            m_MuteButton = new Button(() => (dataSource as VivoxRosterListItemData)?.ToggleMute()) { text = "" };
            m_MuteButton.AddToClassList(VivoxTheme.Button);
            m_MuteButton.AddToClassList(VivoxTheme.ButtonSmall);
            m_MuteButton.AddToClassList(VivoxTheme.MuteButton);

            m_LeftContainer.Add(m_PlayerNameLabel);
            m_LeftContainer.Add(m_SpeakingIndicatorIcon);

            Add(m_LeftContainer);
            Add(m_MuteButton);
        }

        void UpdateVoiceControlsVisibility()
        {
            if (m_MuteButton == null)
            {
                return;
            }

            bool shouldShowVoiceControls = HasVoice && IsInAudio && !string.IsNullOrEmpty(ButtonText);

            if (shouldShowVoiceControls)
            {
                m_MuteButton.AddToClassList(VivoxTheme.MuteButtonActive);
            }
            else
            {
                m_MuteButton.RemoveFromClassList(VivoxTheme.MuteButtonActive);
            }
        }
    }
}
