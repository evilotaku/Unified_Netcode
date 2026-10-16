# Unified Netcode

A Unity 6 sample project that runs **Netcode for GameObjects (NGO)** in *unified mode* on top of **Netcode for Entities (N4E)**. Ordinary GameObjects become ghosts: N4E replicates them with snapshots, predicts them on the owning client and rolls them back. They also keep working with NGO's `NetworkObject`, `NetworkVariable` and RPCs.

The game built on this layer is a networked first-person melee brawler. It has combos, blocking and parrying, a dash, a client-predicted fireball that can be parried back, active-ragdoll hit reactions and carryable items.

> **Experimental.** N4E's GameObject layer sits behind `NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL`, and the Unity Editor is a beta. APIs and behavior change between package versions. This project documents what has been tested with the versions below.

## Requirements

| | Version |
|---|---|
| Unity | 6000.7.0b1 |
| Netcode for GameObjects (`com.unity.netcode.gameobjects`) | 3.1.0 |
| Netcode for Entities (`com.unity.netcode`) | 7.1.0 |
| Multiplayer Services (`com.unity.services.multiplayer`) | 2.4.0 |
| Multiplayer Play Mode (`com.unity.multiplayer.playmode`) | 3.0.0 |
| Universal Render Pipeline | 17.7.0 |
| Input System | 6.7.0 |

## Getting started

1. Clone the repository and open it in Unity 6000.7.0b1.
2. Open `Assets/Scenes/SampleScene.unity`.
3. Enter Play mode and start a session:
   - **Local:** press **Host (direct)** in the on-screen menu. To add a second player, open a Multiplayer Play Mode virtual player (**Window > Multiplayer > Multiplayer Play Mode**) and press **Client (direct)** there. Clients connect to `127.0.0.1:7979`.
   - **Relay:** use the session menu's Quick Join. This needs the project linked to a Unity Cloud project with Authentication and Relay enabled.

The host also spawns three practice dummies: one that blocks, one that parries, and one that attacks.

### Controls

| Action | Keyboard and mouse | Gamepad |
|---|---|---|
| Move / look | WASD / mouse | Left stick / right stick |
| Light attack | Left mouse button | West button (X / Square) |
| Medium attack | Right mouse button | North button (Y / Triangle) |
| Heavy attack | F | Right trigger |
| Special (fireball) | R | Right shoulder |
| Block (press to parry) | Q | Left shoulder |
| Dash | Double-tap a direction | Flick the left stick twice |

## How unified mode is set up

These settings are already applied in this project. Copy them to enable unified mode in your own project:

1. **Scripting define symbols:** `NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL`, `UNIFIED_NETCODE` and `OUT_OF_BAND_RPC`.
2. **`Assets/NetcodeConfig.asset`:** Host World Mode Selection set to **Single World**, and Client Server Bootstrap enabled.
3. **Bootstrap:** NGO's `UnifiedBootstrap` creates the world. Custom bootstraps are disabled.
4. **NetworkManager:** bridged prefabs go in the prefab list, and **Player Prefab is left empty**. `UnifiedPlayerSpawner` spawns players and sets the ghost owner.
5. **Prefabs:** add a `GhostObject` next to the `NetworkObject`, and write gameplay in `GhostBehaviour.PredictionUpdate` with `GhostField` state.

The full walkthrough, including the workarounds for current package issues, is in the guide below.

## Documentation

| Guide | Covers |
|---|---|
| [Unified Netcode for GameObjects](Documentation/UnifiedNetcode-GameObjects.md) | How the NGO and N4E layers fit together; ghost objects; prediction and input; RPCs and custom messages; sessions and Relay; known issues and workarounds |
| [Combat System](Documentation/CombatSystem.md) | Attacks, combos, hit detection, health, blocking and parrying, the dash, the predicted fireball, effects, ragdolls, networking and dummies |

## Project layout

| Path | Contents |
|---|---|
| `Assets/_Scripts/Unified/` | Unified-mode glue: the player spawner, the host connection fix, local session buttons, and the Relay network handler (`Sessions/`) |
| `Assets/_Scripts/Ghost/` | The predicted first-person player controller and animator |
| `Assets/_Scripts/Combat/` | The combat system: `CombatControllerGhost`, `Projectile`, `Health`, effects, ragdolls and dummies |
| `Assets/_Scripts/Interaction/` | Carryable items and the server-validated interact request |
| `Assets/_Prefabs/` | Player, dummy, projectile and item prefabs |
| `Documentation/` | The guides above |

`Assets/_Scripts/DOTS Character Controller/`, `GoInGame.cs`, `DOTS_NetworkManager.cs` and `Ghost/Systems/RPC_Systems.cs` are left over from an earlier pure-ECS version and aren't used by the current scene.

## Known issues

- On a Single World host, the host's own connection isn't marked in game by NGO. `HostLocalConnectionInGameSystem` adds `NetworkStreamInGame` itself.
- `NetworkObjectBridge` resets prefabs' Supported Ghost Modes to Interpolated and turns on host smoothing. `BridgeGhostModeGuard` restores both on Play and before builds.
- Multiplayer Play Mode virtual players can keep running old code. Reload their scripts after a change.

See [Known issues and workarounds](Documentation/UnifiedNetcode-GameObjects.md#known-issues-and-workarounds) for the full list.
