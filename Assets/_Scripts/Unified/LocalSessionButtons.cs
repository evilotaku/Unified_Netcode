using Unity.Netcode;
using Unity.Networking.Transport;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Direct-connect Host / Client buttons for NGO unified mode, plus session status.
/// The host listens on UnifiedBootstrap's port (7979) and clients connect to ClientServerBootstrap.DefaultConnectAddress.
/// Relay sessions (Quick Join etc.) go through UnifiedNetworkHandler instead; this hides the menu once any session runs
/// and shows the join code.
/// </summary>
public class LocalSessionButtons : MonoBehaviour
{
    public string Address = "127.0.0.1";
    [Tooltip("Hidden once a session starts, e.g. the session menu.")]
    public PanelRenderer MenuToHide;

    void OnGUI()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;

        if (nm.IsListening) HideMenu();

        GUILayout.BeginArea(new Rect(Screen.width - 230, 10, 220, 120));
        if (!nm.IsListening)
        {
            if (GUILayout.Button("Host (direct)") && nm.StartHost()) HideMenu();
            Address = GUILayout.TextField(Address);
            if (GUILayout.Button("Client (direct)"))
            {
                if (NetworkEndpoint.TryParse(Address, 0, out var endpoint))
                    ClientServerBootstrap.DefaultConnectAddress = endpoint;
                if (nm.StartClient()) HideMenu();
            }
        }
        else
        {
            var session = CurrentSession();
            GUILayout.Label((nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client") + (session != null ? $"  Code: {session.Code}" : ""));
            if (GUILayout.Button("Shutdown"))
            {
                // Leaving the session also stops networking through its network handler.
                if (session != null) _ = session.LeaveAsync();
                else nm.Shutdown();
            }
        }
        GUILayout.EndArea();
    }

    static ISession CurrentSession()
    {
        if (MultiplayerService.Instance == null) return null;
        foreach (var session in MultiplayerService.Instance.Sessions.Values) return session;
        return null;
    }

    void HideMenu()
    {
        if (MenuToHide != null) MenuToHide.enabled = false;
    }
}
