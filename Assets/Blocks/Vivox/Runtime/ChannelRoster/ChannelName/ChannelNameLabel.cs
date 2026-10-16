using Unity.Properties;
using System;
using Blocks.Common;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class ChannelNameLabel : Label
    {
        DataBinding m_DataBinding;

        public ChannelNameLabel()
        {
            AddToClassList(BlocksTheme.Header);
            m_DataBinding = new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(ChannelNameData.TargetChannelName)),
                bindingMode = BindingMode.ToTarget
            };
            SetBinding(new BindingId(nameof(text)), m_DataBinding);

            RegisterCallback<AttachToPanelEvent>(_ => UpdateBindings());
            RegisterCallback<DetachFromPanelEvent>(_ => CleanupBindings());
        }

        void UpdateBindings()
        {
            CleanupBindings();
            m_DataBinding.dataSource = new ChannelNameData();
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
