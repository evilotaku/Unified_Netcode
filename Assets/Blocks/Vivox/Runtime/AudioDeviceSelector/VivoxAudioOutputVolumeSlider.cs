using System;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    [UxmlElement]
    public partial class VivoxAudioOutputVolumeSlider : Slider
    {
        public VivoxAudioOutputVolumeSlider()
        {
            SetBinding(new BindingId(nameof(value)), new DataBinding()
            {
                dataSourcePath = new PropertyPath(nameof(VivoxAudioOutputVolumeSliderData.VolumeValue)),
                bindingMode = BindingMode.TwoWay,
            });

            RegisterCallback<AttachToPanelEvent>(_ => dataSource = new VivoxAudioOutputVolumeSliderData());
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
