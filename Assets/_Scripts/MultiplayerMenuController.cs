using System;
using UnityEngine;
using UnityEngine.UIElements;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using System.Threading.Tasks;

public class MultiplayerMenuController : MonoBehaviour
{
    private Button _hostButton;
    private Button _joinButton;
    private Button _quickJoinButton;
    private TextField _joinCodeInput;
    private Label _statusLabel;
    private Label _joinCodeLabel;

    private ISession _currentSession;
    private PanelRenderer _panelRenderer;

    private async void OnEnable()
    {
        // Get the PanelRenderer instead of UIDocument
        _panelRenderer = GetComponent<PanelRenderer>();
        if (_panelRenderer == null)
        {
            Debug.LogError("PanelRenderer component not found on the GameObject.");
            return;
        }

        _panelRenderer.RegisterUIReloadCallback(OnUIReloaded);

       
    }

    private async void OnUIReloaded(PanelRenderer panelRenderer, VisualElement _root, int version)
    {
        // Access the visual root via the PanelRenderer's visualTree
        
        _hostButton = _root.Q<Button>("hostButton");
        _joinButton = _root.Q<Button>("joinButton");
        _quickJoinButton = _root.Q<Button>("quickJoinButton");
        _joinCodeInput = _root.Q<TextField>("joinCodeInput");
        _statusLabel = _root.Q<Label>("statusLabel");
        _joinCodeLabel = _root.Q<Label>("joinCodeLabel");

        _hostButton.clicked += OnHostClicked;
        _joinButton.clicked += OnJoinClicked;
        _quickJoinButton.clicked += OnQuickJoinClicked;

        _statusLabel.text = "Initializing Services...";

        try
        {
            await UnityServices.InitializeAsync();
            _statusLabel.text = "Ready";
        }
        catch (Exception e)
        {
            _statusLabel.text = $"Initialization Failed: {e.Message}";
        }
    }

    private async void OnQuickJoinClicked()
    {
        _statusLabel.text = "Quick Joining...";
        SetButtonsEnabled(false);

        try
        {
            var quickJoinOptions = new QuickJoinOptions
            {
                CreateSession = true
            };

            var sessionOptions = new SessionOptions
            {
                MaxPlayers = 4,
                IsPrivate = false
            }.WithRelayNetwork().WithNetworkHandler(UnifiedNetworkHandler.Instance);

            var session = await MultiplayerService.Instance.MatchmakeSessionAsync(quickJoinOptions, sessionOptions);
            _currentSession = session;

            if (session.IsHost)
            {
                _joinCodeLabel.text = $"Hosting - Join Code: {session.Code}";
                _statusLabel.text = "No games found. You are now the Host.";
            }
            else
            {
                _statusLabel.text = "Joined existing game!";
                // Disable the PanelRenderer to hide the UI on success
                _panelRenderer.enabled = false;
            }
        }
        catch (SessionException e)
        {
            _statusLabel.text = $"Quick Join Failed: {e.Message}";
            SetButtonsEnabled(true);
        }
    }

    private async void OnHostClicked()
    {
        _statusLabel.text = "Creating Room...";
        SetButtonsEnabled(false);

        try
        {
            var options = new SessionOptions
            {
                MaxPlayers = 4,
                IsPrivate = false
            }.WithRelayNetwork().WithNetworkHandler(UnifiedNetworkHandler.Instance);

            var session = await MultiplayerService.Instance.CreateSessionAsync(options);
            _currentSession = session;

            _joinCodeLabel.text = $"Join Code: {session.Code}";
            _statusLabel.text = "Hosting Room";
        }
        catch (SessionException e)
        {
            _statusLabel.text = $"Host Failed: {e.Message}";
            SetButtonsEnabled(true);
        }
    }

    private async void OnJoinClicked()
    {
        string code = _joinCodeInput.value;
        if (string.IsNullOrEmpty(code))
        {
            _statusLabel.text = "Enter a Join Code";
            return;
        }

        _statusLabel.text = "Joining Room...";
        SetButtonsEnabled(false);

        try
        {
            var options = new JoinSessionOptions().WithNetworkHandler(UnifiedNetworkHandler.Instance);
            var session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code, options);
            _currentSession = session;

            _statusLabel.text = "Joined Room";
            // Disable the PanelRenderer to hide the UI on success
            _panelRenderer.enabled = false;
        }
        catch (SessionException e)
        {
            _statusLabel.text = $"Join Failed: {e.Message}";
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _hostButton.SetEnabled(enabled);
        _joinButton.SetEnabled(enabled);
        _quickJoinButton.SetEnabled(enabled);
    }
}