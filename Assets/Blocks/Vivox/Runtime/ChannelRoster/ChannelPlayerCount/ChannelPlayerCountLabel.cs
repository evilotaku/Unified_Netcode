using System;
using Blocks.Common;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class ChannelPlayerCountLabel : Label
    {
        [CreateProperty, UxmlAttribute]
        public string SessionType
        {
            get => m_SessionType;
            set
            {
                if (m_SessionType == value)
                {
                    return;
                }

                m_SessionType = value;
                if (panel != null)
                {
                    UpdateBindings();
                }
            }
        }

        string m_SessionType;

        DataBinding m_DataBinding;

        public ChannelPlayerCountLabel()
        {
            AddToClassList(BlocksTheme.Label);
            m_DataBinding = new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(ChannelPlayerCountData.DisplayText)),
                bindingMode = BindingMode.ToTarget
            };
            SetBinding(new BindingId(nameof(text)), m_DataBinding);

            RegisterCallback<AttachToPanelEvent>(_ => UpdateBindings());
            RegisterCallback<DetachFromPanelEvent>(_ => CleanupBindings());
        }

        void UpdateBindings()
        {
            CleanupBindings();
            m_DataBinding.dataSource = new ChannelPlayerCountData(m_SessionType);
        }

        void CleanupBindings()
        {
            if (m_DataBinding.dataSource is IDisposable disposable)
            {
                disposable.Dispose();
            }

            m_DataBinding.dataSource = null;
        }
    }
}
