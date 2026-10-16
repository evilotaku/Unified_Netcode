# Combat System

How the project's networked melee combat works and how to extend it: combo attacks, the fireball special attack, blocking and parrying, the dash, hit detection, health and death, effects, physics hit reactions, and the test dummies.

The combat runs on the predicted player ghost described in [Unified Netcode for GameObjects](UnifiedNetcode-GameObjects.md). Read that guide's [Prediction and input](UnifiedNetcode-GameObjects.md#prediction-and-input) section first if `GhostField`, `PredictionUpdate` or `IsServer` on a host are unfamiliar.

## Contents

1. [Overview](#overview)
2. [Controls](#controls)
3. [Adding combat to a player prefab](#adding-combat-to-a-player-prefab)
4. [Attacks](#attacks)
5. [Combos](#combos)
6. [Hit detection and damage](#hit-detection-and-damage)
7. [Health and death](#health-and-death)
8. [Blocking and parrying](#blocking-and-parrying)
9. [Dash](#dash)
10. [Special attack (fireball)](#special-attack-fireball)
11. [Effects](#effects)
12. [Physics hit reactions (active ragdoll)](#physics-hit-reactions-active-ragdoll)
13. [Networking](#networking)
14. [Testing with combat dummies](#testing-with-combat-dummies)
15. [Known limitations](#known-limitations)
16. [File reference](#file-reference)

---

## Overview

| Component | Role | Runs on |
|---|---|---|
| `CombatControllerGhost` | Attack, combo, block and parry state; server hit detection | Owner (predicted) and server |
| `AttackData` (asset) | One attack: clip, timing, hit windows, damage, follow-ups, effects | Data |
| `ComboSet` (asset) | The move list: which attack each button starts | Data |
| `Projectile` | A fired projectile, such as the fireball: flight, impact and explosion | Everyone predicts it; the server applies the explosion |
| `Hitbox` | A sphere on a bone that deals hits, such as a fist | Server tests it |
| `Hurtbox` | A trigger on a bone that can be hit, with a damage multiplier | Server tests it |
| `Health` | Hit points, death and respawn, fed by `ServerHit` | Server changes it; everyone reads it |
| `HealthBar` | Bars over other players and on screen for the local player | Clients |
| `FPSControllerGhost` | Movement, knockback and the dash | Owner (predicted) and server |
| `FPSAnimatorGhost` | Plays attack and dash clips over the Animator, from replicated time | Everyone |
| `CombatEffects` | Swing, impact, parry and dash effects; hit-stop; camera shake; parry indicator | Clients |
| `ActiveRagdoll` + `RagdollWorld` | Physics hit reactions, within a budget | Clients |
| `CombatDummy` + `CombatDummySpawner` | Server-driven practice opponents | Server |

The flow of one swing:

1. The owner presses an attack button. `CombatControllerGhost` starts the attack in `PredictionUpdate`, on the owner's client and on the server.
2. Both advance the attack each tick. The owner sees its own swing with no delay.
3. While a hit window is open, the server tests the attacker's hitboxes against other players' hurtboxes.
4. The server applies block, parry, knockback and hit stun, takes damage off the target's `Health`, and counts the hit in `GhostField`s.
5. Clients see the counts change in the next snapshot and play effects and ragdoll reactions.

The server decides every hit. Clients only predict their own player's movement and attack state.

---

## Controls

| Action | Keyboard and mouse | Gamepad |
|---|---|---|
| Light attack | Left mouse button | West button (X / Square) |
| Medium attack | Right mouse button | North button (Y / Triangle) |
| Heavy attack | F | Right trigger |
| Special attack (fireball) | R | Right shoulder |
| Block (press to parry) | Q | Left shoulder |
| Dash | Double-tap a movement direction (W, A, S, D) | Flick the left stick twice |

The attack and block bindings are `InputAction` fields on `CombatControllerGhost`, editable in the Inspector. Special is bound to R and the right shoulder because E and the east button were already used. While a player carries an item, the left mouse button throws it, so attacking and blocking are off.

---

## Adding combat to a player prefab

`Assets/_Prefabs/Ghost/FPS_Player.prefab` is already set up. To set up another player:

1. **Components.** Put `CombatControllerGhost`, `CombatEffects`, `Health` and `HealthBar` on the root, next to `FPSControllerGhost` and `FPSAnimatorGhost`. Add `ActiveRagdoll` for physics hit reactions.
2. **Move list.** Assign a `ComboSet` to **Combos**. Every player must use the same asset (see [Networking](#networking)).
3. **Hitboxes.** Add a child under each striking bone, with a `Hitbox` and a `SphereCollider`. Give each a **Name** that the clips' animation events use. The player prefab has `RightHand`, `LeftHand`, `RightFoot` and `LeftFoot`.
4. **Hurtboxes.** Add a `Hurtbox` and a trigger collider to each body bone. Set **Damage Multiplier** per part. The player prefab has 11: the head at 1.5, two parts at 1, and eight limb parts at 0.75.
5. **Animation events.** Put `CombatAnimationEvents` on the GameObject with the `Animator`. Its empty `HitboxOn` and `HitboxOff` methods stop Unity logging "has no receiver" for the clips' events.

---

## Attacks

Create an attack with **Assets > Create > Combat > Attack**. The project's attacks are in `Assets/_Data/Combat`.

### Animation

| Field | Meaning |
|---|---|
| **Clip** | The attack's animation. It is played directly over the Animator through a Playables mixer. |
| **Play Clip Directly** | Off: set the Animator's `AttackType` parameter to **Animator Attack Type** and let the controller's own state play it. |
| **Blend In Time / Blend Out Time** | Seconds to blend from the previous pose, and back to locomotion at the end. |

The clip's pose is computed from the attack's elapsed time, not from a running animation clock. When prediction rolls back and replays, the pose stays correct.

### Timing

| Field | Meaning |
|---|---|
| **Duration** | Seconds the clip is stretched to, not counting the startup delay. |
| **Startup Delay** | Extra seconds of wind-up. The clip up to its first hit window plays slower to fill them. The rest keeps its speed. |
| **Hit Windows** | When each hitbox is active, as 0–1 fractions of the clip. |
| **Move Speed Multiplier** | The attacker's move speed while attacking, such as 0.2 for a committed heavy attack. |

**Clip time.** All in-attack times, including hit windows, effect times, the combo window and the cancel time, are 0–1 fractions of the *clip*. Startup Delay changes how long the wind-up takes in seconds, but none of these values need retuning. `AttackData.ToClipTime(elapsed)` converts seconds to clip time, and `ToElapsed(clipTime)` converts back.

```
clip:     0 ─────── wind-up ──────── first hit window ──────────── 1
seconds:  0 ── WindupEnd × Duration + StartupDelay ── … ── Duration + StartupDelay
```

For example, the Jab's fist lands at 0.32 of its clip. With a 0.45 s duration and a 0.15 s startup delay, the hit comes 0.29 s into the attack instead of 0.14 s.

**Hit windows from animation events.** Give the clip `HitboxOn` and `HitboxOff` events, with the hitbox name as the string parameter (empty means every hitbox). The asset reads them into **Hit Windows** when it loads and when it's edited, replacing what was typed in. The project's Hand_to_Hand clips already have them. A clip with no events keeps hand-entered windows. Turn off **Use Clip Hit Windows** to ignore the events and keep the typed windows. `Fireball` does this, with no windows, so its punch clip doesn't hit.

### Hit

| Field | Meaning |
|---|---|
| **Damage** | Base damage, multiplied by the struck hurtbox's multiplier. See [Damage](#damage). |
| **Knockback Speed / Knockback Up Speed** | Speed given to the target, away from the attacker and upward. |
| **Hit Stun Time** | Seconds the target can't move, attack or block. |
| **Can Be Parried** | Off: a timed block only blocks it. Use for unparryable finishers. The parry indicator flashes red for these. |

### Combo

| Field | Meaning |
|---|---|
| **Follow Ups** | Which attack each button leads to from this one. |
| **Combo Window Start / End** | When a follow-up press is accepted, in clip time. Presses outside it are ignored. |
| **Cancel Time** | The earliest clip time a queued follow-up cuts this attack short. |

### Projectile

| Field | Meaning |
|---|---|
| **Projectile Prefab** | A `Projectile` ghost fired by this attack, such as the fireball. Empty for a melee attack. Its explosion uses this attack's **Hit** settings. See [Special attack (fireball)](#special-attack-fireball). |
| **Projectile Time** | When it's fired, in clip time. |
| **Projectile Offset** | Where it's fired from, relative to the player's root and facing. |

### Effects and on-hit feedback

| Field | Meaning |
|---|---|
| **Effects** | A list of prefabs and sounds played as the attack passes a clip time, such as swing trails or whooshes. Each can attach to a hitbox and follow it. |
| **Impact Prefab / Sound**, **Blocked Prefab / Sound** | Played where the attack connects, depending on whether it was blocked. |
| **Hit Stop** | Seconds the attacker's and target's animations freeze on a hit. |
| **Camera Shake** | Shake force for the local players involved. |

---

## Combos

A `ComboSet` (**Assets > Create > Combat > Combo Set**) lists **Starters**, the attack each button starts from neutral. Each attack lists its own follow-ups, so the move list is a tree.

The project's set, `PlayerCombos`:

```
Light  → Jab ──┬─ Light  → Cross ──┬─ Light  → Haymaker
               │                   └─ Medium → Uppercut
               └─ Heavy  → Uppercut
Medium → Medium ── Heavy → Heavy
Heavy  → Heavy
Special → Fireball
```

How chaining works:

1. During an attack's combo window, pressing a button that has a follow-up queues it. A later press replaces the queued one.
2. When the clip reaches **Cancel Time**, the queued follow-up starts.
3. The new attack blends in from the old attack's pose, not from locomotion.
4. Without a queued press, the attack plays out and returns to neutral.

The [dummies](#testing-with-combat-dummies) use the same rules: they press the next button of their **Attack Sequence** once the combo window opens.

---

## Hit detection and damage

### How hits are tested

Only the server tests hits. On each tick that overlaps a hit window, it does an overlap-sphere query for each named hitbox against the attacker's opponents. A window counts if it overlaps any part of the tick, so a window shorter than a tick isn't skipped.

- **Players** are hit through their hurtboxes. If a hitbox touches several parts of the same player, the part with the highest damage multiplier counts.
- **Each target is hit once per swing,** however many ticks the window lasts.
- **Rigidbodies** that aren't players, such as crates, are pushed along the attacker's facing. **Rigidbody Knockback Multiplier** scales the push.
- **Hurtboxes follow the animated pose.** The server syncs transforms before testing, so a hit lands where the body actually is.

During a swing, active hitboxes' trigger colliders are turned on, so they show in the Scene view. Hit tests don't depend on this.

### A hit that lands

The target is pushed back, stunned for the attack's **Hit Stun Time**, and its attack, combo, block and dash are cancelled. Knockback slows at **Knockback Deceleration** (m/s per second). Upward knockback goes into the target's velocity, so gravity brings it back down.

### Damage

Each connection raises the server-only event `CombatControllerGhost.ServerHit` with a `CombatHit`. [`Health`](#health-and-death) listens to it and takes the damage off the target.

| Field | Meaning |
|---|---|
| `Attacker`, `Target`, `Attack` | Who hit whom, and with what |
| `Hurtbox` | The body part hit, or null for a non-player object |
| `Blocked`, `Parried` | How it was defended |
| `Damage` | `Attack.Damage × Hurtbox.DamageMultiplier`, or 0 if blocked |
| `Point` | Where the hitbox touched the target |

Subscribe to `ServerHit` for other reactions to hits, such as scoring, combo counters or achievements. It's raised on the server only.

---

## Health and death

`Health` gives a player hit points. Only the server changes them. Clients receive them in snapshots.

### Taking damage

`Health` subscribes to `ServerHit` and subtracts `hit.Damage` when the hit's target is its own player. The hit is ignored when:
- the player is already dead,
- it's within its respawn invulnerability, or
- the hit did no damage (blocked or parried).

The server can also change health directly with `ServerAddHealth(amount)`, positive to heal or negative to hurt, such as for fall damage or pickups.

### Death

When health reaches 0, the player dies:
- `IsDead` is set and `DeathCount` goes up by one.
- Its attack, guard and dash are cancelled. Until it respawns it can't move, jump, dash, attack or block. `CombatControllerGhost.IsDead` gates all of these, so the owner's prediction agrees with the server.
- It can't be hit. The server skips its hurtboxes.
- Its ragdoll goes limp (**Dead Strength** on `ActiveRagdoll`) and the body collapses. Dummies stop targeting it.
- The server-only event `Health.ServerDied` is raised with the hit that did it. Use it for kill feeds and scoring.

### Respawn

After **Respawn Delay** seconds, the player gets up with full health:
- It can't be hurt for **Respawn Invulnerability** seconds.
- With **Respawn At Spawn Point** on, it is moved back to where it first spawned. Otherwise it gets up where it fell.
- `Health.ServerRespawned` is raised.

A negative Respawn Delay means the player stays dead until your code respawns it.

### Settings

| Field | Default | Meaning |
|---|---|---|
| **Max Health** | 100 | Starting and respawn health. |
| **Regen Per Second** | 0 (off) | Health regained per second. |
| **Regen Delay** | 5 s | Time after the last damage before regeneration starts. |
| **Respawn Delay** | 3 s | Time a dead player stays down. Below 0: never respawn automatically. |
| **Respawn At Spawn Point** | off | Respawn where the player first spawned, instead of where it fell. |
| **Respawn Invulnerability** | 1 s | Time after respawning that hits do no damage. |

Replicated state: `Current`, `IsDead`, `DeathCount`, `RespawnRemaining`, `InvulnerableRemaining` and `RegenDelayRemaining`. The timers run in `PredictionUpdate` on the server, so `Health` needs a predicted ghost, like the player.

### Health bars

`HealthBar` shows a player's `Health`:
- **Other players:** a bar above the head, facing the camera. By default it appears only once the player is hurt, and hides when the player is dead or more than **Max Distance** (25 m) away.
- **Local player:** a bar at the bottom of the screen with the health numbers. While dead, a "Defeated" message counts down to the respawn.
- **Damage trail:** a light segment shows recent damage draining away.

The bars generate their own sprites and draw the screen bar with IMGUI, so they need no assets or canvas. Replace them with your own UI by reading `Health.Current`, `Health.Fraction` and `Health.IsDead`.

---

## Blocking and parrying

### Blocking

Hold Block to guard. A blocked hit does no damage, knockback or stun, but still plays the attack's blocked effects.

- **Angle.** Only attacks from within **Block Angle** (default 120°) in front of the defender are blocked.
- **Speed.** A blocking player moves at **Block Move Speed Multiplier** (default 0.4).
- **When.** You can't block while attacking, stunned, dead, carrying, or until a dash's animation finishes. Attacking drops the guard.

### Parrying

A fresh press of Block opens a **Parry Window** (default 0.15 s). A blocked hit during the window is a parry.

- **The attacker** loses its attack and is stunned for **Parry Stun Time** (0.9 s). It's pushed back at **Parry Knockback Speed** (3 m/s).
- **Both players'** animations pause for **Parry Hit Stop** (0.25 s on `CombatEffects`), and the parry effect and sound play at the contact point.
- **Mashing doesn't work.** After a press, the next parry needs **Parry Cooldown** (0.6 s). A successful parry resets the cooldown, so a well-timed defender can parry each hit of a combo.
- **Holding Block, or pressing it** during your own attack or stun, doesn't open the window.
- **Projectiles bounce back.** Parrying a fireball sends it back the way it came, as the parrying player's fireball. See [Parrying a fireball](#parrying-a-fireball).

### Parry indicator

Shortly before a swing can hit, a glint flashes on the attacker's striking hitbox:
- **Gold:** the attack can be parried.
- **Red:** the attack can only be blocked.

The settings are in the **Parry Indicator** section of `CombatEffects`:

| Field | Default | Meaning |
|---|---|---|
| **Show Parry Indicator** | on | Turns the flash on or off. |
| **Parry Indicator Lead** | 0.25 s | How long before the first hit window the flash appears. |
| **Parry Indicator On Own Attacks** | off | Also flash on the local player's own swings. |
| **Parryable / Unparryable Color** | gold / red | Flash colors. |
| **Size / Duration** | 1 m / 0.3 s | How big the flash is and how long it lasts. |
| **Prefab / Sound** | none | A custom flash instead of the built-in one, and an optional sound. |

The built-in flash, `ParryIndicatorFlash`, generates its own sprite and needs no assets.

---

## Dash

Double-tap a direction to dash forward, back, left or right, relative to where the player faces. The settings are in the **Dash** section of `FPSControllerGhost`:

| Field | Default | Meaning |
|---|---|---|
| **Double Tap Window** | 0.25 s | Most time between the two taps. |
| **Tap Threshold** | 0.6 | How far a stick must be pushed to count as a tap. Keys always count. |
| **Dash Speed × Dash Duration** | 12 m/s × 0.2 s | 2.4 m of travel. |
| **Dash Animation Time** | 0.4 s | The player can't attack or block until the animation finishes. |
| **Dash Cooldown** | 0.5 s | Time from one dash's start to the next. |
| **Allow Air Dash** | | Whether dashing works while airborne. |

Restrictions:
- You can't dash while attacking or stunned, and can't jump during a dash.
- Being hit cancels a dash.

The dash is predicted like the rest of movement. Its four clips (**Dash Forward / Back / Left / Right Clip** on `FPSAnimatorGhost`) are played over the Animator. The clips' root motion is ignored, so the travel comes from the dash velocity.

## Special attack (fireball)

The Special button (R, or the right shoulder) throws a fireball straight where the player is looking. It flies until it touches something, or until **Max Lifetime** runs out, and then explodes. Everyone within **Explosion Radius** takes the attack's hit.

How it works:

1. **Starting it.** Special only starts from neutral, like other starters. It can't be chained into from another attack. **Special Cooldown** (1.5 s, on `CombatControllerGhost`) begins when it starts, and while it runs Special does nothing. The cooldown is a predicted `GhostField`, `SpecialCooldownRemaining`.
2. **Firing.** The `Fireball` attack plays a punch clip. When its clip passes **Projectile Time**, `CombatControllerGhost.FireProjectile` spawns the attack's **Projectile Prefab** at **Projectile Offset** from the player.
3. **Aiming.** The fireball follows the camera's pitch as well as the player's facing. A ray is cast from the camera target along the view, up to **Projectile Aim Distance** (60 m). The fireball flies from its spawn point toward whatever the ray hits first, or toward the ray's end. The player's own colliders are skipped. If the aim point is closer than 1 m, or behind the spawn point, it flies along the view direction instead. The pitch comes from `FPSControllerGhost.ViewPitch`, which is predicted, so the server aims the same way.
4. **Flight.** `Projectile` works out its position from `Origin + Direction × Speed × Elapsed`, all `GhostField`s. It sweeps a sphere of **Radius** along each tick's path. It ignores its shooter and other players' character colliders, and hits players through their hurtboxes.
   - **Parried:** if the player it touches parries it, it doesn't explode. It flies back the way it came instead. See [Parrying a fireball](#parrying-a-fireball).
5. **Explosion.** On the server, `CombatControllerGhost.ServerExplosion` finds every player within **Explosion Radius**, keeping each player's hurtbox with the highest damage multiplier. Each one is blocked or hit as below. Non-player rigidbodies are pushed away from the blast. The shooter can't hit themselves.
   - **Hit:** full **Damage** times that hurtbox's multiplier, with no falloff. Knockback pushes them away from the blast point, plus hit stun.
   - **Blocked:** the explosion is blocked if the player's guard faces the blast point. An explosion can't be parried: a guard inside the parry window only blocks it. Only the fireball itself can be parried, before it explodes.

   The shooter's `HitConfirm…` fields are updated for each player, so impact effects play as for a melee hit.
6. **Despawn.** The fireball stays at the blast point for **Despawn Delay** (0.5 s) so clients receive the explosion, then the server destroys it.

**Networking.** The fireball is a [client predicted spawn](UnifiedNetcode-GameObjects.md#client-predicted-spawning). The shooter's client spawns its own copy in `PredictionUpdate`, so it appears with no delay. When the server's copy arrives, it's matched to that copy by shooter and spawn tick (`ProjectileSpawnClassificationSystem`), so another player's fireball can't take its place. Other clients receive the server's copy and predict it from its `GhostField`s. Only the server applies the explosion's damage and despawns the fireball.

**Projectile settings** (`Projectile`, on `Fireball_Ghost`):

| Field | Default | Meaning |
|---|---|---|
| **Speed** | 20 m/s | Flight speed. |
| **Radius** | 0.25 m | Radius of the sphere swept along the path to find what it touches. |
| **Max Lifetime** | 2.5 s | Explodes in the air after this. |
| **Explosion Radius** | 3 m | Players and rigidbodies within this distance are affected. |
| **Despawn Delay** | 0.5 s | How long the server keeps it after exploding. |
| **Hide On Explode** | Ball, Light, Trail | Hidden when it explodes. Shown again if a correction undoes the explosion. |
| **Explosion Prefab / Lifetime / Sound / Volume** | `FireballExplosion` | Played on clients where it explodes. |

The damage, knockback, hit stun and impact effects come from the attack asset, `Assets/_Data/Combat/Fireball.asset`. It has 15 damage, 9 m/s knockback with 3 m/s upward, and 0.4 s hit stun, and it reuses the Heavy attack's impact effects. Its duration is 0.7 s, it has a 0.1 s startup delay, and it fires at 0.35 of the clip.

The prefab's ball, light, trail and explosion are placeholders made from primitives and particle systems, with the `M_Fireball` and `M_FireballParticles` materials.

**Making another projectile attack.** Any attack can fire a projectile:

1. Make a prefab with a `GhostObject` set to **Predicted** (Supported Ghost Modes **All**) and a `Projectile`, without a `NetworkObject`.
2. Assign it to an attack's **Projectile Prefab**.
3. Turn off **Use Clip Hit Windows** if the clip has hitbox events you don't want, and clear **Hit Windows** so the swing itself doesn't hit.

Every prefab with a `Projectile` is matched by the player who fired it (`LaunchShooterGhostId`, which a parry doesn't change), so other players' projectiles can't be confused with yours, even after one is parried. Two of your own shots from the same prefab fired less than 5 ticks apart can still be swapped. The closest spawn tick wins, which is usually right. See [Matching projectiles by shooter](UnifiedNetcode-GameObjects.md#matching-projectiles-by-shooter).

### Parrying a fireball

A fireball that touches a player who parries it is sent back the way it came. The parry uses the same rules as for a swing: the player must be blocking inside the **Parry Window**, facing the fireball within **Block Angle**, and the attack's **Can Be Parried** must be on. It's on for `Fireball`.

When a fireball is parried, `Projectile.TryReflect`:

1. **Reverses it** from the contact point: **Origin** becomes the contact point, **Direction** is reversed, and **Elapsed** restarts, so it gets a full **Max Lifetime** again.
2. **Changes its shooter** to the parrying player. It can now hit the original shooter. Its explosion and hit confirms count as the parrying player's, and that player can't be hurt by it.
3. **Records the parry** on the server. `CombatControllerGhost.ServerParryProjectile` raises the parrying player's `ParryCount`, which plays the parry effect, hit stop and camera shake. It also resets their parry window and cooldown. Unlike a melee parry, nobody is stunned or pushed back, because the shooter may be far away.

A parried fireball can be parried again, so two players can send it back and forth.

The reflection is predicted with the rest of the flight. Each machine checks the parry against its own copy of the defender:
- The defender's own client predicts its parry window, so it sees its parry with no delay.
- Other clients, including the shooter, see the defender's guard a little in the past, so they can guess wrong. Their copy explodes or bounces until the server's snapshot corrects it, and an explosion effect may already have played.

The **Parry** test dummy parries fireballs too. It presses Block when a fireball will reach it within **Parry Lead** plus one simulation step.

**Tested** on 2026-10-14 on a host in the Editor: a fireball thrown at the Parry dummy from 7 m was parried 1.1 m in front of the dummy. It flew back and hit the thrower for 22.5 damage, and the dummy took none.

**Tested with a remote client** on 2026-10-14, with a Multiplayer Play Mode virtual player:
- **The client parries the host's fireball** (thrown from 15 m). The client pressed Block with the fireball 2 m away. Its copy bounced back three frames later, and the server's parry arrived six frames after that. The client's path didn't jump when the server's state arrived. The bounced fireball exploded on the host's player at the same point and tick on both machines, and the host's player was hit.
- **The client throws at the Parry dummy.** The client sees the dummy's guard late, so its copy exploded on the dummy for a frame or two, then snapped to the bounce from the server. The bounce, the parry count and the hit on the thrower were all correct. With the default matching settings, the client's predicted copy was never matched in these runs, because both Editors were stalling. After the settings were raised, three of four matched (see [Known issues](UnifiedNetcode-GameObjects.md#predicted-spawns-fail-while-both-editors-stall)).

---

## Effects

`CombatEffects` is cosmetic and runs on clients only. It watches `CombatControllerGhost`'s counters and plays effects when they change:

| Trigger | What plays |
|---|---|
| The attack passes an effect's time | The attack's **Effects** (trails, whooshes) |
| `HitConfirmCount` changes (this player hit something) | The impact or blocked prefab and sound at the contact point, hit-stop, camera shake |
| `HitCount` changes (this player was hit) | Hit-stop and camera shake, using the attack's values |
| `ParryCount` / `ParriedCount` changes | The parry prefab and sound, an animation pause on both players, camera shake |
| `DashCount` changes | The dash prefab and sound |
| The attack nears its first hit window | The [parry indicator](#parry-indicator) |

Notes:
- **Prediction replays** don't trigger anything twice. Effects key off counters and the attack's serial number, not individual ticks.
- **Joining late** doesn't replay old events. Counters are read once when the player first appears.
- **Hit-stop** freezes only the animation graph. Gameplay and the network keep running.
- **Camera shake** uses Cinemachine impulses. A listener is added to the scene's Cinemachine camera if it has none.

---

## Physics hit reactions (active ragdoll)

`ActiveRagdoll` makes a hit throw the body. The ragdoll follows the animation through two kinds of joint forces:
- **Muscles** bend each joint toward its animated angle.
- **Pins** pull each body part toward its animated position and hold up its weight.

When the player is hit, muscles and pins go slack and the struck part gets the hit's impulse (`LastHitImpulse`, `LastHitHurtbox`). Strength then recovers and the pose blends back to pure animation. A carried player goes limp until it lands.

A dead player goes fully limp and collapses. While dead, the hips may move up to **Dead Max Hips Offset** (1.5 m) from the animated hips instead of **Max Hips Offset**, so the body can lie flat. It recovers when the player respawns.

- **Any skinned model works.** `RagdollBuilder` works out body parts and colliders from the model's bones, humanoid or not. Enable Read/Write on the model's mesh so colliders can be fitted to it. Otherwise it falls back to bone lengths and logs a warning.
- **Cosmetic, client only.** It doesn't affect the server's hurtboxes or gameplay.
- **One shared physics scene.** All ragdolls live in `RagdollWorld`'s physics scene, stepped once per rendered frame. The default physics scene can't be used because netcode steps it once per prediction tick, replays included. Body parts use the `Ragdoll` layer, which must not collide with itself (**Project Settings > Physics**).
- **Budget.** Each frame, `RagdollWorld` ranks the ragdolls that want to simulate: the local player first, then by distance to the camera. Ragdolls farther than **Max Distance** (40 m) or off screen are culled. At most **Max Active Ragdolls** (8) simulate, plus the local player's. The rest blend back to animation. `RagdollWorld` is created on first use; put one in the scene to change its settings.

---

## Networking

### What is synchronized

`CombatControllerGhost` keeps its state in `GhostField`s:

| Group | Fields | Notes |
|---|---|---|
| Attack | `AttackId`, `AttackElapsed`, `AttackSerial`, `QueuedInput`, `PreviousAttackId`, `PreviousAttackTime`, `SpecialCooldownRemaining` | Predicted by the owner. |
| Guard | `IsBlocking`, `BlockHeld`, `ParryWindowRemaining`, `ParryCooldownRemaining` | Predicted by the owner. |
| Hits taken | `KnockbackVelocity`, `HitStunRemaining`, `HitCount`, `LastHitHurtbox`, `LastHitAttackId`, `LastHitImpulse` | Set by the server. |
| Hits dealt | `HitConfirmCount`, `HitConfirmAttackId`, `HitConfirmPoint`, `HitConfirmBlocked` | For effects. |
| Parries | `ParryCount`, `ParryPoint`, `ParriedCount` | For effects. |

`Health` has its own fields (see [Health and death](#health-and-death)), all set by the server. `Projectile` has its own too (see [Special attack (fireball)](#special-attack-fireball)).

- **`AttackSerial`** goes up by one per swing, so repeating the same attack is still a new swing for effects, the dummies and remote animation.
- **Attack ids** are indexes into the `ComboSet`. The server and every client must use the same `ComboSet` asset, or ids will name different attacks.
- **Precision.** Fields used only for effects (`ParryPoint`, `HitConfirmPoint`, `LastHitImpulse`) are quantized to 1 cm. Fields the owner predicts with stay unquantized, so replays match the server exactly. See [Configuring a field with `FieldConfig`](UnifiedNetcode-GameObjects.md#configuring-a-field-with-fieldconfig).

### Input

Attack presses are `InputEvent`s in `FPSInputs` (`LightAttack`, `MediumAttack`, `HeavyAttack`, `SpecialAttack`, `Dash`), so a press is never lost between ticks. `Block` is a held bool.

Presses are recorded in the Input System callbacks and copied into the input component in `PredictionGatherInput`. Polling `WasPressedThisFrame` there misses presses, because input gathering runs before the Input System updates.

Input fields have no `[GhostField]`, so they go only to the server, not to other clients.

### Remote players

Remote players aren't predicted, so their attack and dash animations are driven by `RemoteClock` in `FPSAnimatorGhost`:
- The clock advances smoothly every frame.
- It restarts when `AttackSerial` or `DashCount` changes.
- It catches up gently with the replicated time, and snaps if it drifts by more than 0.25 s.

Interpolating `AttackElapsed` instead would play the old attack backwards when a combo chains, because the elapsed time resets to 0. Locomotion speed comes from the interpolated `PredictedVelocity`.

### Movement consistency

The player's rigidbody is moved only through its velocity, and the player physics material has zero friction (combine Minimum). This keeps movement, knockback and dashes the same on the owner, the server and remote clients.

---

## Testing with combat dummies

`Assets/_Prefabs/Ghost/FPS_Dummy.prefab` is a variant of the player with no camera and a `CombatDummy` brain. No client owns it.

A dummy plays by player rules:
- It presses the same buttons through the same input path, so its blocks and parries use the real parry window and cooldown.
- It runs only on the server.
- Its owner is set to `-1`, so netcode doesn't log an error for an owner-predicted ghost without an owner.

**Spawning.** The scene's **Combat Dummy Spawner** (`CombatDummySpawner`) spawns dummies when the host or server starts. Its defaults, relative to the spawner:

| Dummy | Offset | Behavior |
|---|---|---|
| Block | (-3, 0, 0) | Keeps its guard up. |
| Parry | (0, 0, 0) | Parries every parryable attack. |
| Attack | (3, 0, 0) | Follows you and attacks with Light, Light, Light. |

Each entry sets the dummy's defense, parry chance, attack sequence and follow behavior. Gizmos show where dummies will appear.

**`CombatDummy` settings:**

| Group | Settings |
|---|---|
| Target | **Sight Range**, **Target Dummies** (so dummies can fight each other), **Face Target**, **Turn Speed**, **Follow Target**, **Follow Distance**, **Leash Radius** (it won't chase beyond this distance from home). |
| Defense | **Defense** (`None`, `Block`, `Parry`, `Mixed`), **Parry Chance** for `Mixed`, **Parry Lead** (how early it presses Block, on top of one simulation step; keep the total below the Parry Window). It defends against swings and incoming projectiles. |
| Offense | **Attack**, **Attack Sequence**, **Attack Range**, **Attack Interval**. A sequence can include `Special`, so a dummy can throw fireballs. |

Dummies inherit `Health` and `HealthBar` from the player prefab, so they can be defeated and respawn after 3 seconds.

To test both sides of a parry, stand in front of the attack dummy and parry its jabs on the gold flash, then attack the parry dummy to be parried yourself. Throw a fireball at the parry dummy to have it sent back at you. For two real players, use Multiplayer Play Mode with a virtual player.

---

## Known limitations

- **A culled body stands back up.** A dead player's ragdoll counts against the `RagdollWorld` budget. If it's culled (off screen, too far, or over budget), it blends back to its standing animation until it's simulated again.
- **Respawn teleports are corrected, not predicted.** With Respawn At Spawn Point, the owner sees its player jump back when the server's snapshot arrives.
- **Prefab variants of a network prefab need their own `GlobalObjectIdHash` saved in the variant file.** Otherwise a Multiplayer Play Mode virtual player gives the variant its base prefab's hash, logs "duplicate GlobalObjectIdHash", and can't connect. `FPS_Dummy` now stores its own hash. Do the same for new variants: change and restore the NetworkObject's hash through a `SerializedObject` and save.
- **No lag compensation.** The server tests hits against its current poses. A remote defender sees attackers a little in the past, so with latency the right moment to parry comes slightly before the flash. Raise **Parry Indicator Lead** or **Parry Window** if parries feel late online.
- **Hit detection is server-only.** Impact effects and reactions wait for the server's snapshot. The attacker's own swing animation is predicted with no delay.
- **Remote fireballs show up partway along.** Other clients receive a fireball about a round trip after it's thrown, then predict it forward, so it first appears already some way from the thrower. In the remote test, a fireball thrown from 7 m appeared on the target's screen a frame before it hit, too late to parry. From 15 m there was time. Real network latency should have the same effect.
- **The fireball's explosion isn't predicted for damage.** The shooter sees the fireball and its explosion immediately, but damage and knockback come with the server's snapshot. A fireball can explode early on the server, for example on someone standing behind the shooter. Until the correction arrives, the shooter's screen can show it still flying.
- **Unconfirmed predicted fireballs are untested.** The server may never fire the fireball the shooter predicted, for example because the shooter was stunned on the server first. Netcode should then destroy the predicted copy after a timeout, but this hasn't been tested.
- **A mispredicted swing can skip the predicted fireball.** The client spawns its copy only the first time it predicts the firing tick. If its prediction of the attack is wrong at that moment, it spawns nothing and shows the server's fireball when it arrives, a few hundred milliseconds late. This happened twice with both Editors stalling. See [Known issues](UnifiedNetcode-GameObjects.md#predicted-spawns-fail-while-both-editors-stall) in the netcode guide.
- **Editor noise.** After a player prefab is saved, the AI Assistant's search indexer can log "Sanity check failed, releasing a ghost" errors from preview copies of the prefab. They come from `PreviewRenderUtility`, not the combat code.
- **Unused files.** `ParryScreenFreeze.cs`, its shader and its material are left over from the earlier screen-freeze parry effect. They can be deleted.

---

## File reference

| File | Contents |
|---|---|
| `Assets/_Scripts/Combat/CombatControllerGhost.cs` | Attack, combo, block and parry logic; server hit detection; fireball firing, aiming and explosion; `ServerHit` |
| `Assets/_Scripts/Combat/AttackData.cs` | `AttackData`, `HitWindow`, `ComboLink`, `AttackEffect`, `AttackKind`; clip-time conversion |
| `Assets/_Scripts/Combat/ComboSet.cs` | Move list and attack ids |
| `Assets/_Scripts/Combat/Projectile.cs` | Projectile flight, impact, parry reflection, explosion and effects |
| `Assets/_Scripts/Combat/ProjectileSpawnClassificationSystem.cs` | Matches the server's projectiles to the shooter's predicted copies by shooter and spawn tick |
| `Assets/_Scripts/Combat/Hitbox.cs`, `Hurtbox.cs` | Hit and hurt volumes |
| `Assets/_Scripts/Combat/CombatAnimationEvents.cs` | Receiver for `HitboxOn` / `HitboxOff` events |
| `Assets/_Scripts/Combat/CombatEffects.cs` | Client effects, hit-stop, camera shake, parry indicator |
| `Assets/_Scripts/Combat/ParryIndicatorFlash.cs` | Built-in parry flash |
| `Assets/_Scripts/Combat/ActiveRagdoll.cs`, `RagdollBuilder.cs`, `RagdollWorld.cs` | Physics hit reactions and their budget |
| `Assets/_Scripts/Combat/Health.cs` | Health, death, respawn; `ServerDied`, `ServerRespawned` |
| `Assets/_Scripts/Combat/HealthBar.cs` | Overhead and on-screen health bars |
| `Assets/_Scripts/Combat/CombatDummy.cs`, `CombatDummySpawner.cs` | Practice dummies |
| `Assets/_Scripts/Ghost/GhostController/FPSControllerGhost.cs` | Movement, knockback, dash |
| `Assets/_Scripts/Ghost/GhostController/FPSAnimatorGhost.cs` | Attack and dash clip playback, remote clocks |
| `Assets/_Scripts/Ghost/GhostController/FPSInputs.cs` | Input component, `DashDirection` |
| `Assets/_Data/Combat/` | `PlayerCombos` and the attack assets |
| `Assets/_Prefabs/Ghost/FPS_Player.prefab`, `FPS_Dummy.prefab` | Player and dummy prefabs |
| `Assets/_Prefabs/Combat/Fireball_Ghost.prefab`, `FireballExplosion.prefab`, `M_Fireball.mat`, `M_FireballParticles.mat` | Placeholder fireball ghost, explosion effect and materials |
