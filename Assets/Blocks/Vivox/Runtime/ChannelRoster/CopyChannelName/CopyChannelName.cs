using Blocks.Common;
using System.Collections.Generic;
using Unity.Properties;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class CopyChannelName : VisualElement
    {
        const string k_CopyChannelNameButtonText = "COPY";


        CopyChannelNameData m_ViewModel;

        readonly List<DataBinding> m_Bindings = new();

        public CopyChannelName()
        {
            AddToClassList(BlocksTheme.ContainerHorizontal);

            var channelNameLabel = new Label();
            channelNameLabel.AddToClassList(BlocksTheme.Label);
            var channelNameBinding = new DataBinding
            {
                dataSourcePath = new PropertyPath(nameof(CopyChannelNameData.DisplayChannelName)),
                bindingMode = BindingMode.ToTarget
            };
            channelNameLabel.SetBinding("text", channelNameBinding);
            Add(channelNameLabel);
            m_Bindings.Add(channelNameBinding);

            var copyChannelNameButton = new Button
            {
                text = k_CopyChannelNameButtonText
            };
            copyChannelNameButton.AddToClassList(BlocksTheme.Button);
            copyChannelNameButton.clicked += CopyChannelNameText;
            Add(copyChannelNameButton);

            var hasChannelName = new DataBinding
            {
                dataSourcePath = new PropertyPath(nameof(CopyChannelNameData.HasChannelName)),
                bindingMode = BindingMode.ToTarget
            };
            copyChannelNameButton.SetBinding(new BindingId(nameof(enabledSelf)), hasChannelName);
            m_Bindings.Add(hasChannelName);

            RegisterCallback<AttachToPanelEvent>(_ => UpdateBindings());
            RegisterCallback<DetachFromPanelEvent>(_ => CleanupBindings());
        }

        void CopyChannelNameText()
        {
            if (string.IsNullOrEmpty(m_ViewModel?.TargetChannelName))
            {
                return;
            }

            GUIUtility.systemCopyBuffer = m_ViewModel.TargetChannelName;
        }

        void UpdateBindings()
        {
            CleanupBindings();

            m_ViewModel = new CopyChannelNameData();
            foreach (var binding in m_Bindings)
            {
                binding.dataSource = m_ViewModel;
            }
        }

        void CleanupBindings()
        {
            m_ViewModel?.Dispose();
            m_ViewModel = null;

            foreach (var binding in m_Bindings)
            {
                binding.dataSource = null;
            }
        }
    }
}
