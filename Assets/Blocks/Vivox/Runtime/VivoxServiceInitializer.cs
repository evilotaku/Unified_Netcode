using System;
using System.Threading.Tasks;
using Unity.Services.Core;
using Unity.Services.Vivox;
using UnityEngine;

namespace Blocks.Vivox
{
    public class VivoxServiceInitializer : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] bool m_EnableLogging = false;

        // Keep track of event subscriptions for cleanup
        TaskCompletionSource<bool> m_UnityServicesTask;
        TaskCompletionSource<bool> m_VivoxInitTask;

        async void Start()
        {
            await InitializeAsync();
        }

        void OnDestroy()
        {
            // Cancel any pending tasks
            m_UnityServicesTask?.TrySetCanceled();
            m_VivoxInitTask?.TrySetCanceled();

            // Cleanup is automatic for TaskCompletionSource since we're not using persistent event handlers
            Log("VivoxServiceInitializer destroyed, cleaned up pending operations");
        }

        async Task InitializeAsync()
        {
            try
            {
                Log("Starting Vivox initialization...");

                // Wait for Unity Services to be initialized
                await WaitForUnityServices();

                // Then initialize Vivox
                await InitializeVivoxService();

                Log("Vivox initialization completed successfully");
            }
            catch (OperationCanceledException)
            {
                Log("Vivox initialization was cancelled");
            }
            catch (Exception e)
            {
                var errorMsg = $"Vivox initialization failed: {e.Message}";
                LogError(errorMsg);
            }
        }

        async Task WaitForUnityServices()
        {
            m_UnityServicesTask = new TaskCompletionSource<bool>();

            void OnUnityServicesInitialized()
            {
                UnityServices.Initialized -= OnUnityServicesInitialized;
                m_UnityServicesTask?.TrySetResult(true);
            }

            UnityServices.Initialized += OnUnityServicesInitialized;

            if (UnityServices.State == ServicesInitializationState.Initialized)
            {
                m_UnityServicesTask.TrySetResult(true);
            }

            try
            {
                await m_UnityServicesTask.Task;
                Log("Unity Services is now initialized");
            }
            finally
            {
                UnityServices.Initialized -= OnUnityServicesInitialized;
                m_UnityServicesTask = null;
            }
        }

        async Task InitializeVivoxService()
        {
            if (VivoxService.Instance == null)
            {
                throw new InvalidOperationException("VivoxService.Instance is null");
            }

            var vivoxService = VivoxService.Instance;

            if (vivoxService.InitializationState == VivoxInitializationState.Initialized)
            {
                Log("Vivox Service already initialized");
                return;
            }

            if (vivoxService.InitializationState == VivoxInitializationState.Failed)
            {
                throw new InvalidOperationException("Vivox Service has already failed to initialize");
            }

            if (vivoxService.InitializationState == VivoxInitializationState.Initializing)
            {
                Log("Vivox Service initializing, waiting...");

                m_VivoxInitTask = new TaskCompletionSource<bool>();

                void OnVivoxInitialized()
                {
                    vivoxService.Initialized -= OnVivoxInitialized;
                    vivoxService.InitializationFailed -= OnVivoxInitializationFailed;
                    m_VivoxInitTask?.TrySetResult(true);
                    Log("Vivox Service initialized");
                }

                void OnVivoxInitializationFailed(Exception e)
                {
                    vivoxService.Initialized -= OnVivoxInitialized;
                    vivoxService.InitializationFailed -= OnVivoxInitializationFailed;
                    m_VivoxInitTask?.TrySetException(e);
                }

                vivoxService.Initialized += OnVivoxInitialized;
                vivoxService.InitializationFailed += OnVivoxInitializationFailed;

                if (vivoxService.InitializationState == VivoxInitializationState.Initialized)
                {
                    m_VivoxInitTask.TrySetResult(true);
                }
                else if (vivoxService.InitializationState == VivoxInitializationState.Failed)
                {
                    vivoxService.Initialized -= OnVivoxInitialized;
                    vivoxService.InitializationFailed -= OnVivoxInitializationFailed;
                    m_VivoxInitTask = null;
                    throw new InvalidOperationException("Vivox Service failed to initialize");
                }

                try
                {
                    await m_VivoxInitTask.Task;
                }
                finally
                {
                    vivoxService.Initialized -= OnVivoxInitialized;
                    vivoxService.InitializationFailed -= OnVivoxInitializationFailed;
                    m_VivoxInitTask = null;
                }
                return;
            }

            if (vivoxService.InitializationState == VivoxInitializationState.Uninitialized)
            {
                Log("Initializing Vivox Service...");
                await vivoxService.InitializeAsync();
                Log("Vivox Service initialized");
                return;
            }

            throw new InvalidOperationException($"Unexpected Vivox initialization state: {vivoxService.InitializationState}");
        }

        void Log(string message)
        {
            if (m_EnableLogging)
            {
                Debug.Log($"[VivoxServiceInitializer] {message}");
            }
        }

        void LogError(string message)
        {
            if (m_EnableLogging)
            {
                Debug.LogError($"[VivoxServiceInitializer] {message}");
            }
        }
    }
}
