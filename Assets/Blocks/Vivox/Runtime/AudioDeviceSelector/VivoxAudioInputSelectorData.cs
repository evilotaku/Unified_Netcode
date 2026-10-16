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
    public class VivoxAudioInputSelectorData : IDisposable, INotifyBindablePropertyChanged
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
                    _ = m_VivoxObserver.VivoxService.AvailableInputDevices.ToList().Find(x => x.DeviceName == value).SetActiveDeviceAsync();
                }
                m_ActiveDevice = value;
                Notify();
            }
        }

        public VivoxAudioInputSelectorData()
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
            m_VivoxObserver.AvailableInputDevicesChanged += OnAvailableInputDevicesChanged;
            m_VivoxObserver.EffectiveInputDeviceChanged += OnEffectiveInputDeviceChanged;

            OnAvailableInputDevicesChanged();
            OnEffectiveInputDeviceChanged();
        }

        void OnAvailableInputDevicesChanged()
        {
            DeviceChoices = m_VivoxObserver.VivoxService.AvailableInputDevices
                .Select(device => device.DeviceName)
                .ToList();
        }

        void OnEffectiveInputDeviceChanged()
        {
            ActiveDevice = m_VivoxObserver.VivoxService.ActiveInputDevice.DeviceName;
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
                m_VivoxObserver.AvailableInputDevicesChanged -= OnAvailableInputDevicesChanged;
                m_VivoxObserver.EffectiveInputDeviceChanged -= OnEffectiveInputDeviceChanged;
                m_VivoxObserver.Dispose();
                m_VivoxObserver = null;
            }
        }
    }
}
