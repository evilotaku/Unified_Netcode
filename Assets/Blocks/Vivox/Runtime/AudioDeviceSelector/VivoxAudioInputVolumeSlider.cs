using System;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class VivoxAudioInputVolumeSlider : Slider
    {
        public VivoxAudioInputVolumeSlider()
        {
            SetBinding(new BindingId(nameof(value)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxAudioInputVolumeSliderData.VolumeValue)),
                bindingMode = BindingMode.TwoWay,
            });

            RegisterCallback<AttachToPanelEvent>(_ => dataSource = new VivoxAudioInputVolumeSliderData());
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
