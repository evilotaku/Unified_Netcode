using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Properties;
using Unity.Services.Vivox;
using UnityEditor;
using UnityEngine.UIElements;

namespace Blocks.Vivox
{
    public class VivoxAudioOutputSelectorData : IDisposable, INotifyBindablePropertyChanged
    {
        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        VivoxObserver m_VivoxObserver;
        List<string> m_DeviceChoices = new List<string>();
        string m_ActiveDevice;

        [CreateProperty]
        public List<string> DeviceChoices
        {
            get => m_DeviceChoices;
            set
            {
                if (!m_VivoxObserver.IsServiceInitialized || m_DeviceChoices == value)
                {
                    return;
                }

                m_DeviceChoices = value;
                Notify();
            }
        }

        [CreateProperty]
        public string ActiveDevice
        {
            get => m_ActiveDevice;
            set
            {
                if (!m_VivoxObserver.IsServiceInitialized || m_ActiveDevice == value)
                {
                    return;
                }

                if (m_ActiveDevice != null)
                {
                    _ = m_VivoxObserver.VivoxService.AvailableOutputDevices.ToList().Find(x => x.DeviceName == value).SetActiveDeviceAsync();
                }
                m_ActiveDevice = value;
                Notify();
            }
        }

        public VivoxAudioOutputSelectorData()
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
            m_VivoxObserver.AvailableOutputDevicesChanged += OnAvailableOutputDevicesChanged;
            m_VivoxObserver.EffectiveOutputDeviceChanged += OnEffectiveOutputDeviceChanged;

            OnAvailableOutputDevicesChanged();
            OnEffectiveOutputDeviceChanged();
        }

        void OnAvailableOutputDevicesChanged()
        {
            DeviceChoices = m_VivoxObserver.VivoxService.AvailableOutputDevices
                .Select(device => device.DeviceName)
                .ToList();
        }

        void OnEffectiveOutputDeviceChanged()
        {
            ActiveDevice = m_VivoxObserver.VivoxService.ActiveOutputDevice.DeviceName;
        }

        void Notify([CallerMemberName] string property = null)
        {
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));
        }

        public void Dispose()
        {
            if (m_VivoxObserver != null)
            {
                m_VivoxObserver.ServiceInitialized -= OnVivoxReady;
                m_VivoxObserver.AvailableOutputDevicesChanged -= OnAvailableOutputDevicesChanged;
                m_VivoxObserver.EffectiveOutputDeviceChanged -= OnEffectiveOutputDeviceChanged;
                m_VivoxObserver.Dispose();
                m_VivoxObserver = null;
            }
        }
    }
}
