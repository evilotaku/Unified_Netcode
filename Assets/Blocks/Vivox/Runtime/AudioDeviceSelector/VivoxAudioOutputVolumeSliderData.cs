using System;
using System.Runtime.CompilerServices;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    public class VivoxAudioOutputVolumeSliderData : IDisposable, INotifyBindablePropertyChanged
    {
        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        const int k_MinVolume = -50;
        const int k_MaxVolume = 10;
        static readonly float k_EMinusOne = Mathf.Exp(1f) - 1f;

        VivoxObserver m_VivoxObserver;
        float m_VolumeValue;

        [CreateProperty]
        public float VolumeValue
        {
            get => m_VolumeValue;
            set
            {
                if (!m_VivoxObserver.IsServiceInitialized || m_VolumeValue == value)
                {
                    return;
                }

                m_VivoxObserver.VivoxService.SetOutputDeviceVolume(SliderToVivox(value));
                m_VolumeValue = value;
                Notify();
            }
        }

        public VivoxAudioOutputVolumeSliderData()
        {
            m_VivoxObserver = new VivoxObserver(VivoxObserverType.AudioDevices);
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
            m_VolumeValue = VivoxToSlider(m_VivoxObserver.VivoxService.OutputDeviceVolume);
            Notify(nameof(VolumeValue));
        }

        /// Converts a normalised slider value [0, 1] to a Vivox volume int [-50, 10] using a
        /// piecewise logarithmic taper (audio taper). The centre (0.5) maps to Vivox's unchanged default of 0.
        static int SliderToVivox(float slider)
        {
            if (slider <= 0.5f)
            {
                var u = slider * 2f;
                return (int)Mathf.Round(k_MinVolume * (1f - Mathf.Log(u * k_EMinusOne + 1f)));
            }
            var v = (slider - 0.5f) * 2f;
            return (int)Mathf.Round(k_MaxVolume * Mathf.Log(v * k_EMinusOne + 1f));
        }

        /// Converts a Vivox volume int [-50, 10] back to a normalised slider value [0, 1],
        /// inverting the logarithmic taper so round-tripping a value returns the original slider position.
        static float VivoxToSlider(int vivox)
        {
            if (vivox <= 0)
                return (Mathf.Exp((vivox - k_MinVolume) / (float)-k_MinVolume) - 1f) / k_EMinusOne * 0.5f;
            return 0.5f + (Mathf.Exp(vivox / (float)k_MaxVolume) - 1f) / k_EMinusOne * 0.5f;
        }

        void Notify([CallerMemberName] string property = null)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
        }

        public void Dispose()
        {
            m_VivoxObserver.ServiceInitialized -= OnVivoxReady;
            m_VivoxObserver.Dispose();
            m_VivoxObserver = null;
        }
    }
}
