using System;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class VivoxAudioInputSelector : DropdownField
    {
        public VivoxAudioInputSelector()
        {
            SetBinding(new BindingId(nameof(choices)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxAudioInputSelectorData.DeviceChoices)),
                bindingMode = BindingMode.ToTarget,
            });


            SetBinding(new BindingId(nameof(value)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxAudioInputSelectorData.ActiveDevice)),
                bindingMode = BindingMode.TwoWay,
            });

            RegisterCallback<AttachToPanelEvent>(_ => dataSource = new VivoxAudioInputSelectorData());
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
