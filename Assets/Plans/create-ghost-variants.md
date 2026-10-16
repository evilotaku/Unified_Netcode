# Project Overview
- Game Title: Unified Netcode FPS
- High-Level Concept: First-person shooter using the new Unity 6 "Unified Netcode" (GameObject-centric) workflow.
- Goal: Convert player and character prefabs to use `GhostObject` with `GhostField` (via `GhostBehaviour`) instead of traditional ECS authoring components.

# Game Mechanics
## Core Gameplay Loop
Synchronization of player state (movement, control) using the GameObject layer bridge for Netcode for Entities.
## Controls and Input Methods
- Character movement and rotation synced via `GhostField`.

# Key Asset & Context
- `Assets/Samples/Character Controller/6.7.0/Standard Characters/FirstPerson/Prefabs/FirstPersonPlayer.prefab` (Source)
- `Assets/Samples/Character Controller/6.7.0/Standard Characters/FirstPerson/Prefabs/FirstPersonCharacter.prefab` (Source)
- `GhostObject`: Bridge component.
- `GhostBehaviour`: Base class for GameObject-layer networked logic.
- `GhostField<T>`: Data replication primitive for `GhostBehaviour`.

# Implementation Steps
## 1. Create GhostBehaviour Scripts
- **Description**: Implement custom `GhostBehaviour` classes that replicate the necessary data for the player and character controllers using `GhostField`.
- **Assigned role**: developer
- **Dependencies**: None
- **Parallelizable**: Yes

### 1.1 FirstPersonPlayerGhost Script
- Create `Assets/_Scripts/Ghost/FirstPersonPlayerGhost.cs`.
- Define fields: `GhostField<Entity> m_ControlledCharacter`, `GhostField<float> m_LookInputSensitivity`.
- These fields will replace the data stored in the legacy `FirstPersonPlayer` ECS struct.

### 1.2 FirstPersonCharacterGhost Script
- Create `Assets/_Scripts/Ghost/FirstPersonCharacterGhost.cs`.
- Define fields for movement properties (e.g., `GroundMaxSpeed`, `JumpSpeed`, `Gravity`).
- These fields will replace the data in the legacy `FirstPersonCharacterComponent`.

## 2. Create and Configure Prefab Variants
- **Description**: Create new Prefab Variants in `Assets/_Prefabs/Ghost/` to maintain the original assets while implementing the new workflow.
- **Assigned role**: developer
- **Dependencies**: Step 1
- **Parallelizable**: Yes

### 2.1 FirstPersonPlayer Variant
- Create a variant of `FirstPersonPlayer.prefab` at `Assets/_Prefabs/Ghost/FirstPersonPlayer_Ghost.prefab`.
- Remove: `FirstPersonPlayerAuthoring`, `GhostAuthoringComponent`, `GhostAuthoringInspectionComponent`, `LinkedEntityGroupAuthoring`.
- Add: `GhostObject`, `FirstPersonPlayerGhost`.
- Configure `GhostObject`: `HasOwner = true`, `DefaultGhostMode = OwnerPredicted`.

### 2.2 FirstPersonCharacter Variant
- Create a variant of `FirstPersonCharacter.prefab` at `Assets/_Prefabs/Ghost/FirstPersonCharacter_Ghost.prefab`.
- Remove: `FirstPersonCharacterAuthoring`, `GhostAuthoringComponent`, `GhostAuthoringInspectionComponent`, `LinkedEntityGroupAuthoring`.
- Add: `GhostObject`, `FirstPersonCharacterGhost`.
- Configure `GhostObject`: `HasOwner = true`, `DefaultGhostMode = Predicted`.

## 3. Update Scene References
- **Description**: Point the spawning system to the new GhostObject-based prefabs.
- **Assigned role**: developer
- **Dependencies**: Step 2
- **Parallelizable**: No

### 3.1 Update GameSetup
- Modify the `GameSetup` object in `GameSubScene`.
- Update the `CharacterPrefab` and `PlayerPrefab` references in the `FPSGameSetupAuthoring` component to point to the new variants in `Assets/_Prefabs/Ghost/`.

# Verification & Testing
1. **Compilation Check**: Ensure `GhostBehaviour` scripts and their source-generated parts compile without errors.
2. **Inspector Check**: Verify that `GhostObject` and the new `GhostBehaviour` components appear correctly on the variants and that fields can be edited.
3. **Runtime Test**: Connect via QuickJoin and verify that players spawn and move as expected. Check the "Netcode" debugger to confirm `GhostField` data is replicating.
