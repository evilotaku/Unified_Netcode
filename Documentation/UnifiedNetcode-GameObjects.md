# Unified Netcode for GameObjects

How this project combines Netcode for GameObjects (NGO) and Netcode for Entities (N4E) so ordinary GameObjects can be ghosts: replicated by N4E snapshots, predicted, and still usable with NGO's `NetworkObject`, `NetworkVariable` and RPCs.

The project's melee combat, built on this layer, has its own guide: [Combat System](CombatSystem.md).

> **Experimental.** The GameObject layer is behind `NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL`. APIs and behavior change between package versions. This guide reflects the versions below and what has been tested in this project.

| Package | Version |
|---|---|
| Unity | 6000.7.0b1 |
| Netcode for GameObjects (`com.unity.netcode.gameobjects`) | 3.1.0 |
| Netcode for Entities (`com.unity.netcode`) | 7.1.0 |
| Multiplayer Services (`com.unity.services.multiplayer`) | 2.3.3 |

## Contents

1. [How it fits together](#how-it-fits-together)
2. [Enabling unified mode](#enabling-unified-mode)
3. [Working with ghost objects](#working-with-ghost-objects)
   - [`GhostObject` settings](#ghostobject-settings) and [runtime API](#ghostobject-runtime-api)
   - [`GhostBehaviour`](#ghostbehaviour), [`GhostField<T>`](#ghostfieldt), [`GhostComponentRef<T>`](#ghostcomponentreft)
   - [`GhostBehaviour` callback order](#ghostbehaviour-callback-order)
4. [Ghost entity mapping and syncing](#ghost-entity-mapping-and-syncing)
5. [Prediction and input](#prediction-and-input)
   - [What gets rolled back](#what-gets-rolled-back)
   - [Client predicted spawning](#client-predicted-spawning)
   - [When to use `PredictionPhysicsUpdate`](#when-to-use-predictionphysicsupdate)
6. [RPCs](#rpcs)
7. [Sessions and Relay](#sessions-and-relay)
8. [Known issues and workarounds](#known-issues-and-workarounds)
9. [Project file reference](#project-file-reference)

Behavior described as "in the package source" was read from `com.unity.netcode` 7.1.0 (`Runtime/GameObjectLayer/`). Behavior described as tested was run in this project.

---

## How it fits together

- **N4E owns the simulation.** NGO creates one N4E world and uses it as its transport (`UnifiedNetcodeTransport`). State replication, ghost spawning, prediction, physics and the tick loop are all N4E.
- **A ghost is a GameObject with a `GhostObject`.** Each one is linked to an entity. Scripts derive from `GhostBehaviour` and replicate data with `GhostField<T>`.
- **A bridged prefab also has a `NetworkObject`.** NGO adds a hidden `NetworkObjectBridge` component. The object is spawned through NGO, so NGO features (ownership callbacks, `NetworkVariable`, `[Rpc]`) work alongside the ghost.
- **Single World host.** The host runs one world (`HostSingleWorld-N`) that is both server and client. There's only one copy of each object on the host, so `IsServer` and `IsClient` can both be true on the same instance.

| Question | Host (Single World) | Remote client | Dedicated server |
|---|---|---|---|
| `IsServer` | true | false | true |
| `IsClient` | true | true | false |
| Copies of each ghost | 1 | 1 | 1 |

Code that must run once, on the authority, should check `IsServer`. Code for the local player's view should check `IsClient` plus ownership (see [Ownership](#ownership)).

---

## Enabling unified mode

### 1. Packages

Install NGO 3.x. It pulls in N4E, Entities, Burst and Collections as dependencies. For Relay sessions, also install Multiplayer Services.

### 2. Scripting define symbols

**Project Settings > Player > Other Settings > Scripting Define Symbols** needs:

| Define | Why |
|---|---|
| `NETCODE_GAMEOBJECT_BRIDGE_EXPERIMENTAL` | Makes N4E's GameObject layer public: `GhostObject`, `GhostBehaviour`, `GhostField`, `ICustomMessage`, `Netcode.RPC`. |
| `UNIFIED_NETCODE` | Turns on NGO's unified code paths: ghost prefabs, `NetworkObjectBridge`, `UnifiedBootstrap`. |
| `OUT_OF_BAND_RPC` | Required for `UnifiedNetcodeTransport`. Without it NGO doesn't switch transports. |

`UNIFIED_NETCODE_7_0_0` is set automatically by NGO's assembly definition when N4E 7.x is installed. It doesn't need to be in the list, though it's harmless.

### 3. Netcode config

In `Assets/NetcodeConfig.asset`:

- **Host World Mode Selection:** `Single World`. The rest of this guide assumes it.
- **Enable Client Server Bootstrap:** on.
- **Client Tick Rate → Default Classification Allowable Tick Period:** 5 (the default), and **Num Additional Client Predicted Ghost Lifetime Ticks:** 30 (default 0). The longer lifetime gives client predicted spawns more time to be matched. The window was 10 for a while and went back to 5 on 2026-10-15. See [Client predicted spawning](#client-predicted-spawning).

### 4. Bootstrap

NGO provides its own bootstrap (`UnifiedBootstrap`), which creates the world when NGO starts. Any custom `ClientServerBootstrap` must not run as well:

- `Assets/_Scripts/NetcodeBootstrap.cs` is marked `[DisableBootstrapOverrides]` for this reason.
- The scene has an `OverrideAutomaticNetcodeBootstrap` set to **Disable Automatic Bootstrap**, so N4E doesn't create default worlds on Play.

### 5. NetworkManager

- **Transport:** leave whatever transport is assigned. If any prefab in the NetworkManager's prefab lists is a ghost, NGO replaces the transport at startup with `UnifiedNetcodeTransport`.
- **Prefab lists:** add every bridged prefab to `Assets/DefaultNetworkPrefabs.asset` (or another list on the NetworkManager).
- **Player Prefab:** leave it **empty**. `UnifiedPlayerSpawner` spawns players instead, because NGO doesn't set the ghost owner. See [Spawning players](#spawning-players).

### 6. Check it works

Enter Play mode and start a host, for example with the session menu's Quick Join.

- An Entities world named `HostSingleWorld-1` appears in the Entities Hierarchy.
- The NetworkManager has a `UnifiedNetcodeTransport` component.
- The player spawns, and `GhostObject.IsSpawned` is true.

---

## Working with ghost objects

### Making a prefab a ghost

1. Add a **`GhostObject`** to the prefab root.
2. Set the important fields:
   - **Default Ghost Mode:** `Interpolated` for objects nobody predicts, such as crates, or `OwnerPredicted` for player-controlled objects.
   - **Supported Ghost Modes:** must include the default mode. Use `All` for predicted prefabs.
   - **Has Owner:** on for anything a player controls or sends input for.
   - **Parent Replication:** on if the object will be parented to another ghost at runtime, for example a carried item.
   - **Single World Host Interpolation Smoothing:** `Disabled` for predicted objects. See [Known issues](#host-smoothing-overwrites-predicted-transforms).
3. To bridge it to NGO, add a **`NetworkObject`**. NGO adds the hidden `NetworkObjectBridge`. Turn off **Synchronize Transform** on the `NetworkObject`, because the ghost already replicates the transform.
4. Add the prefab to a NetworkManager prefab list.

Ghost GameObjects can't be placed directly in a scene. They have to be spawned from a prefab at runtime. `CarryableItemSpawner` stands in for scene placement.

### `GhostObject` settings

Every option on the `GhostObject` Inspector. Settings are read **once, when the prefab is registered** (see [Prefab registration](#prefab-registration)). Changing them on the prefab asset at runtime has no effect, and changing **Parent Replication** after registration makes parenting fail with an error.

**Ghost mode and prediction**

| Setting | Default | Effect |
|---|---|---|
| **Default Ghost Mode** | `Interpolated` | How clients simulate the ghost when it spawns. `Interpolated`: shown in the past, smoothed between snapshots. `Predicted`: every client predicts it. `OwnerPredicted`: the owning client predicts it, others interpolate it. `OwnerPredicted` needs **Has Owner**. |
| **Supported Ghost Modes** | `All` | Which modes the ghost can be in. `Interpolated` or `Predicted` alone lets netcode strip unused components, but then the mode can't change at runtime. Must include the default mode. `OwnerPredicted` needs `All`. The NGO bridge forces this to `Interpolated` (see [Known issues](#the-bridge-forces-interpolated-mode)). |
| **Has Owner** | off | Adds `GhostOwner` and `GhostOwnerIsLocal`, so the ghost can have an owning connection. Required for `OwnerNetworkId`, owner prediction and `PredictionGatherInput`. |
| **Support Auto Command Target** | on | Adds `AutoCommandTarget`, so input components on a ghost the client owns are sent to the server automatically. Required for `PredictionGatherInput`. |
| **Rollback Predicted Spawned Ghost State** | off | For ghosts a client spawns itself during prediction. On: re-predict them from their spawn tick until the server's copy arrives. Off: they're never rolled back before then. |
| **Rollback Prediction On Structural Changes** | on | When a predicted ghost has no prediction backup (for example after a component was added or removed), replay from the last snapshot. Turning it off saves client CPU but can restore wrong values for re-added components. |

**Sending and bandwidth**

| Setting | Default | Effect |
|---|---|---|
| **Optimization Mode** | `Dynamic` | `Dynamic`: sent at a regular interval even when nothing changed, which allows more aggressive compression. Best for things that move often. `Static`: only sent when something changes. Snapshots are smaller when it doesn't change and larger when it does. Best for things that rarely change. |
| **Importance** | `1` | Relative priority when a snapshot can't fit every ghost. A ghost with importance 100 is sent about 100 times as often as one with importance 1. |
| **Max Send Rate** | `0` (no limit) | Highest rate, in Hz, at which this ghost type is sent. Spawns and despawns ignore it. On predicted ghosts it can cause mispredictions, because clients only roll back ghosts that are in the snapshot. |
| **Snapshot History Size** | `0` (global default, 32) | Snapshot history slots kept per ghost type, on the server for delta baselines and on the client for received snapshots. Clamped to 4–32. Lower saves memory, but too low forces full resends at high ping. |
| **Use Pre Serialization** | off | Serializes the ghost once for all connections instead of once per connection. Saves server CPU for ghosts that are almost always sent and have many fields, such as players. |
| **Use Uniform Scale** | off | Off: the ghost replicates per-axis scale through a `PostTransformMatrix`. On: only `transform.localScale.x` is used, as a uniform scale. Saves a component and a change bit per snapshot on ghosts that never scale non-uniformly. |

**Spawning, parenting and the host**

| Setting | Default | Effect |
|---|---|---|
| **Delay Spawning** | off | Off: instantiating the prefab spawns it on the network. On: instantiate first, then call `GhostObject.Spawn()`. `Despawn()` removes it from the network and destroys the GameObject. Not used in this project; bridged prefabs spawn through `NetworkObject.Spawn()` instead. |
| **Parent Replication** | off | Replicates the ghost's parent and raises `OnParentChanged`. See [Parenting](#parenting). |
| **Skip Automatic Prefab Registration** | off | On: netcode doesn't register the prefab when a world starts. Call `Netcode.RegisterPrefab(prefab)` yourself, in the same order on every peer. Useful for changing settings in code before registering, or for prefabs loaded later, such as Addressables. |
| **Single World Host Interpolation Smoothing** | `Disabled` | Only on a Single World host. `Interpolate` smooths the ghost's rendered transform between ticks, adding about half a tick of latency. Keep it `Disabled` on predicted ghosts (see [Known issues](#host-smoothing-overwrites-predicted-transforms)). |

**Shown but not used by `GhostObject` in N4E 7.1.** These come from the settings base class that `GhostAuthoringComponent` (the ECS ghost component) shares. The package source never applies them to GameObject ghosts:

- **Track Interpolation Delay**
- **Ghost Group.** Its only effect is to force **Optimization Mode** to `Dynamic`.
- **Use Single Baseline**

**Prefab variants.** The ghost type id comes from the prefab's asset GUID, so a variant is a separate ghost type from its base prefab. A bridged variant also needs its own `NetworkObject` hash saved on disk (see [Known issues](#prefab-variants-share-the-base-prefabs-networkobject-hash)).

### `GhostObject` runtime API

| Member | Purpose |
|---|---|
| `IsSpawned` | True once the ghost has a live entity. |
| `IsServer` / `IsClient` | The role of the world this copy lives in. False before spawn. |
| `CanWriteState` | True when this copy has `PredictedGhost`: always on the server, and on clients that predict it. |
| `Entity`, `World` | The linked entity and its `NetcodeWorld`. Use `World.EntityManager` for ECS access, and `World.NetworkTime` for tick information. |
| `GhostId`, `SpawnTick`, `GhostType` | The network identity. `GhostId` + `SpawnTick` identify a ghost across peers. `GhostInfo` returns all three in one read. |
| `OwnerNetworkId` | Gets or sets the ghost owner. Needs **Has Owner**. Set it on the server, before the first snapshot (see [Spawning players](#spawning-players)). |
| `GhostTransform`, `Position`, `Rotation` | The tick-accurate, unsmoothed entity transform (`LocalTransform`), in local space. It can differ from the GameObject transform while smoothing. On the server, the GameObject transform is copied over it every frame, so move the GameObject instead. |
| `PublishTransform()` | Copies the entity transform onto the GameObject transform. |
| `Spawn()` / `Despawn()` | Only with **Delay Spawning**. |
| `OnParentChanged` | `event Action<Transform previous, Transform next>`. Only raised with **Parent Replication**, and only for parent changes netcode accepted. |
| `ConvertToPredicted(t)` / `ConvertToInterpolated(t)` | Client only. Switches this ghost's mode at runtime, blending over `t` seconds. Needs **Supported Ghost Modes** `All`. Not used in this project. |

### GhostBehaviour

Scripts on a ghost derive from `GhostBehaviour` and **must be `partial`**, because source generation adds the replication code.

```csharp
public partial class BridgeTestGhost : GhostBehaviour
{
    public GhostField<float> Hue;

    void Update()
    {
        if (IsServer)
            Hue.Value = Mathf.Repeat(Time.time * 0.1f, 1f);
        // Every copy reads the replicated value.
        GetComponent<Renderer>().material.color = Color.HSVToRGB(Hue.Value, 0.8f, 1f);
    }
}
```

| Member | Purpose |
|---|---|
| `Ghost` | The `GhostObject` (see [runtime API](#ghostobject-runtime-api)). Found with `GetComponentInParent`, so a `GhostBehaviour` can sit on a child of the ghost root. |
| `IsServer` / `IsClient` | Which role this copy has. Both are true on a Single World host. |
| `CanWriteState` | True on the server, and on clients that predict this ghost. |
| `OnGhostSpawn()` / `OnGhostDespawn()` | Called when the ghost spawns or despawns, on every peer. Exceptions thrown here are logged, not rethrown. |
| `Awake()` / `OnDestroy()` | These are **virtual**. Call `base.Awake()` and `base.OnDestroy()` when you override them. The base versions hold a reference on the ghost's entity (see [Entity lifetime](#entity-lifetime)). |
| `PredictionGatherInput`, `PredictionUpdate`, `PredictionPhysicsUpdate` | See [Prediction and input](#prediction-and-input). |
| `TryGetPreviousInputData<T>(out T)` | The input from the previous tick, for detecting presses and releases inside prediction. |
| `TryGetInputDataAtTick<T>(tick, out T)` | The input stored for any tick, or the closest earlier one. |

For when each callback runs, in a frame and over a ghost's life, see [GhostBehaviour callback order](#ghostbehaviour-callback-order).

**Prediction callbacks run on these conditions:**

- **The method must be overridden** with the exact public signature. Netcode finds overrides by reflection when entering Play mode, and when building. An empty override still costs a call per tick, so leave out the ones you don't use.
- **`Start` must have run.** The behaviour isn't called until its own `Start` has run, if it has one, and the `GhostObject`'s `Start` has run.
- **The behaviour must be enabled and active** (`isActiveAndEnabled`). Disabling one skips its callbacks without affecting the rest of the ghost.
- **Order between scripts:** behaviours are grouped by **Script Execution Order** (or `[DefaultExecutionOrder]`), lowest first. Within a group, order follows the ghosts, not the scripts. The order is captured when entering Play mode, so changes made during Play mode don't apply until the next one.

**Restrictions:**

- **One of each type per ghost.** Two `GhostBehaviour`s of the same type, or where one type inherits from the other, would share their generated `GhostField` components. The package logs *"Having two GhostBehaviours with a shared sets of GhostField … is undefined behaviour"*.
- **Only the root transform is replicated.** Child GameObjects are not ghosts. `GhostBehaviour`s on children share the root's entity, and their children's transforms aren't synced.
- **File name must match the class**, as for any `MonoBehaviour` saved on a prefab.

### GhostBehaviour callback order

The order below was measured on 2026-10-13. A temporary script logged every callback, along with the frame, the tick, the `NetworkTime` flags and the ghost id. It sat on `FPS_Player`, the dummies and `Fireball_Ghost`. Logs were taken on a Single World host and on a Multiplayer Play Mode client connected to it, both in the Editor. Both Editors stalled often, so frames covered many ticks, which makes replays and batching easy to see.

#### Within one frame

Netcode adds its ECS groups to the *end* of Unity's player loop phases. `SimulationSystemGroup` goes after the `Update` phase, and `PresentationSystemGroup` after `PreLateUpdate`, which ends with `LateUpdate` (`ScriptBehaviourUpdateOrder` in Entities). So every `GhostBehaviour` sees this order each frame:

| # | Callback | Runs | Notes |
|---|---|---|---|
| 1 | `FixedUpdate` | Unity's fixed timestep, 0 to many times | Not tied to ticks: it ran 2 to 10 times per frame in the test. Physics doesn't step here, because netcode switches `Physics.simulationMode` to `Script` and steps physics itself. Nothing in it is rolled back, so don't use it for ghost gameplay. |
| 2 | `Update` | Once | **Before this frame's tick.** Ghost state is what the previous frame's tick left. |
| 3 | `PredictionGatherInput` | Once, at the start of `SimulationSystemGroup` | Only on ghosts that have an input component and are owned by this client. The server's copies of remote players, and the dummies (owner `-1`), never get it. Its `TickedDeltaTime` is the frame's delta time, not a tick's. |
| 4 | `PredictionPhysicsUpdate` | Once per physics step, then physics steps | Only on ghosts with a `GhostRigidbody`. See [Order inside one tick](#order-inside-one-tick). |
| 5 | `PredictionUpdate` | Once per predicted tick | On a host, at most once per frame, and not at all in a frame shorter than a tick. On a client, once per replayed tick, once per new tick, then once for a partial tick (see below). |
| 6 | `LateUpdate` | Once | **After this frame's tick**, so it sees the tick's results. It runs before `PresentationSystemGroup`, where netcode smooths transforms (in the package source). |

Steps 3 to 5 run inside `SimulationSystemGroup`, after every `Update` has finished. `IsFirstTimeFullyPredictingTick` reads false in `Update` and `LateUpdate`, so use it only inside prediction.

**Logged on the host, one frame at 60 Hz (the Editor was running about 4 ticks behind per frame):**

```
f=2408 Update                                       tick=2723
f=2408 PredictionGatherInput    dt=0.2569           tick=2727
f=2408 PredictionPhysicsUpdate  dt=0.0167  ×4       tick=2727 firstFull=True
f=2408 PredictionUpdate         dt=0.0667           tick=2727 firstFull=True
f=2408 LateUpdate                                   tick=2727
```

**A slow server batches ticks.** When a frame covers several ticks, the server runs them as **one** batched tick, up to `MaxSimulationStepBatchSize` ticks (4 by default). `PredictionUpdate` then runs **once**, with `TickedDeltaTime` set to the whole batch (0.0667 s above), and `ServerTick` jumps by 4. Physics still takes one step per tick, so `PredictionPhysicsUpdate` runs 4 times. This affects:

- **Delta time:** always use `context.TickedDeltaTime`, never a fixed `1 / tickRate`.
- **Counting ticks:** code that counts `PredictionUpdate` calls or assumes one tick per call is wrong on a slow server. Anything timed inside a tick, such as a hit window, should check the whole span the tick covers. `CombatControllerGhost` does this.

A client predicts one tick per `PredictionUpdate` by default (`MaxPredictionStepBatchSizeRepeatedTick` and `…FirstTimeTick` are 1).

**On a remote client, steps 4 and 5 repeat** in the prediction loop:

1. **Restore.** The ghost is reset to the newest snapshot's tick. See [What gets rolled back](#what-gets-rolled-back).
2. **Replay.** Each tick from the one after the snapshot up to the last full tick runs `PredictionPhysicsUpdate` and `PredictionUpdate`. The first one has `IsFirstPredictionTick` set, and none has `IsFirstTimeFullyPredictingTick`.
3. **New full ticks.** Each tick predicted in full for the first time has `IsFirstTimeFullyPredictingTick` true. That includes a tick that was only run as a partial tick in the previous frame.
4. **Partial tick.** A partial tick usually comes last. It runs `PredictionUpdate` only, with no physics step, and has `IsPartialTick` and `IsFinalPredictionTick` set. Its delta time is a fraction of a tick. See [Partial ticks and repeated side effects](#partial-ticks-and-repeated-side-effects).

**Logged on the client for its own player, one frame** (condensed; every full tick also ran `PredictionPhysicsUpdate` just before):

```
f=2495 Update                                      tick=1965 (partial)
f=2495 PredictionGatherInput   dt=0.1650           tick=1975
f=2495 PredictionUpdate        dt=0.0167           tick=1956  firstPred      ← replay from the snapshot
f=2495 PredictionUpdate        dt=0.0167           tick=1957 … 1964           ← replay
f=2495 PredictionUpdate        dt=0.0167           tick=1965 … 1974  firstFull ← new ticks
f=2495 PredictionUpdate        dt=0.0017           tick=1975  finalPred partial
f=2495 LateUpdate                                  tick=1975 (partial)
```

In the next frame the replay started at tick 1967. Tick 1975, which had only run as a partial tick, then ran in full with `IsFirstTimeFullyPredictingTick` true.

- **A client doesn't batch ticks.** Every client `PredictionUpdate` had a delta time of one tick (0.0167 s), or less for a partial tick. `MaxPredictionStepBatchSizeRepeatedTick` and `…FirstTimeTick` default to 1.
- **`PredictionUpdate` can run many times in one frame on a client**, while `Update` and `LateUpdate` still run once. That frame ran it 20 times.
- **Interpolated ghosts get no prediction callbacks.** On the client, the host's player and the dummies are interpolated. They got only `FixedUpdate`, `Update` and `LateUpdate`.

#### Over a ghost's life

**Spawned on the server, or predicted-spawned on a client** (logged on both, without **Delay Spawning**):

```
OnGhostSpawn   ← from the GhostObject's Awake, inside Instantiate
Awake
OnEnable
Start          ← later in the same frame, after the prediction loop
PredictionUpdate, Update, LateUpdate …   ← from the next frame
```

- `Instantiate` runs the `GhostObject`'s `Awake` first, because its execution order is `int.MinValue`. That creates the entity, writes the prefab's `GhostField` values into it, and calls `OnGhostSpawn` on every `GhostBehaviour`. Then each behaviour's own `Awake` and `OnEnable` run.
- **The ghost misses the rest of the tick it was spawned in.** A ghost spawned from `PredictionUpdate` gets its `Start` after the prediction loop, so it isn't simulated in that tick. Prediction callbacks wait for `Start` (see the conditions above), so its first `PredictionUpdate` comes in the next frame. On the client, the fireball was spawned at tick 1406. The rest of that frame's loop (tick 1407 and a partial 1408) ran without it, and then `Start` ran.
- **A predicted spawn is simulated only from the tick after its spawn tick.** In the next frame, the shooter replayed from tick 1400, but the fireball's first `PredictionUpdate` was for tick 1407. Until the server's copy arrived, it ran only new ticks and partial ticks, never replays, because **Rollback Predicted Spawned Ghost State** is off.
- **Matching fires no callback.** Seven frames after the spawn, the client's fireball took the server's ghost id (0, then 11). From the next frame on, it was replayed from snapshots like any predicted ghost. `OnGhostSpawn`, `Awake` and `Start` didn't run again.
- With **Delay Spawning**, `OnGhostSpawn` runs later, when `Spawn()` is called.

**Received by a client** (logged on the client for the players, the dummies, and a server-spawned fireball):

```
OnGhostSpawn      ← the GameObject is still inactive
Awake, OnEnable   ← when it's activated
Start             ← later in the same frame
```

- The client creates received ghosts during `SimulationSystemGroup`, so a new ghost's `Awake` runs after that frame's `Update`.
- `GhostField`s and the ghost id already hold the server's values in `OnGhostSpawn`.
- When several ghosts arrive in one frame, every `OnGhostSpawn` runs first, then each ghost's `Awake` and `OnEnable`, then every `Start`.

**On both paths, `OnGhostSpawn` runs before `Awake`.** `GhostField`s already hold the prefab's or the server's values there, but anything set up in your own `Awake` doesn't exist yet. Put setup that `OnGhostSpawn` needs in field initializers, or move that work to `Awake` or `Start`.

**Despawned** (logged for the fireball on the host, after `Destroy(gameObject)` on the server, and on the client when the despawn arrived):

```
OnDisable
OnGhostDespawn   ← from the GhostObject's OnDestroy
OnDestroy        ← IsServer and IsClient both read false here
```

- The order was the same on the host and the client.
- On the server, `Destroy` is deferred, so all three ran at the end of the frame that called it. A client destroys a despawned ghost with `DestroyImmediate`, from `GhostDespawnSystem` (in the package source).
- **`OnGhostDespawn` runs from the `GhostObject`'s `OnDestroy`,** or from `Despawn()`. Unity doesn't fix the order of `OnDestroy` between components. In every run the `GhostObject`'s ran first, so in the behaviour's `OnDestroy`, `IsServer` and `IsClient` were already false. Check the role in `OnGhostDespawn`, not `OnDestroy`.
- `GhostField`s still read their last values in `OnDestroy`.

### GhostField&lt;T&gt;

`GhostField<T>` replicates a value from the server to clients.

- **Writing:** the server writes it. Clients that predict the ghost can also write it inside prediction, and the value is rolled back automatically on re-simulation.
- **Writing on the wrong copy:** a write where `CanWriteState` is false (an interpolated copy on a client) fails an assertion in development builds. In release builds the write goes through and the next snapshot overwrites it.
- **Reading:** read `.Value` anywhere on the main thread. It reads straight from the entity's component, so it always shows the current state. It can't be used from jobs.
- **Initial value:** the value set in the field initializer, or saved in the prefab, is written into the entity when the server (or a predicting client) spawns the ghost. A client receiving the ghost gets the server's value instead.
- **Before spawn and after despawn,** `.Value` reads and writes a plain local copy. When the ghost despawns, the last value is copied there, so `OnDestroy` can still read it.
- **Inspector:** at runtime the Inspector shows the live value. Editing it on a server copy writes the new value into the ghost.
- **Delivery:** replication is *eventually consistent*. A client always ends up with the latest value but can skip intermediate ones. Use an RPC for one-off events.
- **Types:** use unmanaged types such as `int`, `float`, `bool`, `Vector3`, `Quaternion`, or blittable structs.
- **Options:** precision and smoothing are set per field with a `FieldConfig`. See [Configuring a field with `FieldConfig`](#configuring-a-field-with-fieldconfig).

```csharp
public GhostField<int> CarrierNetworkId;    // CarryableItem: who is holding me (0 = nobody)
public GhostField<float> ViewPitch;         // FPSControllerGhost: predicted camera pitch
public GhostField<Vector3> PredictedVelocity;
```

To share data with ECS systems, or to use a component you define yourself, use [`GhostComponentRef<T>`](#ghostcomponentreft).

#### Configuring a field with `FieldConfig`

A `GhostField<T>` takes an optional `FieldConfig` as its second constructor argument. It sets how the field is serialized and how interpolated copies smooth it:

```csharp
// Sent with 1 cm precision instead of as three full 32-bit floats. Only used for effects.
public GhostField<Vector3> HitConfirmPoint = new(default, new FieldConfig { Quantization = 100 });

// Blended between snapshots on interpolated copies.
public GhostField<float> NetworkSpeed = new(0f, new FieldConfig { Smoothing = Unity.Netcode.SmoothingAction.Interpolate });
```

`FieldConfig` derives from `GhostFieldAttribute`, so it has the same options:

| Option | Default | Effect |
|---|---|---|
| `Quantization` | `-1`: floats are not quantized | Floats are multiplied by this and rounded to an integer before sending. `100` keeps 0.01 precision, `1000` keeps 0.001. `0` also means full precision. Integers ignore it. |
| `Smoothing` | `Clamp` | How **interpolated** copies show the value between snapshots. See below. Predicted copies and the server never smooth. |
| `MaxSmoothingDistance` | `0`: no limit | If the value changes by more than this between two snapshots, it isn't smoothed. It holds the older snapshot's value instead. For quaternions, use sin(θ/2) for the largest angle θ to smooth. |
| `Composite` | `false` | Only for struct types. `true` uses one change bit for the whole struct, not one per field. |
| `SendData` | `true` | `false` stops the field from being replicated. |
| `SubType` | none | Picks a custom serializer registered with the `GhostFieldSubType` API. |

**Smoothing modes:**

- **`Clamp`** (default) shows the latest snapshot at or before the interpolation tick. The value steps as each snapshot arrives. It can trail the ghost's interpolated position by up to the gap between snapshots.
- **`Interpolate`** blends between the two snapshots around the interpolation tick. If the next snapshot hasn't arrived, it holds the latest value.
- **`InterpolateAndExtrapolate`** blends between snapshots too, but when the next snapshot hasn't arrived it continues the trend of the last two. `ClientTickRate.MaxExtrapolationTimeSimTicks` limits how far.

**How it's read.** The source generator doesn't run your code. It reads the `FieldConfig` from the field's initializer and copies each `Option = value` into a `[GhostField(...)]` attribute on the field's generated component. So:

- Write it inline as `new(initial, new FieldConfig { ... })` in the field declaration. A `FieldConfig` built anywhere else, or used as an attribute, is ignored.
- Values must be constants. Fully qualify enum values (`Unity.Netcode.SmoothingAction.Interpolate`), because the generated file may not have your `using` directives.
- A value saved in the prefab replaces the initial value, as with any serialized field.

**Bandwidth notes:**

- Each `GhostField` is generated as its own component, so a ghost with many fields has many small components.
- An unchanged field costs one change bit per snapshot. A changed field costs its data on top.
- An unquantized `float` that changed is sent as a raw 32 bits. A `Vector3` is sent as three such floats. Quantized floats and integers are delta-compressed, so a small change costs only a few bits.
- So the expensive fields are floats and vectors that change every tick: velocities, timers, animation parameters.
- **Quantize only what nobody predicts with.** The owning client predicts at full precision, then replays from quantized snapshots. A replayed value that differs slightly from the server's can flip a comparison, for example `DashTime < DashDuration`, a tick early or late, which causes a visible correction. Quantize effect-only values (hit points, impulses) freely. Keep fields the prediction reads unquantized, or store them as integers, such as tick counts.
- **Don't replicate what clients can work out.** `FPSAnimatorGhost` gets locomotion speed from the replicated `PredictedVelocity`, and hit reactions look up hit-stop and camera shake from the hit's attack id, instead of syncing them as separate fields.
- **`[GhostField]` on an `IInputComponentData` field also sends that input to every other client,** for predicting remote players. The input reaches the server without it. `FPSInputs` has none, because remote players are interpolated here.

**Smoothing values that reset.** A timer that counts up and then restarts at 0, such as an attack's elapsed time, can't use plain `Interpolate`. Between the last snapshot of one attack and the first of the next, it blends from the end time back to 0, so the old attack plays backwards. `MaxSmoothingDistance` stops that only if every reset is bigger than the largest normal gap between snapshots, and when snapshots arrive in bursts that isn't true.

For animation, `FPSAnimatorGhost` doesn't smooth these fields. It runs its own clock on remote copies instead. The clock restarts when a counter field changes (`AttackSerial`, `DashCount`) and catches up to the replicated value smoothly. See `FPSAnimatorGhost.RemoteClock`.

### GhostComponentRef&lt;T&gt;

`GhostComponentRef<T>` points a `GhostBehaviour` field at an ECS component **you** define (`T : unmanaged, IComponentData`) on the ghost's entity. A `GhostField` generates its component for you instead.

```csharp
public struct FPSInputs : IInputComponentData { ... }

public partial class FPSControllerGhost : GhostBehaviour
{
    public GhostComponentRef<FPSInputs> InputComponent;
}
```

How it differs from a `GhostField`:

| | `GhostField<T>` | `GhostComponentRef<T>` |
|---|---|---|
| Component | Generated, one per field | Your own struct |
| What is replicated | The field, configured with `FieldConfig` | The struct's `[GhostField]` members, configured with `[GhostField(...)]` and `[GhostComponent(...)]` as in N4E. A struct with no `[GhostField]` isn't replicated: each peer has its own value. |
| Shared between behaviours | No | Yes. Every `GhostComponentRef<T>` of the same `T` on one ghost points at the same component. |
| Usable from ECS systems | Not in practice; the generated type is nested in your class | Yes. Query `T` like any component. |
| Reading | `.Value`, read from the chunk | `.Value` returns a **copy**. Change it, then assign it back. |
| Write check | Asserts `CanWriteState` | None. A client write to a replicated member is overwritten by the next snapshot. |
| Initial value | Always written on an authoritative spawn | Written only if it isn't `default(T)` |

Other rules:

- **Input components.** For an `IInputComponentData`, netcode also adds the per-tick input buffer (`InputBufferData<T>`). That's what sends input to the server and keeps it for replays.
- **Before spawn,** `.Value` reads and writes a local copy, as with `GhostField`.
- **Zero-sized (tag) components** can be added but have no value to read.
- **In this project** it holds `FPSInputs` on the player, and `GameSetup` uses one for `FPSGameSetup`.

### Spawning and despawning

**Bridged prefabs (with a `NetworkObject`)** are spawned through NGO, on the server only:

```csharp
var item = Instantiate(ItemPrefab, position, rotation);
item.GetComponent<NetworkObject>().Spawn();
```

If a bridged prefab is spawned without NGO, NGO treats it as unspawned and blocks things like reparenting.

**Plain ghost prefabs** (no `NetworkObject`) spawn when they are instantiated in the server world. `GhostObject.Spawn()` / `Despawn()` are for objects with delayed spawning.

**A client can also spawn a plain ghost prefab** from `PredictionUpdate`, so it appears without waiting for the server. See [Client predicted spawning](#client-predicted-spawning).

### Spawning players

NGO doesn't link its client ids to N4E `NetworkId`s, so it doesn't set the ghost owner. Without an owner there's no owner prediction and no `GhostOwnerIsLocal`. `UnifiedPlayerSpawner` does this instead:

```csharp
var player = Instantiate(PlayerPrefab, position, Quaternion.identity);
// Must happen before the ghost replicates, so prediction is right from the first snapshot.
player.GetComponent<GhostObject>().OwnerNetworkId = new NetworkId { Value = networkId };
player.SpawnAsPlayerObject(clientId);
```

How it finds the `NetworkId`:

- **Remote client:** `NetworkManager.GetTransportIdFromClientId(clientId)`. The unified transport uses the N4E NetworkId as the transport id.
- **Host's own player:** the host has no transport id. Use the `NetworkId` on the world's `LocalConnection` entity instead.
- **Timing:** retry each frame until the id resolves. It isn't always available in `OnClientConnectedCallback`.

### Ownership

There are two kinds of owner, and they're separate:

- **NGO owner:** `NetworkObject.OwnerClientId`. Used by NGO features.
- **Ghost owner:** `GhostObject.OwnerNetworkId`. Used by N4E for prediction and input.

To check "is this my player?" on a client, use the ghost owner:

```csharp
bool IsLocalOwner()
{
    if (!IsClient) return false;
    var em = Ghost.World.EntityManager;
    return em.HasComponent<GhostOwnerIsLocal>(Ghost.Entity)
        && em.IsComponentEnabled<GhostOwnerIsLocal>(Ghost.Entity);
}
```

### Parenting

To parent one ghost under another, for example a carried item:

- **Child prefab:** turn on **Parent Replication** on the `GhostObject`.
- **Parent:** must also be a `GhostObject`.
- **Who parents:** only the server, for interpolated ghosts. Use a plain `transform.SetParent(...)` on the server, and the ghost replicates it. A client may parent a ghost it predicts, and the server's parent wins when the next snapshot arrives.
- **Both must be spawned.** Otherwise the change is reverted with a warning. Parenting under a GameObject without a `GhostObject` is reverted with an error.
- **`worldPositionStays` is detected for you.** Netcode compares the local transform before and after the change, and replicates whichever kind of parenting you used.
- **To react to a parent change,** use `GhostObject.OnParentChanged` rather than `OnTransformParentChanged`. It only fires for changes netcode accepted, including ones that arrive from the server.
- **Bridged child:** turn off NGO's own parent sync. Otherwise NGO and the ghost both replicate the change. On clients, whichever arrives first gets rejected and reverted, which logs *"Invalid parent change detected! Client attempting to parent … but only the server can parent an interpolated ghost."* Turn it off in code, because the prefab setting doesn't save reliably (see [Known issues](#autoobjectparentsync-doesnt-save-on-the-crate-prefab)):

```csharp
public override void Awake()
{
    base.Awake();
    // The ghost's Parent Replication owns parenting; NGO's parent sync would race it.
    if (TryGetComponent<NetworkObject>(out var networkObject))
        networkObject.AutoObjectParentSync = false;
}

// Server only
transform.SetParent(carrier.transform, worldPositionStays: true);   // pick up
transform.SetParent(null, worldPositionStays: true);                // drop
```

Don't use `NetworkObject.TrySetParent` for bridged ghosts. It goes through NGO's parent sync.

### Rigidbodies on ghosts

When a bridged prefab has a `Rigidbody`, NGO adds a **`GhostRigidbody`**. That puts the body into N4E's physics loop, with these effects:

- N4E sets `Physics.simulationMode = Script` and steps physics itself, once per network tick, as soon as any ghost has a `GhostRigidbody`. A scene without netcode then needs `Physics.Simulate` called manually.
- Before each step it calls `rb.Move(transform.position, transform.rotation)` on every `GhostRigidbody`, so a `MoveRotation` / `MovePosition` target can be replaced by the transform's pose (see [Known issues](#rigidbodymoverotation--moveposition-may-be-overridden)).
- The step writes the physics pose back to the transform. Netcode then copies the transform into the ECS `LocalTransform` (see [Transform syncing](#transform-syncing)).
- `GhostRigidbodyData` replicates linear and angular velocity (1 mm/s precision), the kinematic flag, sleep state and wake counter, to clients that predict the ghost. Higher-precision variants can be picked in the ghost's component inspection.
- **Physics settings:** in debug log mode the package warns unless **Enhanced Determinism** is on and the solver is **Projected Gauss Seidel**. Both make replays match the server more closely.

In practice:

- **Moving a body:** set `transform.position` / `transform.rotation` directly, or set `linearVelocity` and let the physics step move it.
- **Teleporting a physics ghost with host smoothing on:** also set its ECS `LocalTransform`. On a host, a ghost with **Single World Host Interpolation Smoothing** gets the entity transform copied back over the GameObject every frame, before the transform is saved, so a teleport made only on the transform is undone.
- **Kinematic children:** a kinematic child parented to a dynamic body can drift a little each tick. `CarryableItem.KeepHoldPose` re-applies the hold pose in `PredictionUpdate` and `LateUpdate` to stop this.
- **Client copies:** keep them kinematic (`CarryableItem` sets `isKinematic` in `Awake`, and the server copy turns physics back on). Snapshots drive client copies, not local physics.

---

## Ghost entity mapping and syncing

Every ghost GameObject is paired with an N4E ghost entity. The entity is what N4E serializes, predicts and rolls back. The GameObject is a view of it, plus the transform and `Rigidbody` that netcode copies in and out. This section describes that pairing, as found in the package source (`GhostEntityMapping`, `PrefabRegistry`, `GhostGameObjectSyncTransformSystem`, `GhostSpawnSystem`).

### The mapping table

- **One global table** maps each GameObject to its world, entity and reference count. It's keyed by the GameObject's id.
- **Prefabs are keyed per world**, because one prefab has a prefab entity in every netcode world. A spawned GameObject has exactly one entity.
- **The table holds a transform index** for each spawned GameObject, so transforms can be copied in batched jobs (`TransformAccessArray`).
- **The entity has a `GhostGameObjectLink`** pointing back to the GameObject and its transform. That's how systems find the GameObject from an entity.
- **To go from a ghost id to a GameObject,** use `Netcode.RPC.ResolveGameObject(world, ghostId, spawnTick)`. It looks the ghost up in the world's `SpawnedGhostEntityMap`.

### Prefab registration

When a netcode world is created, netcode registers every prefab that has a `GhostObject`, unless **Skip Automatic Prefab Registration** is on. Registering builds a prefab entity with:

| Component | When |
|---|---|
| One generated component per `GhostField`, one per `GhostComponentRef` type, plus input buffers | Always, from every `GhostBehaviour` on the prefab and its children |
| `LocalTransform` | Always. Position, rotation and (with **Use Uniform Scale**) scale. |
| `PostTransformMatrix` | Unless **Use Uniform Scale** is on. Per-axis scale, replicated by default for GameObject ghosts. |
| `GhostOwner`, `GhostOwnerIsLocal` | **Has Owner** |
| `AutoCommandTarget` | **Support Auto Command Target** |
| `ParentingData` | **Parent Replication** |
| `SmoothingEnabled` | Always on a client world. On a host only with **Single World Host Interpolation Smoothing**. Marks ghosts whose GameObject transform may be smoothed and must be reset before simulating. |
| `GhostRigidbodyData` | When the prefab has a `GhostRigidbody` |

The prefab then goes through N4E's normal ghost conversion, with the `GhostObject` settings as its config. Its ghost type id comes from the prefab's asset GUID.

### Spawning

- **Server:** `Instantiate` runs `GhostObject.Awake`. That instantiates the prefab entity in the active netcode world and copies the GameObject's transform into it. Then it writes each field's initial value and calls `OnGhostSpawn`.
- **Where a spawn is allowed:** on a server, or on a client inside the prediction loop (a predicted spawn, see [Client predicted spawning](#client-predicted-spawning)). Anywhere else, the package logs *"Invalid world … for spawn"*. With no netcode world at all, it throws.
- **Client:** a new ghost in a snapshot creates the entity first, marked as pending. Later in the same frame, after `GhostUpdateSystem` has written the snapshot values, `GhostGameObjectSpawnSystem` instantiates the GameObject prefab inactive. It links the two, calls `OnGhostSpawn`, then activates the GameObject. So `Awake` already sees replicated values.

### Entity lifetime

- **The entity is reference counted.** The `GhostObject` and every `GhostBehaviour` on it each hold a reference: taken in `Awake` (or `Spawn()`), released in `OnDestroy` (or `Despawn()`). The entity is destroyed when the last reference goes. Not calling `base.Awake()` / `base.OnDestroy()` breaks this count.
- **Destroy the GameObject, never the entity.** Destroying a ghost entity that has a GameObject logs *"Destroying a ghost entity … that has a GameObject attached is not supported"*.
- **Server despawn:** `Destroy(gameObject)`. The entity goes with it, and clients receive the despawn.
- **Client despawn:** `GhostGameObjectDespawnManagedSystem` destroys the client's GameObject. Interpolated ghosts despawn when the interpolation timeline reaches the despawn tick, predicted ghosts at the predicted tick.
- **World shutdown:** every remaining ghost GameObject is destroyed with its world.

### Where data lives

- **`GhostField` values live in the entity's components**, not in the `MonoBehaviour`. `.Value` reads the component's memory directly, through a cached pointer that's refreshed after structural changes. There's no copy step and no dirty flag.
- **Snapshots write those components directly.** On a client, a `GhostField` changes when `GhostUpdateSystem` applies a snapshot, and again whenever prediction rolls it back and replays it.
- **Frame timing:** the package's comments note that `MonoBehaviour.Update` runs before the frame's netcode simulation. Reading in `LateUpdate` shows this frame's values. Reading in `Update` shows the previous frame's.

### Transform syncing

The transform is the one piece of state that exists twice: on the GameObject and in the entity's `LocalTransform` (plus `PostTransformMatrix` for scale). Netcode copies it between them at fixed points:

| When | Direction | Where |
|---|---|---|
| Server, once per frame, after prediction and before snapshots are sent | GameObject → entity | `GhostObjectGameObjectToEntityTransformSystem` |
| Client and host, after snapshots are applied | Entity → GameObject, only on ghosts with `SmoothingEnabled` | `GhostObjectEntityToGameObjectTransformSystem` |
| Client (not host), start of each predicted tick | Entity → GameObject | `PredictedGhostObjectEntityToGameObjectTransformSystem` |
| Client (not host), after `PredictionUpdate` each predicted tick | GameObject → entity | `PredictedGhostObjectGameObjectToEntityTransformSystem` |
| Client and host, in presentation | Smoothed `LocalToWorld` → GameObject | `GhostObjectEntityToGameObjectLocalToWorldSmoothedTransformSystem` |

What this means:

- **On the server, the GameObject transform is the source of truth.** Move it however you like, from any script. It's captured once per frame.
- **On a client, the entity is the source of truth.** The GameObject transform is overwritten every frame, by the snapshot on interpolated ghosts and by the predicted state on predicted ones. Moving a client copy outside `PredictionUpdate` only lasts until the next frame.
- **The rendered transform can differ from the simulated one** while interpolating, switching prediction mode or smoothing a misprediction. On a client, the tick-accurate value is `Ghost.GhostTransform` / `Ghost.Position` / `Ghost.Rotation`. On the server, read the GameObject transform: those properties are only updated from it once per frame.
- **On a host, no per-tick copying happens.** The transform stays on the GameObject, and the host smoothing option is the only thing that writes back over it.
- **Only the root transform is synced.** Children move with their root but aren't replicated themselves.

### Rigidbody syncing

For ghosts with a `GhostRigidbody`, the `Rigidbody` state (velocity, angular velocity, kinematic flag, sleep state and wake counter) is copied into `GhostRigidbodyData` after each simulated tick:

- **On the server,** last in the predicted group.
- **On a predicting client,** right after `PredictionUpdate`.

When a client rolls back, the restored values (and the restored pose, via `rb.Move`) are written back onto the `Rigidbody` at the start of the first replayed tick. On a client, rigidbody ghosts that aren't being simulated in a tick are made kinematic for that tick, so they don't drift while others replay. See [Rigidbodies on ghosts](#rigidbodies-on-ghosts) for how the physics step itself runs.

---

## Prediction and input

Predicted ghosts run the same simulation on the owning client, ahead of the server, and rewind and replay when a snapshot disagrees. In the GameObject layer this happens in `GhostBehaviour` overrides.

### 1. Make the ghost predicted

- Set **Default Ghost Mode** to `OwnerPredicted` and **Supported Ghost Modes** to `All`.
- Set **Single World Host Interpolation Smoothing** to `Disabled`.

The NGO bridge resets these on bridged prefabs, so `BridgeGhostModeGuard` puts them back when entering Play mode and before builds (see [Known issues](#the-bridge-forces-interpolated-mode)).

### 2. Define an input component

```csharp
[Serializable]
public struct FPSInputs : IInputComponentData
{
    public float2 Move;
    public float2 Look;
    public InputEvent Jump;   // one-shot events: use InputEvent, not bool
}
```

Every field is sent to the server as a command. Add `[GhostField]` to a field only to send it to the other clients as well, for predicting remote players with their input. Otherwise it costs bandwidth for nothing.

Reference it from the behaviour with a `GhostComponentRef`:

```csharp
public GhostComponentRef<FPSInputs> InputComponent;
```

`InputEvent` makes sure a press seen in one frame is delivered exactly once, even when several ticks run in that frame or none do.

### 3. Gather input in `PredictionGatherInput`

This runs only on the owning client, once per frame, before the prediction loop. It only runs on ghosts with **Has Owner** and **Support Auto Command Target**, whose `GhostOwnerIsLocal` is enabled, after the ghost has a ghost id. Read the input devices here and nowhere else:

```csharp
public override void PredictionGatherInput(PredictionGatherInputContext context)
{
    var input = InputComponent.Value;
    input.Jump = default;
    input.Move = _moveAction.ReadValue<Vector2>();
    input.Look = _lookAction.ReadValue<Vector2>();
    if (_jumpBuffered) { input.Jump.Set(); _jumpBuffered = false; }
    InputComponent.Value = input;
}
```

Netcode sends the input to the server and stores it per tick, so replays use the input from the tick being replayed.

### 4. Simulate in `PredictionUpdate`

`PredictionUpdate` runs once per simulated tick, on the server and on clients that predict the ghost. On a client it can run several times per frame while replaying.

- **On the server,** every ghost entity has `PredictedGhost`, so `PredictionUpdate` runs for interpolated ghosts too. It runs once per tick, and never replays.
- **On a Single World host,** the host is the server, so the same applies: no rollback, no replays, no partial ticks.
- **On a client,** it only runs for ghosts this client predicts, and only for ticks they're being simulated in. During a replay, a ghost whose own replay starts later is skipped until its start tick.

```csharp
public override void PredictionUpdate(PredictionUpdateContext context)
{
    var input = InputComponent.Value;          // input for *this* tick
    float dt = context.TickedDeltaTime;        // not Time.deltaTime

    // Yaw: set the transform. N4E moves the Rigidbody to it before the next physics step.
    // (FPSControllerGhost uses _rb.MoveRotation, which also worked in testing; see Known issues.)
    transform.rotation *= Quaternion.Euler(0f, input.Look.x * LookSensitivity, 0f);

    // Pitch: a GhostField, so it's rolled back and replayed with everything else.
    ViewPitch.Value = Mathf.Clamp(ViewPitch.Value - input.Look.y * LookSensitivity, -89f, 89f);

    // Movement: keep state in GhostFields, apply it through the Rigidbody.
    var velocity = ComputeVelocity(input, PredictedVelocity.Value, dt);
    PredictedVelocity.Value = velocity;
    _rb.linearVelocity = velocity;
}
```

Rules:

- **Must be deterministic from replicated state and input.** Anything that affects the result must be a `GhostField`, the ghost's transform, or the input. Plain C# fields aren't rolled back, so a value kept in one goes wrong on replay.
- **Use `context.TickedDeltaTime`,** never `Time.deltaTime`.
- **Don't read devices here.** Use `InputComponent.Value`.
- **Visual-only work goes in `Update`/`LateUpdate`,** for example applying `ViewPitch` to the camera. Those run once per rendered frame.
- **To compare with the previous tick's input,** for example to detect a button-down edge, use `TryGetPreviousInputData<T>(out var previous)`.

### 5. Physics interaction (optional)

Override `PredictionPhysicsUpdate` for forces and other logic that has to run immediately before each physics step. See [When to use `PredictionPhysicsUpdate`](#when-to-use-predictionphysicsupdate).

### Order inside one tick

Each tick of `PredictedSimulationSystemGroup` runs these steps in this order. Client-only steps are skipped on a host. For where the tick sits in the frame, and how often it runs, see [GhostBehaviour callback order](#ghostbehaviour-callback-order).

1. **Client only: restore.** On the first replayed tick, the rolled-back `LocalTransform` is copied onto the GameObject transform, and the rolled-back `GhostRigidbodyData` onto the `Rigidbody`.
2. **Physics, once per fixed step** (`PredictedFixedStepSimulationSystemGroup`). By default there's one fixed step per tick. A server tick that batches several ticks takes one step for each of them.
   - `PredictionPhysicsUpdate` runs on every ghost with a `GhostRigidbody`.
   - Then every `GhostRigidbody` is moved to its GameObject transform.
   - Then the physics scene is stepped, and the results are written back to the transforms.
   - Skipped entirely on partial ticks.
3. **`PredictionUpdate`** on every predicted ghost.
4. **Save.** The `Rigidbody` state and (on a client) the GameObject transform are copied back into the entity.

**Physics runs before `PredictionUpdate` in each tick.** So `PredictionUpdate` sees this tick's post-physics pose, and a velocity it sets moves the body in the **next** tick's step. That's the same on the server and on every replay, so prediction stays consistent. Just don't expect a velocity set in `PredictionUpdate` to show up in the same tick's position.

### What gets rolled back

When a client receives a snapshot with new data for a predicted ghost, N4E resets the ghost to the snapshot's tick and replays every tick from there to the present. When there's no new data for a ghost, it restores the ghost from its own backup of the last full tick instead. Either way, these are reset before the replay:

| Rolled back | How |
|---|---|
| Every `GhostField` | Its generated component, from the snapshot or the backup |
| `GhostComponentRef` members marked `[GhostField]` and sent to predicted clients | Same, as normal N4E ghost components |
| Position and rotation | `LocalTransform`, copied to the GameObject at the start of the first replayed tick |
| Per-axis scale | `PostTransformMatrix`, unless **Use Uniform Scale** |
| `Rigidbody` velocity, angular velocity, kinematic flag, sleep state and wake counter | `GhostRigidbodyData`, written back to the `Rigidbody` on the first replayed tick |
| The input for each replayed tick | Not reset, but `InputComponent.Value` holds the input stored for the tick being replayed |

**Not rolled back:**

- Plain C# fields and properties on your behaviours
- Fields marked `SendData = false`
- `GhostComponentRef` structs without `[GhostField]`s
- Any other ECS component
- `Animator`, `ParticleSystem`, audio and other component state
- Child transforms
- Forces and torques added to a `Rigidbody`, and settings such as mass and constraints
- Anything outside the ghost: static variables, other objects, spawned effects

Anything a replay reads must come from the first table. Otherwise the replay starts from whatever the last frame left behind, and it diverges from the server. That's why `FPSControllerGhost` keeps `PredictedVelocity` and its dash timers in `GhostField`s rather than plain fields.

### Partial ticks and repeated side effects

- **Partial ticks.** At a variable frame rate, a remote client ends most frames with a partial tick. That tick runs with a fraction of a tick's `TickedDeltaTime`, and its results are thrown away: the next frame restores the last full tick and runs the whole tick again. Physics never runs on a partial tick. A host never runs them.
- **Every tick can run more than once on a client:** as a partial tick, as the first full prediction, and again in each replay after a snapshot. So **keep side effects out of `PredictionUpdate`:** spawning objects, sounds, particles, analytics, sending RPCs.
- **Better:** let `PredictionUpdate` change replicated state (a counter, a timestamp), and react to that state in `Update` / `LateUpdate`. `CombatControllerGhost` does this with `AttackSerial`.
- **If an effect must fire from prediction,** gate it on `Ghost.World.NetworkTime.IsFirstTimeFullyPredictingTick`. That's true only the first time a tick is predicted in full. Spawning a ghost from prediction needs this gate too: see [Client predicted spawning](#client-predicted-spawning).
- **The same goes for anything that advances over time and isn't rolled back,** such as an `Animator` or a `PlayableGraph`. `FPSAnimatorGhost` used to evaluate its graph in `PredictionUpdate`. On a remote client, the player's own animation then ran nearly three times faster than other copies (6.0 against 2.2 graph seconds per second, measured on 2026-10-11), because every replay advanced it again. Now only the server evaluates it per tick, since its hit detection needs the animated bones of each tick. Clients evaluate it once per frame in `Update`.
- **Drive a `PlayableGraph` yourself with `DirectorUpdateMode.Manual`.** With `GameTime`, Unity also advances a playing graph by itself every frame, on top of your `Evaluate` calls. That ran this project's animations at about twice their speed.

### Client predicted spawning

A client can spawn a ghost itself, inside prediction, so it appears immediately instead of a round trip later. The server spawns the real ghost in the same tick. When the server's copy arrives, netcode matches it to the client's copy, and the client's GameObject becomes that ghost. The fireball special attack works this way (`CombatControllerGhost.FireProjectile`, `Projectile`).

**Requirements**

- **A plain ghost prefab,** with a `GhostObject` and no `NetworkObject`. Bridged prefabs spawn through `NetworkObject.Spawn()`, which only the server can call. A predicted spawn of a bridged prefab hasn't been tried.
- **The prefab must be predicted on the spawning client.** `Fireball_Ghost` uses **Default Ghost Mode** `Predicted` and **Supported Ghost Modes** `All`, so every client predicts it. A predicted spawn of an `OwnerPredicted` prefab hasn't been tested.
- **Spawn from `PredictionUpdate`.** A client may only spawn while its world is in the prediction loop. Anywhere else, the package logs *"Invalid world … for spawn. You can only spawn a ghost on a server or during prediction on a client."*
- **Spawn once per tick.** A client runs each tick several times: as a partial tick, as its first full prediction, and again in each replay (see [Partial ticks and repeated side effects](#partial-ticks-and-repeated-side-effects)). Without a gate, every run spawns another copy. The server runs each tick once, so it needs no gate.

```csharp
// In the shooter's PredictionUpdate, on the server and on the client that predicts the shooter.
void FireProjectile(AttackData attack)
{
    if (!IsServer && !Ghost.World.NetworkTime.IsFirstTimeFullyPredictingTick) return;
    var projectile = Instantiate(attack.ProjectilePrefab, origin, rotation);
    projectile.Launch(this, Combos.GetId(attack), origin, direction); // writes its GhostFields
}
```

**Setting it up after `Instantiate`**

- `Instantiate` runs the `GhostObject`'s `Awake` first, which creates the entity in the client's world and writes the prefab's initial `GhostField` values. The new ghost has `PredictedGhost`, so the code that spawned it can write its `GhostField`s straight after `Instantiate` returns, like the server does.
- **Everything the spawned ghost needs must be in `GhostField`s.** The server's copy and the spawning client's copy were both set up by `Launch`. Every other client receives the ghost from a snapshot and only has its `GhostField` values. A plain C# reference set at spawn, such as `Projectile.m_Shooter`, exists on just those two copies. `Projectile` also stores `ShooterGhostId` and looks the shooter up by it on the other clients.
- **Make the simulation a function of replicated state.** `Projectile` keeps `Origin`, `Direction` and `Elapsed` and works out its position from them each tick. So the predicted copy, the server's copy and the copies other clients predict all follow the same path.
- **The new ghost isn't simulated in the tick that spawned it.** Its `Start` runs after the prediction loop, and its first `PredictionUpdate` comes in the next tick (see [Over a ghost's life](#over-a-ghosts-life)). Place it where it should be at the end of the spawn tick, or advance it once yourself in `Launch`.

**What happens next** (in the package source, with what was seen in testing)

1. The new entity starts with a `PredictedGhostSpawnRequest` and no ghost id. `PredictedGhostSpawnSystem` fills its snapshot buffer from the ghost's current values and its spawn tick, then removes the request the next frame.
2. Until the server's copy arrives, the ghost is predicted like any other. **Rollback Predicted Spawned Ghost State** (off by default) decides whether it's rolled back and replayed from its spawn tick in the meantime.
3. **Matching:** when a new ghost arrives in a snapshot, `DefaultGhostSpawnClassificationSystem` pairs it with a predicted spawn of the same ghost type whose spawn tick is less than `ClientTickRate.DefaultClassificationAllowableTickPeriod` ticks from the server's spawn tick. This project uses the default of 5. Projectiles are matched by this project's own system first; see [Matching projectiles by shooter](#matching-projectiles-by-shooter). The client keeps its GameObject and entity, takes the server's ghost id, and the snapshot's values take over from then on.
4. **No match:** a predicted spawn the server never confirmed, for example because the shooter was stunned on the server, is destroyed once the interpolation tick passes its spawn tick, plus `ClientTickRate.NumAdditionalClientPredictedGhostLifetimeTicks` (30 here, default 0). Its GameObject is destroyed with it. A server that never fired hasn't been tested. A server that fired too late to match has been: see [Known issues](#predicted-spawns-fail-while-both-editors-stall).

The default matching only looks at ghost type and tick. Another player's fireball is still a fireball, so if it was spawned within the window and arrives first, it can be paired with your predicted copy. Your copy then turns into their fireball, and yours shows up later as a separate object. A wider window makes that more likely. This project prevents it for projectiles with a custom classification system; see [Matching projectiles by shooter](#matching-projectiles-by-shooter).

**Effects and despawning**

- Only the server should apply results and despawn. `Projectile` applies the explosion's damage and destroys itself on the server only, `DespawnDelay` seconds after exploding, so clients receive the exploded state first.
- Predicted state can be undone by a correction. `Projectile` plays its explosion in `Update` when `Exploded` becomes true, and shows the ball again if a rollback sets it back to false.
- Hide a server-only copy, as with other ghosts: `Projectile.Start` puts it on the `ServerWorld` layer and turns off its renderers.

**Tested** on 2026-10-13, with a host and a Multiplayer Play Mode client, both in the Editor:

- The client's fireball appeared on the first frame with the right `Origin` and `Direction`. About eight frames later it was matched to the server's ghost (ghost id 0, then 11). Its path didn't jump at the switch.
- A fireball fired from the client hit a dummy, and the server applied the damage.
- On a Single World host, the host is the server. `Instantiate` in `PredictionUpdate` is a normal server spawn, and nothing is predicted.
- **One unexplained result:** in the first client test, the predicted copy's `GhostField`s read zero on its first frame. It exploded at the world origin until the server's copy corrected it. This happened while the host was logging large *"serverTick prediction error"* errors, and it didn't happen in the next three runs. See [Known issues](#predicted-spawns-fail-while-both-editors-stall).
- **Callback logging** (later on 2026-10-13): the client spawned its copy at tick 1406, the same tick the server fired. The spawn came only from the first full prediction of that tick. Replays and partial ticks of the same tick didn't spawn copies. See [Over a ghost's life](#over-a-ghosts-life).
- **Two fireballs weren't predicted.** Both were fired in the same Play mode session. The client made no copy and showed the server's fireball when it arrived, about 20 ticks late. A trace of one showed the client's attack vanish for four frames and come back a little over 0.15 s in, which means it mispredicted the attack. The next four fireballs, in a new Play mode session, were all predicted. See [Known issues](#predicted-spawns-fail-while-both-editors-stall).
- **Six fireballs weren't matched** (2026-10-14). The client spawned its copy, but the server fired 3 to 5 ticks later. Its ghost arrived after the predicted copy's timeout, so the client showed the server's fireball instead. After the matching settings were raised to 10 and 30 ticks, three of the next four matched. See [Known issues](#predicted-spawns-fail-while-both-editors-stall).

#### Matching projectiles by shooter

`Assets/_Scripts/Combat/ProjectileSpawnClassificationSystem.cs` pairs a projectile from the server with a predicted copy only when they have the same ghost type and the same `LaunchShooterGhostId`, and their spawn ticks are less than `DefaultClassificationAllowableTickPeriod` apart. If more than one copy qualifies, it takes the one with the closest tick. A player's fireballs are a Special cooldown (1.5 s, 90 ticks) apart, so at most one of them can be in the window.

- **Where it runs:** in `GhostSpawnClassificationSystemGroup`, after `GhostSpawnClassificationSystem` (which decides predicted or interpolated) and before `DefaultGhostSpawnClassificationSystem` (which is `OrderLast`). Client worlds only. It turns itself off on the single-world host, which has nothing to match.
- **It claims every projectile spawn,** matched or not, by setting `HasClassifiedPredictedSpawn`. So the default system never sees a projectile and can't pair it with the wrong copy. Other ghost types still go to the default system.
- **Reading a `GhostField` from the spawn snapshot:** each `GhostField` is stored in its own generated component, named `<Class>_<Field>_Gen` and nested in the `GhostBehaviour`. Here that's `Projectile.Projectile_LaunchShooterGhostId_Gen`, with the value in `m_Value`. `SnapshotDataBufferComponentLookup.TryGetComponentDataFromSpawnBuffer` reads the server's value from the spawn queue, and a `ComponentLookup` of the same type reads the predicted copy's value from its entity.
- **It runs on the main thread.** An `IJobEntity` that uses a generated `_Gen` type fails to compile with *SGJE0024 "Failure to generate the schedule invocation"*, because one source generator can't see another's output. Creating the snapshot lookup forces a sync anyway, and only a few spawns arrive per frame.
- **The namespaces:** `SnapshotDataLookupHelper` is in `Unity.Netcode.LowLevel` and `NetworkTimeSystem` in `Unity.Netcode.NetcodeTime`. With either `using` missing, Unity offered to run the API updater on the file. Accepting it rewrote the file and wrote a namespace that doesn't exist. Add the `using`s and click **No Thanks** instead.
- **It compares the shooter at launch, not the current shooter.** Reflecting a projectile sets `ShooterGhostId` to the parrier, and either side can bounce it before the server's first snapshot reaches the client. `Launch` sets `LaunchShooterGhostId` to the same player, and nothing changes it afterwards, so both sides always agree on it. It costs one `int` that doesn't change after spawn, so it's cheap to send. Comparing `ShooterGhostId` instead left parried copies unmatched; see the tests below.

**Tested** on 2026-10-15 with the Multiplayer Play Mode client. The host and the client fired a fireball at the same moment:
- The host's fireball had server spawn tick 4179. The client's copy had tick 4186, and its server ghost had 4187.
- The host's fireball reached the client first, while the client's copy was still unmatched. They were 7 ticks apart, inside the window of 10 used at the time, so the default system would have paired them. The new system kept the host's fireball as a separate object, with shooter 4 (the host).
- Four frames later the client's own ghost arrived and was paired with the copy: same object, ghost id 0 then 12, shooter 10 (the client) throughout. Their spawn ticks were 1 tick apart.

**Parried before matching**, tested on 2026-10-15 with the window at 5. The client fired at a dummy that already had a parry window open, so the client's copy bounced before the server's ghost arrived. First, while the system compared `ShooterGhostId`:
- **From 2.5 m it matched.** The copy bounced one frame after it spawned. The server bounced its fireball before sending it, so its first snapshot also had the dummy as shooter. They were paired four frames later, with the same spawn tick.
- **From 4 m it didn't.** The copy bounced five frames after it spawned. Three frames later the server's ghost arrived, still with the client as shooter, because the server had sent it before its own bounce. The spawn ticks were the same, but the shooters differed, so it came in as a second fireball. One frame later its prediction bounced it too, and from then on the two overlapped exactly. Both exploded, so the client played two explosions. The unmatched copy was removed 16 frames later. The server applied the parry and the hit on the client once.

Then with `LaunchShooterGhostId`, twice from 4 m. Each time the copy bounced (shooter 10, then 8, the dummy) four frames before the server's ghost arrived. It was matched anyway: one object throughout, ghost id 0 then 11 or 12, launch shooter 10, same spawn tick. It exploded once, and the server applied the parry and the hit once. Two fireballs fired at the same moment were still kept apart by shooter.

Then twice from 2.5 m. The copy bounced one or two frames after it spawned and hit the client a frame or two later. The server's ghost arrived three to four frames after the explosion and was matched: one object, ghost id 0 then 11, same spawn tick, one explosion. The server applied the parry and the hit once.

In an earlier Play mode session the same runs never made a predicted copy, even without a parry: the client showed only the server's fireball, about 8 frames after its spawn tick. A new Play mode session fixed it. This looks like [Predicted spawns fail while both Editors stall](#predicted-spawns-fail-while-both-editors-stall), not the classification, which only runs once a copy exists.

### When to use `PredictionPhysicsUpdate`

`PredictionPhysicsUpdate(PredictionPhysicsUpdateContext context)` is the predicted counterpart of `FixedUpdate`.

**When it runs:**

- Only on ghosts that have a **`GhostRigidbody`** and are being predicted (always on the server). It isn't called on ghosts without one.
- Inside the predicted fixed-step group, right before the physics step, once per fixed step. That's once per tick unless `ClientServerTickRate.PredictedFixedStepSimulationTickRatio` is above 1, in which case it runs several times per tick, each with `context.FixedDeltaTime`.
- Never on partial ticks.

**Use it for:**

- **Forces and impulses** (`AddForce`, `AddTorque`, explosions). Forces aren't part of the rolled-back state, so they must be applied right before the step that consumes them, in every replay. Added in `PredictionUpdate`, they'd land in the next tick's step instead, and with a tick ratio above 1 they'd only reach the first sub-step.
- **Anything that must run once per physics sub-step,** such as buoyancy, drag or suspension springs.
- **Setting velocity or the pose for this tick's step,** when it must take effect before this tick's physics instead of the next.

**Use `PredictionUpdate` instead for** gameplay state, input handling and character movement that keeps its own velocity, as `FPSControllerGhost` does. It runs on every predicted ghost, with or without a `Rigidbody`. This project doesn't use `PredictionPhysicsUpdate` yet.

### Interpolated ghosts

Interpolated ghosts aren't predicted. Clients show them slightly in the past, smoothed between snapshots. Their `PredictionUpdate` still runs **on the server**, which makes it a good place for server logic that has to run before the physics step (see `CarryableItem.KeepHoldPose`).

---

## RPCs

There are two RPC systems, N4E's and NGO's, and N4E's can be written three ways. All of them work on bridged objects.

| | N4E custom messages (`ICustomMessage` / `[CustomMessage]`) | N4E `[RPC]` methods | NGO RPCs (`[Rpc]`) |
|---|---|---|---|
| Defined on | A struct | A static method or a `GhostBehaviour` method | A `NetworkBehaviour` method |
| Sent with | `Netcode.RPC.Invoke` | Calling the method | Calling the method |
| Received with | `Netcode.RPC.Query`, polled each frame, or a `Handle()` method on the struct | The method body runs on the receiver | The method runs on the target |
| Recipients | The world or connections you pass | Every client (or the server). No targeting | `SendTo` / `RpcTarget` |
| Sender identity | The `Connection` you queried (none in `Handle()`) | None | `RpcParams.Receive.SenderClientId` |
| Good for | Requests the server must check against the sender | Broadcast notifications and events | NGO-side features, notifications |
| Used in this project by | `InteractRequest`, `GoInGameRequest` | `RpcAttributeTest` (test only) | `BridgeTestNGO.PingRpc` |

### N4E custom messages

**1. Declare the message.** It has to be an unmanaged struct with at least one field:

```csharp
public struct InteractRequest : ICustomMessage
{
    public byte Action;          // enums are sent as a byte
    public int ItemGhostId;      // refer to ghosts by GhostId + SpawnTick, never by object reference
    public uint ItemSpawnTick;   // NetworkTick.SerializedData
}
```

**2. Send it.** Pass the world to send from and the direction:

```csharp
var request = new InteractRequest { Action = (byte)InteractAction.PickUp,
                                    ItemGhostId = item.Ghost.GhostId,
                                    ItemSpawnTick = item.Ghost.SpawnTick.SerializedData };
Netcode.RPC.Invoke(request, Ghost.World, SendDirection.ClientToServer);
```

Other overloads send to a specific `Connection` or a list of connections, for server-to-client messages to particular clients.

**3. Receive it on the server, one connection at a time.** This tells you who sent each message without trusting anything inside it:

```csharp
[WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
[UpdateInGroup(typeof(SimulationSystemGroup))]
public partial class InteractionServerSystem : SystemBase
{
    readonly List<InteractRequest> m_Requests = new();
    readonly List<Connection> m_Connections = new();

    protected override void OnUpdate()
    {
        if (World is not NetcodeWorld world || !world.IsServer()) return;

        m_Connections.Clear();
        if (world.AllConnections != null) m_Connections.AddRange(world.AllConnections);
        if (world.IsHost()) m_Connections.Add(world.LocalConnection);   // the host's own player

        foreach (var connection in m_Connections)
        {
            m_Requests.Clear();
            Netcode.RPC.Query(connection, m_Requests);   // consumes this connection's messages
            var player = PlayerInteractor.FindServerPlayer(world, connection.NetworkId);
            foreach (var request in m_Requests) player?.ServerHandleRequest(request);
        }
    }
}
```

Rules:

- **Include the host.** On a Single World host, `world.AllConnections` only lists remote clients. Messages the host's own player sends arrive on `world.LocalConnection`, so query that too.
- **Querying consumes messages.** Query each message type in exactly one place.
- **Validate on the server.** `PlayerInteractor.ServerHandleRequest` checks range, item state and that the sender owns the player. Treat every field as untrusted.
- **Resolve ghosts on the server by `GhostId` + `SpawnTick`,** as in `CarryableItem.FindServerItem`, or with `Netcode.RPC.ResolveGameObject`.

### `Netcode.RPC` reference

Every overload on `Netcode.RPC` (`RPCHandler`). Messages must be `unmanaged` structs implementing `ICustomMessage`, or `[CustomMessage]` structs. `ICustomMessage` is N4E's `IRpcCommand`, so messages have the same limits as N4E RPCs.

| Call | Sends to / reads from |
|---|---|
| `Invoke(msg, world, SendDirection.ClientToServer)` | The world's `LocalConnection`, the server. |
| `Invoke(msg, world, SendDirection.ServerToClient)` | Every connection in `world.AllConnections`, **plus `LocalConnection` on a host**, so the host's own client side receives it too. |
| `Invoke(msg, List<NetcodeWorld>, direction)` | The same, for each world in the list. |
| `Invoke(msg, Connection)` / `Invoke(msg, List<Connection>)` | Exactly those connections. Use this to message one client. |
| `Query<T>(Connection)` / `Query<T>(Connection, List<T>)` | Messages of type `T` received from that connection. The only way to know the sender. |
| `Query<T>(List<Connection>)` / `Query<T>(List<Connection>, List<T>)` | The same, for several connections. |
| `Query<T>(NetcodeWorld)` / `Query<T>(NetcodeWorld, List<T>)` | Every connection of that world, including `LocalConnection`. |
| `Query<T>(List<NetcodeWorld>)` / `Query<T>(List<NetcodeWorld>, List<T>)` | Every connection of each world. |
| `Query<T>()` / `Query<T>(List<T>)` | Every server and client world. |
| `ResolveGameObject(world, ghostId, spawnTick)` | The GameObject for a ghost in that world, or `null`. `spawnTick` is `NetworkTick.SerializedData`. |
| `RegisterAutoInvokeType(id, handler, world)` | Used by generated code for `Handle()` and `[RPC]` methods. You don't call it yourself. |

Every `Query` overload **removes** the messages it returns. The overloads that take a list append to it, so clear the list before reusing it.

### Code-generated RPCs: `[CustomMessage]` and `[RPC]`

N4E's source generator can write the message plumbing for you. Everything below was tested in this project (host plus one Relay client) with `Assets/_Scripts/RpcAttributeTest/`; see [Test results](#test-results).

**Options.** Both attributes take one argument, a `SendDirection`: `ClientToServer`, `ServerToClient` or `Undefined` (the default). There are no other settings: no targeting, no reliability or ordering options. All N4E RPCs are reliable.

#### `[CustomMessage]` structs

`[CustomMessage]` turns a `partial` struct into a custom message, with no need to implement `ICustomMessage` yourself. Send and query it exactly like a hand-written one:

```csharp
[CustomMessage(SendDirection.ClientToServer)]
public partial struct RpcTestPolledMessage
{
    public int Value;
}

Netcode.RPC.Invoke(new RpcTestPolledMessage { Value = 102 }, Ghost.World, SendDirection.ClientToServer);
var received = Netcode.RPC.Query<RpcTestPolledMessage>(connection);
```

Give the struct a private `Handle()` method, and it runs automatically on the receiver instead of waiting to be queried:

```csharp
[CustomMessage(SendDirection.ClientToServer)]
public partial struct RpcTestHandledMessage
{
    public int Value;

    void Handle() => Debug.Log($"received {Value}");   // runs on the receiver
}
```

- **Handled messages never reach `Query`.** In the test, `Query<RpcTestHandledMessage>()` returned 0 every time.
- **`Handle()` doesn't know who sent the message.** To act per sender, leave out `Handle()` and query each connection, as `InteractionServerSystem` does.
- **The `SendDirection` on the attribute isn't used.** You still pass a direction to `Invoke`.
- **Declare the struct at the top level** of a namespace, not nested in a class. The package tests note that nesting doesn't work yet.

#### `[RPC]` methods

Put `[RPC(direction)]` on a method in a `partial` class. Calling the method sends it, and the method body runs on the receiving side, not on the caller:

```csharp
public partial class RpcTestStaticMethods
{
    [RPC(SendDirection.ClientToServer)]
    public static void ClientToServer(int value, float number, Vector3 vector, FixedString64Bytes text)
    {
        // Runs on the server.
    }

    [RPC(SendDirection.ServerToClient)]
    public static void ServerToClient(int value)
    {
        // Runs on every client, including the host.
    }
}

public partial class RpcTestGhost : GhostBehaviour
{
    [RPC(SendDirection.ServerToClient)]
    public void ServerToClient(int value)
    {
        // Runs on this same ghost on every client.
    }
}

// Caller
RpcTestStaticMethods.ClientToServer(1, 1.5f, new Vector3(1, 2, 3), "hello");
cube.ServerToClient(5);
```

**How it works:**
- The generator writes a message struct holding the arguments, plus a `<Method>_Invoker` method that sends it.
- An IL post-processor swaps the bodies of your method and its invoker.
- Your call site therefore sends the message, and your original body runs when the message arrives.
- For a `GhostBehaviour` method, the message also carries the ghost's `GhostId` and spawn tick. The receiver runs the method on the matching instance of the same ghost.

**Rules and limits:**
- **Always give a direction.** With none (`SendDirection.Undefined`), the generated code logs an error.
- **Calling from the wrong side logs an error and sends nothing.** A client calling a server-to-client method gets *"Can't Invoke a ServerToClient RPC on a Client GhostBehaviour."* (or *"… static ServerToClient RPC on a Client instance."*).
- **You can't choose recipients.**
  - Client-to-server goes from every client world.
  - Server-to-client goes to every connected client, **including the host's own client side**: on a Single World host, the method also runs on the host.
- **The receiver doesn't know who sent it.** Don't use `[RPC]` methods for requests the server has to validate against the sender (picking up items, spending resources). Use a custom message queried per connection.
- **Arguments must be unmanaged.** `int`, `float`, `Vector3` and `FixedString64Bytes` were tested. Use fixed strings, not `string`.
- **Calling one doesn't run the body locally.** Logic that should also happen on the sender has to be called separately.
- **The class must be `partial`.** A `GhostBehaviour` with `[RPC]` methods needs no `GhostField`s.

#### Test results

`RpcAttributeTest.cs` and `RpcTestGhost.cs` (on `BridgeTest_Cube`) record every message they receive in `RpcTestLog`. Tested on 2026-10-06 with a Relay host and one Multiplayer Play Mode client:

| Test | Sent from | Result |
|---|---|---|
| `[CustomMessage]`, queried | Client | Arrived on the host's server side; `Query` returned it once |
| `[CustomMessage]` with `Handle()` | Client | `Handle()` ran on the host; `Query` returned nothing |
| Static `[RPC]` client-to-server, 4 argument types | Client | Ran on the host with every value intact |
| `GhostBehaviour` `[RPC]` client-to-server | Client | Ran on the host's instance of the same ghost (ghost 8) |
| Static and `GhostBehaviour` `[RPC]` server-to-client | Host | Ran on the client **and on the host** |
| All client-to-server types | Host's own client side | Arrived on the host's server side; the remote client got nothing |
| Server-to-client `[RPC]` called on a client | Client | Error logged on the client; nothing sent |
| Any message, on the sender itself | — | Never ran locally |

All messages sent in the same frame arrived together in one frame, though not necessarily in the order they were sent.

### NGO RPCs

These work as in standard NGO, on any `NetworkBehaviour` on a bridged object:

```csharp
public class BridgeTestNGO : NetworkBehaviour
{
    public override void OnNetworkSpawn()
    {
        if (IsClient) PingRpc(Time.frameCount);
    }

    [Rpc(SendTo.Server)]
    void PingRpc(int frame, RpcParams rpcParams = default)
    {
        Debug.Log($"ping from client {rpcParams.Receive.SenderClientId}");
    }
}
```

`SenderClientId` is an **NGO client id**, not an N4E `NetworkId`. To map it to a ghost owner, use `NetworkManager.GetTransportIdFromClientId` (see [Spawning players](#spawning-players)).

---

## Sessions and Relay

Multiplayer Services' built-in network handlers don't support unified mode:

- The NGO handler configures `UnityTransport`, which unified mode replaces.
- The Entities handler expects separate client and server worlds.

`UnifiedNetworkHandler` (`Assets/_Scripts/Unified/Sessions/`) is a custom `INetworkHandler` that fills the gap. When a session starts, it:

1. Installs `UnifiedDriverConstructor`, which builds the world's UDP driver with the session's Relay allocation, through `NetworkStreamReceiveSystem.DriverConstructor`.
2. Sets NGO's internal `UnifiedBootstrap.Port` to 0 (by reflection), so the world doesn't auto-listen or auto-connect on port 7979.
3. Calls `StartHost` / `StartServer` / `StartClient`.
4. Listens on the Relay or direct endpoint, or connects to it, and waits for `IsConnectedClient`.

When the session ends, it shuts NGO down and restores both overrides.

To use it:

- **Blocks session UI** (Quick Join, Create, Join by code, Browser): nothing to do. `UnifiedNetworkHandler` registers itself through `SessionSettings.NetworkHandlerOverride` at startup.
- **Your own session code:**

  ```csharp
  // Host
  var options = new SessionOptions { MaxPlayers = 4 }
      .WithRelayNetwork()
      .WithNetworkHandler(UnifiedNetworkHandler.Instance);
  var session = await MultiplayerService.Instance.CreateSessionAsync(options);

  // Client
  var joinOptions = new JoinSessionOptions().WithNetworkHandler(UnifiedNetworkHandler.Instance);
  await MultiplayerService.Instance.JoinSessionByCodeAsync(code, joinOptions);
  ```

  `MultiplayerMenuController` has working examples of create, join by code and Quick Join (`MatchmakeSessionAsync`).

To leave, call `session.LeaveAsync()`. `LocalSessionButtons` does this, and falls back to `NetworkManager.Shutdown()` when there's no session.

**Tested:** host and client over Relay through Quick Join, using a Multiplayer Play Mode virtual player as the client. Each Play Mode player signs in with its own player ID automatically.

---

## Known issues and workarounds

### The bridge forces Interpolated mode

`NetworkObjectBridge.OnValidate` runs whenever the prefab is validated, including on save, so the values can't be fixed on disk. It does three things:

- Sets `SupportedGhostModes = Interpolated`, which breaks `OwnerPredicted` prefabs.
- On prefabs with a Rigidbody, sets `SingleWorldHostInterpolationSmoothing = Interpolate`.
- On prefabs with a Rigidbody, adds `GhostRigidbody`.

**Workaround:** `Assets/_Scripts/Unified/Editor/BridgeGhostModeGuard.cs`. When entering Play mode and before builds, for every bridged prefab whose default mode isn't `Interpolated`, it sets **Supported Ghost Modes** to `All` and turns off host smoothing. In Edit mode the Inspector still shows `Interpolated`; that's expected.

### Host smoothing overwrites predicted transforms

On a Single World host, `SingleWorldHostInterpolationSmoothing = Interpolate` writes the interpolated pose over the GameObject transform every frame. That throws away rotation set in `PredictionUpdate`, so mouse-look stops working on the host. Keep it `Disabled` on predicted prefabs. N4E 7.1 makes `Disabled` the default, but the bridge overrides that for Rigidbody prefabs, so the guard above still applies.

### Rigidbody.MoveRotation / MovePosition may be overridden

N4E moves every `GhostRigidbody` to its transform before each physics step (see [Rigidbodies on ghosts](#rigidbodies-on-ghosts)), so a `MoveRotation` / `MovePosition` target can be replaced by the transform's pose. Setting the transform is the safe choice.

In practice, `FPSControllerGhost` turns its dynamic body with `Rigidbody.MoveRotation`, and that works. In a host test on 2026-10-11, a dummy turned 148° → 239° toward its target, with the transform, Rigidbody and entity agreeing. An earlier yaw failure blamed on `MoveRotation` was really [host smoothing](#host-smoothing-overwrites-predicted-transforms). `MovePosition` on a kinematic body, and either call on a remote predicting client, haven't been tested.

### Host connection isn't marked in game

NGO only marks connections that have `NetworkStreamConnection` as in game. A Single World host's own connection only has `LocalConnection`, so the host's ghosts never get ghost ids. `HostLocalConnectionInGameSystem` adds `NetworkStreamInGame` to it.

### AutoObjectParentSync doesn't save on the crate prefab

Setting it to false on `Crate_Ghost` and saving reverts it to true on import. `CarryableItem.Awake` sets it at runtime instead.

### "Invalid world for spawn" / "Sanity check failed" in the Editor

These come from the AI Assistant package's preview renderer instantiating ghost prefabs outside a netcode world. They're harmless.

### The Multiplayer Play Mode virtual player misses new scripts

After you add a script, the virtual player may not compile it until it's restarted. If it imports a prefab before the script compiles, its copy of the prefab shows that component as a missing script. Restart the virtual player from the Multiplayer Play Mode window after adding scripts. Don't force a reimport on the virtual player: `AssetDatabase.ImportAsset` there crashed Unity 6000.7.0b1 every time it was tried.

Also, a `GhostBehaviour` that goes on a prefab must live in a file named after its class, like any MonoBehaviour. Otherwise Unity saves it to the prefab as a missing script.

The virtual player also doesn't always pick up a new script build or saved prefab changes from the main Editor. Calling `UnityEditor.EditorUtility.RequestScriptReload()` on it reloads both safely, without a restart.

### Prefab variants share the base prefab's NetworkObject hash

A prefab variant of a bridged prefab (for example `FPS_Dummy`, a variant of `FPS_Player`) gets its own `NetworkObject` `GlobalObjectIdHash` in the main Editor, but only in memory. Unless the variant file stores the hash as an override, the Multiplayer Play Mode virtual player uses the base prefab's hash. It then logs *"duplicate GlobalObjectIdHash"*, and the client never connects.

**Fix:** on the variant asset, set the hash to another value and back through a `SerializedObject`, mark the asset dirty and save it, so the file stores the override. `FPS_Dummy` is fixed this way. The ghost side doesn't have this problem, because the ghost type comes from each prefab's own GUID.

### "Large serverTick prediction error"

Seen when two Editors (for example, Multiplayer Play Mode) run on one machine and one stalls. Turn on **Run In Background** in Player settings.

### Predicted spawns fail while both Editors stall

In the first remote-client fireball test (2026-10-13), the client's predicted `Projectile` read zero for `Origin` and `Direction` on its first frame, although `Launch` had set them. It exploded at the world origin, then the server's copy corrected it a few frames later. Damage wasn't affected, because only the server applies it. The host was logging large *"serverTick prediction error"* errors at the time, so a spawn during a big tick correction is the likely trigger, but that isn't confirmed. It didn't happen in the next three runs.

In a later session, two fireballs fired from the client weren't predicted at all. The client showed the server's fireball when it arrived, about 20 ticks late, which is the normal fallback. A trace showed the shooter's attack disappear from the client's prediction for four frames and then come back, already a little over 0.15 s in. So the client's prediction of the attack was wrong when it first predicted the firing tick, and the gate only spawns on that first prediction. The next four fireballs, in a fresh Play mode session, were all predicted.

On 2026-10-14, during the fireball parry tests, none of six fireballs fired from the client was matched. The client spawned its copy, but the server's ghost arrived as a separate object, and netcode destroyed the predicted copy 6 to 14 frames after its spawn. The logged spawn ticks show why:
- **The server fired later.** It spawned its fireball 3 to 5 ticks after the client spawned its copy, for example 17706 against 17703, and 18520 against 18515. The default matching needs the spawn ticks to be less than `DefaultClassificationAllowableTickPeriod` (5) apart, so a 5-tick gap can't match. The likely cause is the client's input reaching the server late.
- **The server's ghost arrived after the timeout.** Even with a 3-tick gap, the server's ghost reached the client about 15 frames after the spawn, after the predicted copy had already been destroyed.

In one of these runs, the server's ghost also read zero on its first frame, flashed an explosion at the world origin, and was then corrected. That's the same symptom as the first problem, on a received ghost rather than a predicted one.

Every result was corrected by the server's state: the fireball's path, the parry, and the damage. Only the shooter's immediate feedback was lost: the fireball appeared late and sometimes flickered.

All of these problems happened while both Editors were stalling (*"Large serverTick prediction error"* on both), and none has been reproduced on demand. The four predicted spawns tested on 2026-10-13, in a quieter session, all matched on the same tick.

- **To diagnose** a predicted spawn that flashes at the world origin, doesn't appear or isn't matched, log its `GhostField`s every frame from the spawn on. Also log `GhostObject.SpawnTick` and `GhostId` on both sides, the shooter's attack state, and `NetworkTime` for a tick rollback.
- **Raise the matching settings.** `ClientTickRate.DefaultClassificationAllowableTickPeriod` lets spawn ticks further apart match. `NumAdditionalClientPredictedGhostLifetimeTicks` keeps the predicted copy alive longer while the server's ghost arrives.

**Raising them helped.** On 2026-10-14 they were raised to 10 and 30 ticks, and four more client fireballs were fired under the same stalls:
- **Three matched,** with spawn-tick gaps of 0, 1 and 4 ticks. Each kept the client's copy and took the server's ghost id. All three gaps were within the old window of 5, so the longer lifetime most likely made the difference. The stalls vary from run to run, so four shots don't prove it. The server's ghost arrived 7 to 12 frames after the spawn.
- **One didn't.** The server fired 26 ticks after the client, beyond any reasonable window. The predicted copy now lived long enough to explode on the dummy, then disappeared after about 30 frames. The server's fireball then appeared and flew again. That is the cost of the longer lifetime: a predicted spawn that never matches stays on screen for up to 0.5 s longer.

The lifetime of 30 is kept. Lower it if unmatched copies linger too long over a real network. The window went back to 5 on 2026-10-15. A wider window isn't needed to stop other players' fireballs being paired with yours, because [Matching projectiles by shooter](#matching-projectiles-by-shooter) prevents that, and all three matched gaps above were under 5.

---

## Project file reference

| File | Role |
|---|---|
| `Assets/NetcodeConfig.asset` | Single World host mode, bootstrap settings. |
| `Assets/DefaultNetworkPrefabs.asset` | NGO prefab list with the bridged prefabs. |
| `Assets/_Scripts/NetcodeBootstrap.cs` | Custom bootstrap, disabled with `[DisableBootstrapOverrides]`. |
| `Assets/_Scripts/Unified/UnifiedPlayerSpawner.cs` | Spawns players and sets the ghost owner. |
| `Assets/_Scripts/Unified/HostLocalConnectionInGameSystem.cs` | Marks the host's connection in game. |
| `Assets/_Scripts/Unified/LocalSessionButtons.cs` | Hides the menu while connected; Leave button. |
| `Assets/_Scripts/Unified/Editor/BridgeGhostModeGuard.cs` | Restores ghost modes and smoothing that the bridge overrides. |
| `Assets/_Scripts/Unified/Sessions/UnifiedNetworkHandler.cs` | Multiplayer Services handler for unified mode. |
| `Assets/_Scripts/Unified/Sessions/UnifiedDriverConstructor.cs` | Builds the UDP driver, with Relay when needed. |
| `Assets/_Scripts/Ghost/GhostController/FPSControllerGhost.cs` | Predicted player movement and look. |
| `Assets/_Scripts/Ghost/GhostController/FPSInputs.cs` | Player input component. |
| `Assets/_Scripts/Combat/Projectile.cs` | Predicted projectile (the fireball), spawned by a client predicted spawn. |
| `Assets/_Scripts/Combat/ProjectileSpawnClassificationSystem.cs` | Matches the server's projectiles to predicted copies by shooter and spawn tick. |
| `Assets/_Prefabs/Combat/Fireball_Ghost.prefab` | Plain ghost prefab, `Predicted` on every client. |
| `Assets/_Scripts/Interaction/PlayerInteractor.cs` | Client aiming, sends `InteractRequest`; server validation. |
| `Assets/_Scripts/Interaction/InteractRequest.cs` | Interaction custom message. |
| `Assets/_Scripts/Interaction/InteractionServerSystem.cs` | Server-side per-connection RPC handling. |
| `Assets/_Scripts/Interaction/CarryableItem.cs` | Server-authoritative carry, with ghost parenting. |
| `Assets/_Scripts/Interaction/CarryableItemSpawner.cs` | Spawns crates at runtime. |
| `Assets/_Scripts/BridgeTest/` | Minimal bridged prefab with both a `GhostField` and NGO `NetworkVariable`/`[Rpc]`. |
| `Assets/_Scripts/RpcAttributeTest/` | Test of `[CustomMessage]` and `[RPC]`; `RpcTestGhost` is on `BridgeTest_Cube`. |
