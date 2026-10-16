using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class TextChatListItem: VisualElement
    {
        Label m_PlayerName;
        Label m_Message;

        public string PlayerName
        {
            get => m_PlayerName.text;
            set
            {
                Debug.Log("Setting PlayerName to " + value);
                if (m_PlayerName == null)
                {
                    m_PlayerName = this.Q<Label>("PlayerName");
                }
                m_PlayerName.text = value;
            }
        }

        public string Message
        {
            get => m_Message.text;
            set
            {
                Debug.Log("Setting Message to " + value);
                if (m_Message == null)
                {
                    m_Message = this.Q<Label>("Message");
                }
                m_Message.text = value;
            }
        }

        public TextChatListItem()
        {
            RegisterCallback<AttachToPanelEvent>(_ =>
            {
                m_PlayerName = this.Q<Label>("PlayerName");
                m_Message = this.Q<Label>("Message");
            });
        }
    }
}
