# Project Overview
- Game Title: Unified Netcode FPS
- High-Level Concept: A first-person shooter using Unity 6's Unified Netcode (Netcode for Entities) with a GameObject-centric authoring workflow.
- Players: Multiplayer (Client/Server)
- Render Pipeline: URP (PC_RPAsset)
- Target Platform: Standalone Windows

# Game Mechanics
## Core Gameplay Loop
Players join a session via QuickJoin, are spawned into the scene, and can move/interact using a first-person controller.
## Controls and Input Methods
- Keyboard (WASD) for movement.
- Mouse for looking/aiming.
- Space for jumping.
- Input is handled via the New Input System and synced using Netcode for Entities prediction.

# UI
- QuickJoin Button: Initiates connection to a session.

# Key Asset & Context
- `Assets/Samples/Character Controller/6.7.0/Standard Characters/FirstPerson/Prefabs/FirstPersonPlayer.prefab`: The player entity prefab.
- `Assets/Samples/Character Controller/6.7.0/Standard Characters/FirstPerson/Prefabs/FirstPersonCharacter.prefab`: The character controller prefab.
- `Assets/Scenes/SampleScene/GameSubScene.unity`: The subscene containing game setup.
- `GhostObject`: The new Unified Netcode component for GameObject-based ghost authoring.

# Implementation Steps
## 1. Convert Prefabs to GhostObject Workflow
- **Description**: Replace the legacy `GhostAuthoringComponent` with the new `GhostObject` component on player and character prefabs. This aligns the project with the "Unified" workflow.
- **Assigned role**: developer
- **Dependencies**: None
- **Parallelizable**: Yes

### 1.1 FirstPersonPlayer Prefab
- Remove `Unity.Netcode.GhostAuthoringComponent`.
- Remove `Unity.Netcode.GhostAuthoringInspectionComponent`.
- Remove `Unity.Entities.Hybrid.Baking.LinkedEntityGroupAuthoring`.
- Add `Unity.Netcode.GhostObject`.
- Configure `GhostObject`:
    - `HasOwner`: True
    - `DefaultGhostMode`: OwnerPredicted
    - `SupportedGhostModes`: All

### 1.2 FirstPersonCharacter Prefab
- Remove `Unity.Netcode.GhostAuthoringComponent`.
- Remove `Unity.Netcode.GhostAuthoringInspectionComponent`.
- Remove `Unity.Entities.Hybrid.Baking.LinkedEntityGroupAuthoring`.
- Add `Unity.Netcode.GhostObject`.
- Configure `GhostObject`:
    - `HasOwner`: True
    - `DefaultGhostMode`: Predicted
    - `SupportedGhostModes`: All

## 2. Update Spawning and Registration
- **Description**: Ensure the prefabs are correctly registered and handled by the spawning system.
- **Assigned role**: developer
- **Dependencies**: Step 1
- **Parallelizable**: No

### 2.1 Verify GameSetup References
- Open `GameSubScene`.
- Inspect `GameSetup` GameObject.
- Ensure the `FPSGameSetupAuthoring` component still points to the correct `FirstPersonPlayer` and `FirstPersonCharacter` prefabs after their modification.

### 2.2 Ghost Collection (If Required)
- If prefabs are not automatically picked up, create a `GhostCollection` in the scene and add the prefabs to it. (Note: Unity 6 usually handles this via references, but manual registration ensures consistency).

# Verification & Testing
1. **Baking Test**: Open the subscene and ensure no baking errors occur.
2. **Multiplayer Test**:
    - Run the game in the editor.
    - Connect via QuickJoin.
    - Verify that the player spawns correctly.
    - Verify that movement is synced between client and server.
    - Check the "Netcode" tab in the Entities Hierarchy to see if the entities are correctly identified as Ghosts.
