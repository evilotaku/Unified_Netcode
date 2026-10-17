using Unity.Entities;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.TextCore.Text;

namespace Unity.Netcode
{
    public partial class GameSetup : GhostBehaviour
    {
       
        GhostComponentRef<FPSGameSetup> GameSetupField;

        // Start is called once before the first execution of Update after the MonoBehaviour is created
        void Start()
        {
            if (IsServer)
            {
                print("Server is setting up the game");
                GameSetupField.Value = new FPSGameSetup();
            }

        }

        // No prediction overrides: netcode calls every overridden one each tick, even when empty.
    }
}
