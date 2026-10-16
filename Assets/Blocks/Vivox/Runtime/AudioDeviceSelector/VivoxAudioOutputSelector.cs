using System;
using UnityEngine.UIElements;
using Unity.Properties;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class VivoxAudioOutputSelector : DropdownField
    {
        public VivoxAudioOutputSelector()
        {
            SetBinding(new BindingId(nameof(choices)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxAudioOutputSelectorData.DeviceChoices)),
                bindingMode = BindingMode.ToTarget,
            });


            SetBinding(new BindingId(nameof(value)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxAudioOutputSelectorData.ActiveDevice)),
                bindingMode = BindingMode.TwoWay,
            });

            RegisterCallback<AttachToPanelEvent>(_ => dataSource = new VivoxAudioOutputSelectorData());
            RegisterCallback<DetachFromPanelEvent>(_ => CleanupBindings());
        }

        void CleanupBindings()
        {
            if (dataSource is IDisposable disposable)
            {
                disposable.Dispose();
            }
            dataSource = null;
        }
    }
}
