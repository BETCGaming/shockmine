# ShockMine — a Shock Mine item for Super Battle Golf

Current as of **0.4.1**. This is the design as built; where it once said something different, the
[History](#history) section records what changed and why. `CHANGELOG.md` is the per-release
record.

## Context

Super Battle Golf is a Unity **Mono** game on **Mirror** networking with the host as server, modded
through **BepInEx 5**. The game's own code lives in `GameAssembly.dll` and `SharedAssembly.dll`
(there is no `Assembly-CSharp.dll`), both publicized at build time so internals and privates are
reachable without reflection.

This project is a **standalone mod** adding the **Shock Mine**: a pickup built from the game's
Landmine that, instead of exploding, **seizes** everyone it catches — held rigid for a
host-configured window, wrapped in the lightning-in-a-bottle zap effect — and then **knocks them
down**, crediting the planter.

`~/workbench/superbattlegolfmod` (the **BallSwapItem / Switcheroo** mod) is the reference. Every
hard problem in registering a modded item for this game was solved there first and the solutions
were ported, not rediscovered: `Switcheroo.cs` (ItemType + prefab clone + Mirror asset id),
`SwitcherooPool.cs` (pool injection with a graceful fallback), `SwitcherooRules.cs`
(`itemOrderLookup` rebuild), `SwitcherooRule.cs` (a cloned match-setup row), `ModGate.cs`
(keeping unmodded clients out), `SwitcherooUse.cs` (own use routine), `SwitcherooNetwork.cs`
(mod-owned networking).

### Findings from the decompiled game that shaped the design

**1. Keeping the `Landmine` component on the clone buys the whole detection system.**
`LandmineManager` is a `SingletonBehaviour` holding a `List<Landmine>`; every `FixedUpdate` it
schedules a Unity job batch (`UpdateLandmineTransformsJob` → `SetUpLandmineOverlapChecksJob` →
`OverlapSphereCommand.ScheduleBatch`) and hands each mine its hits through
`Landmine.ProcessCollidersInWarningRange`. Registration is automatic on arming. A clone carrying
the `Landmine` component is driven by that job system for free — including the blinking warning
VFX, the arm-on-plant / arm-when-stationary delays, freeze immunity, and reflection off the
Electromagnet Shield.

**2. `Landmine.ServerExplode` is a single, complete divert point.** Both trigger paths —
proximity (`ProcessCollidersInWarningRange`) and physical contact (`OnCollisionEnter`) — funnel
into it. A Harmony prefix there that returns `false` for our mines inherits the landmine's trip
behaviour *exactly*, speed gate included, with no re-implementation:

```csharp
if (base.isServer && distanceSquared <= ItemSettings.LandmineCollisionRangeSquared)
    ServerExplode();                                   // contact: no speed check
...
if (!(velocity.sqrMagnitude < ItemSettings.LandmineDetectionMinSpeedSquared))
    if (base.isServer && distanceSquared <= ItemSettings.LandmineDetectionRangeSquared)
        ServerExplode();                               // proximity: must be moving
```

**3. The four landmine radii are distinct**, and the Shock Mine keeps the landmine's split between
tripping and effect:

| Setting | Role | Shock Mine uses it for |
|---|---|---|
| `LandmineWarningRange` | overlap query radius; drives the warning blink | inherited, untouched |
| `LandmineDetectionRange` | trips on an entity moving faster than `LandmineDetectionMinSpeed` | **trip zone** |
| `LandmineCollisionRange` | trips on contact regardless of speed | **trip zone (contact)** |
| `LandmineExplosionRange` | the `Physics.OverlapSphere` radius `ServerExplode` actually hits | **seize zone** |

**4. `Hittable.FrozenState.Frozen` is the game's existing immobilisation and `PlayerMovement`
honours it in ~20 places** — gravity factor, linear damping, rotation, speed-boost denial,
grounding, respawn. Reusing it is far cheaper than re-implementing a hold. But it cannot be
*called*: `ServerFreeze()` is a compiler-generated local function inside `HitWithItem`, hardcoded
to `GameManager.ItemSettings.FreezeBombFreezeDuration`. The mine sets
`Hittable.NetworkfrozenState` itself and runs its own timer.

`FrozenState` also does **not** stop a body: it changes damping and gravity only, so a frozen
player slides, drifts and keeps falling. A real hold has to cancel velocity itself.

**5. The ice block rides on the SyncVar hook, not on the freeze bomb — and the game depends on
it existing.** `OnFrozenStateChanged` → `UpdateIceBlock()` → `FreezeBombManager.GetUnusedIceBlock()`,
so setting `frozenState` builds an ice block whether we want one or not. It cannot simply be
prevented: `PlayerMovement.UpdateFrozenState` reads `PlayerInfo.AsHittable.IceBlock.Collider` with
no null check every physics tick. The block has to be built as normal and then *hidden* —
renderers and collider off — for our victims only, and restored before it goes back to the pool,
or the next freeze-bomb victim gets an invisible block.

**6. The zap is a pooled VFX with a fixed duration.**
`Hittable.PlayLightningStrikeHitEffectsForAllClients(isDirectHit: true, owner)` plays
`VfxType.ThunderstormPlayerZapped` for `ItemSettings.ThunderstormLightningStrikeHitVfxDuration`,
then `ThunderstormPlayerZappedEnd`. One call will not cover a multi-second hold, so the mine
drives `VfxPersistentData.TryGetPooledVfx` directly. `Play` defaults to
`registerWhenFinished: true`, which lets the pool reclaim the instance mid-hold; ours passes
`false` and returns it explicitly.

**7. The game hardcodes `ItemType.Landmine` in several places that must be bypassed.**

```csharp
// PlayerInventory.TryUseItem's dispatch (a compiler-generated local, GetItemUseRoutine)
case ItemType.Landmine:
    return (isAirhornReaction || PlayerInfo.Input.IsHoldingAimSwing)
        ? TossLandmineRoutine() : PlantLandmineRoutine();
// any other type ends in ThrowSwitchExpressionException

// CourseManager.ServerSpawnLandmineInternal
PhysicalItem physicalItem = ServerSpawnItem(ItemType.Landmine, 1, position, rotation, ...);
```

Three more special cases in `PlayerInventory` (aim-swing-held selection in `TrySelectItemSlot`,
and the not-grounded gates) only handle the landmine. A modded `ItemType` slips past all of them,
so the mine has its own use routines, its own spawn message, and re-applies those gates.

The rest are outside `PlayerInventory`, and 0.3.0 missed them, so `LandmineParity.cs`
re-applies them:

| Where | Landmine rule |
|---|---|
| `PlayerMovement.CanMove` | no moving while **planting** (`ItemUseType.Regular`) — this is the landmine's halt, and it is not in the plant routine |
| `PlayerMovement.CanJump` | no jumping while planting or tossing |
| `ApplyRotation`'s local `CanRotate` | no turning while planting or tossing |
| `HitWithItemInternal`'s local `ShouldBeDestroyedByHit` | lightning and railgun vaporize loose items, but not landmines |
| `UpdateSwungByRocketDriverTrailEffects`'s local `ShouldPlay` | no rocket trail on a planted landmine (cosmetic) |
| `PhysicalItem.CmdGiveToPlayer` | picking up someone else's armed landmine sends `RpcInformDisarmedLandmine` |

Not carried over, on purpose: the landmine's knockback and knockout cases in `Hittable` and
`PlayerMovement` (the Shock Mine never explodes, so it never hits anyone), its explosion decal,
and `ServerSpawnLandmineInternal` (ours has its own spawn message).

**8. Held models are a separate system.** What a player holds in hand comes from
`EquipmentType`, not `ItemData.Prefab`, and an unrecognised item falls through to `None` — an
invisible device and a planting animation that mimes holding nothing. The mine registers its own
`EquipmentType`.

**9. `ServerInformKnockedOut` does not knock anyone out.** It only reports a knockout to the feed
and scoring and never touches `knockoutState`. `PlayerMovement.TryKnockOut` is what knocks a
player down, and it brings knockout immunity and teammate protection with it. But
`TryKnockOut` is a **local-player API**: it drives the victim's own animator and sends
`CmdInformKnockedOut` from their client. Called on the server for a remote player, it changes
only the server's copy and throws "Only the local player is allowed to remove their own status
effects". It must run on the victim's own client.

**9a. Thawing grants knockout immunity.** `OnLocalPlayerFrozenStateChanged` calls
`StartKnockoutImmunity` the moment a local player's `frozenState` leaves `Frozen`, before
anything else runs. That protection is meant for someone climbing out of freeze-bomb ice. A
knockout straight after the thaw is refused, so the Shock Mine cancels that immunity
(`CancelKnockoutImmunity`) before knocking the victim down.

**9b. Players are simulated by their own client.** On the host a remote player's rigidbody is
kinematic, so the server cannot hold them still. Writing velocity to it only logs "Setting
linear velocity of a kinematic body is not supported". The hold for a player runs on that
player's own client; the server holds only bodies it simulates (carts, balls, loose items, and
the host's own player).

**10. The landmine appears only in the Close item pool**, at weight 15. "Wherever the landmine
goes" therefore means the Shock Mine spawns only from close-range boxes. That is the intended
behaviour, kept deliberately; the Switcheroo, which injects unconditionally, reaches five pools.

## Decisions

| Area | Decision |
|---|---|
| Trip zone | `LandmineDetectionRange` (+ `LandmineCollisionRange` on contact), **keeping the landmine's `LandmineDetectionMinSpeed` gate**: a stationary or creeping player is not caught, exactly as with a landmine |
| Seize zone | `LandmineExplosionRange`: the landmine's blast radius, so the mine catches slightly wider than where it was tripped |
| Hold window | **One shared timer from the first trip.** The mine stays live and keeps catching entrants during it; everyone is released together when it ends. A late entrant gets a short hold |
| Mine lifetime | Survives its window, then `AsEntity.DestroyEntity()`: one mine, one window, like the landmine. A discharge whose first sweep catches nobody (typically a shot mine) is spent at once |
| Discharge | Every trip shows at the mine itself: `VfxType.ThunderstormStrike` via `VfxManager.ServerPlayPooledVfxForAllClients`, plus a thunderclap (`ShockSprungMessage`) on every client, so a mine set off with nobody in range is visibly gone off rather than silently vanishing |
| Victims | Anything with a `Hittable` and a rigidbody in the seize zone: players, golf carts, balls, loose items. **The owner's teammates are spared** in Teams mode, as the landmine spares them; **the owner is not**. Traffic vehicles are skipped; anything already frozen (a freeze bomb) is left alone |
| Carts | Emptied, not held with people inside: passengers are thrown clear on the game's own `GolfCartExitType.Knockout` exit and seized individually where they land |
| Deploying | Exactly the landmine's rules: planting halts the player, no jumping or turning while planting or tossing, can't plant in mid-air, leaving the ground cancels a plant |
| Hold | Victims freeze **where they are caught**, with no pull toward the mine. `NetworkfrozenState = Frozen` **plus** all linear and angular velocity zeroed every physics tick, by whichever peer simulates the body: the server for carts, balls, items and the host's player; each remote player's own client for themselves. Caught in mid-air, you hang there |
| Release | Server: `frozenState` back to `None`, `EliminationReason.Landmine` credited to the planter, release message sent. Victim's own client: wait for the thaw, cancel the thaw immunity, **knock out** via `TryKnockOut` with no impulse (victims crumple where they were held). Recovery is the game's standard knockout duration |
| Other landmine rules | Survives lightning and railgun; no rocket trail when planted; picking up someone else's armed one shows the "disarmed" notice |
| Ice block | Built as the game expects, then hidden for our victims only; restored on `WillBeReturnedToPool` |
| Appearance | Landmine prefab cloned and every renderer tinted `ShockItem.DeviceColor` (steel blue/cyan); hand model cloned and tinted the same; custom 2D icon as an embedded PNG |
| Spawning | Added **alongside** the Landmine to every pool the Landmine is in, at the Landmine's own weight, with its own match-setup probability slider |
| Host rules | **Seize duration** only: a Battle-section dropdown of **3 / 5 / 7 / 9 s**, default 5. No per-hole limit; mines are limited only by how often they are found |
| Feedback | Thunderclap and camera shake on being caught · Electromagnet shield hum for the hold · info-feed line naming the planter · on-screen countdown for the seized player · knockout on release |
| Config | `Enabled`, `SoundVolume`, `BlockUnmodded` (host), `DebugHotkeys`, `VerboseLogging`: only what has no host-rule home |
| Unmodded players | Disconnected by `ModGate` unless the host turns `BlockUnmodded` off: a modded `ItemType` in the pools is unresolvable on a vanilla client |
| Package | Standalone plugin with its own GUID and Thunderstore package (`BETCGaming-ShockMine`); BallSwapItem's build scaffolding copied |

### Reserved ids

All far above the game's own ranges (items `None..JumboBurger`, 0..17; rules
`Countdown..SmallTeamBoost`, 0..30) and clear of the Switcheroo's 200s:

| Id | Value |
|---|---|
| `ShockItem.Type` | `(ItemType)201` |
| `ShockEquipment.Type` | `(EquipmentType)201` |
| `ShockItem.SeizeDurationRule` | `(MatchSetupRules.Rule)202` |
| *(retired)* | `(MatchSetupRules.Rule)201`, the removed "One Shock Mine per hole" rule. Leave it unused |
| `NetworkAssetId` | `0xB5A50101` |

## Architecture

```
sbg-shock-mine/
  Directory.Build.props          copied from superbattlegolfmod
  Directory.Build.targets        copied — NuGet feeds, publicizer, deploy, PackTS
  Config.Build.user.props.template
  .config/dotnet-tools.json      tcli
  ShockMine.slnx
  plans/shock-mine-plan.md       this file
  src/ShockMine/
    ShockMine.csproj             Version lives here
    thunderstore.toml
    Assets/shock-mine-icon.png   embedded resource
    Plugin.cs                    BepInEx entry, config, serializers, Harmony PatchAll
    Trace.cs                     verbose-logging helper
    MirrorSerializers.cs         hand-written Writer<T>/Reader<T> for our messages
    ModGate.cs                   ShockHelloMessage; disconnect unmodded clients
    ModRunner.cs                 per-frame Ensure* re-registration, debug hotkeys
    ShockItem.cs                 ItemType, ItemData + prefab clone, ShockMarker, tint, icon, asset id
    ShockEquipment.cs            the model held in hand, as its own EquipmentType
    ShockLocalization.cs         ITEM_201 string-table entry, animator mapping
    ShockPool.cs                 pool injection at the landmine's weight + draw-time fallback
    ShockRulesScreen.cs          itemOrderLookup rebuild so the slider can be indexed by 201
    ShockRules.cs                seize duration (DurationOptions index) + GetDefaultValue patch
    ShockRuleRows.cs             the cloned "Seize duration" row; silences its localizer
    ShockUse.cs                  TryUseItem prefix: plant / toss; the three landmine gates
    LandmineParity.cs            every other ItemType.Landmine special case (movement locks,
                                 vaporize immunity, rocket trail, disarm notice)
    ShockLocalVictim.cs          victim's own client: hold the local player, knock out on release
    ShockNetwork.cs              messages: spawn (client→server), seized, released (server→clients)
    LandminePatches.cs           ServerExplode divert; ice block hiding for our victims
    ShockSeize.cs                ShockSeizeWindow (server) + ShockVictims (every client)
    ShockVfx.cs                  one pooled zap per victim, parented to them
    ShockAudio.cs                thunderclap on catch, hum for the hold
    ShockUi.cs                   countdown and refusal notices
```

Files are `Shock<Part>.cs` rather than `ShockMine<Part>.cs`: the namespace is `ShockMine`, so a
`ShockMine.ShockMinePool` reads badly and a class named `ShockMine` would collide with it.

### How a Shock Mine runs, end to end

```
local player presses use
  └─ ShockUsePatch  (prefix on PlayerInventory.TryUseItem)
       ├─ equipped item is ShockItem.Type?        else fall through to the game
       ├─ on the driving range?                   refuse, with a notice
       ├─ reuse CanUseEquippedItem  (holster, stun, cooldown gates stay honoured)
       ├─ IsHoldingAimSwing ? toss routine : plant routine
       │    ├─ mirrors of TossLandmineRoutine / PlantLandmineRoutine: timings from
       │    │  ItemSettings.Landmine*Time, PlayLandminePlantForAllClients, screenshake
       │    └─ while it runs, LandmineParity locks the player: no move (plant),
       │       no jump, no turn (plant and toss)
       └─ send ShockSpawnMessage(pos, rot, vel, angVel, armType, useId, useHash)
            └─ server: per-connection rate limit + ServerValidateAndConsumeItemUse(useHash)
                 ├─ ServerSpawnItem(ShockItem.Type, ...) → PhysicalItem + Landmine
                 ├─ Landmine.ServerInitialize(armType, owner, useId)   ← arming for free
                 └─ NetworkServer.Spawn

LandmineManager job batch  (unchanged, every FixedUpdate)
  └─ Landmine.ProcessCollidersInWarningRange / OnCollisionEnter
       └─ ServerExplode()
            └─ LandmineExplodePatch prefix: TryGetComponent<ShockMarker>?
                 ├─ no                     → return true, vanilla explosion
                 ├─ yes, window exists     → return false (already sprung)
                 └─ yes                    → ShockSeizeWindow.Begin(mine); return false

ShockSeizeWindow.FixedUpdate  (server only, lives on the mine's GameObject)
  ├─ past endTime?  → Finish()
  ├─ Catch(): OverlapSphereNonAlloc(mine, LandmineExplosionRange, LandmineHittablesMask)
  │    └─ per eligible Hittable  (not the mine, not held, not traffic, has rigidbody,
  │                               not already frozen, not a teammate of the owner)
  │         ├─ golf cart?  Evacuate: ExitGolfCart(Knockout), seize each passenger
  │         └─ Seize:
  │              ├─ ShockVictims.MarkHeld(netId)          ← before the SyncVar, see below
  │              ├─ NetworkfrozenState = Frozen           → ice block built, then hidden
  │              ├─ ShockSeizedMessage → zap VFX, clap, hum, shake, countdown
  │              └─ InfoFeed.ShowItemHitMessage(owner, victim, ShockItem.Type)
  └─ HoldStill(): zero linear + angular velocity on every held body the server
                  simulates (skips kinematic ones — remote players)

ShockLocalVictim.FixedTick  (every client, via ModRunner.FixedUpdate)
  └─ local player held?  zero their linear + angular velocity

Finish()
  ├─ per victim: frozenState = None (only if still Frozen by us)
  │              EliminationReason.Landmine credited to the planter
  │              ShockReleasedMessage(victim, owner, useId, minePosition)
  │                → every client: stop zap, hum, countdown
  │                → victim's own client: ShockLocalVictim.QueueKnockout
  │                     wait until frozenState != Frozen (≤ 3 s)
  │                     CancelKnockoutImmunity  (the thaw immunity, finding 9a)
  │                     TryKnockOut(owner, KnockoutType.Landmine, no impulse)
  └─ mine.AsEntity.DestroyEntity()
```

The hold itself reaches clients through the game's `frozenState` SyncVar. Our messages carry only
what clients cannot infer: that the freeze is ours (so `ShockVictims` is maintained on every
client and the ice block is hidden there too), and the countdown.

### Identifying our mines

The clone's `PhysicalItem.itemType` is `ShockItem.Type`, but the patches run on the `Landmine`
component, which real landmines carry too. A marker component on the prefab is the cheaper and
more explicit check:

```csharp
// on the cloned prefab, so every networked copy carries it
internal sealed class ShockMarker : MonoBehaviour { }

// in the ServerExplode prefix
if (!__instance.TryGetComponent<ShockMarker>(out _)) return true;   // a real landmine
```

The prefix skips the landmine's own one-shot guards (`isExploded`, `DestroyEntity`), so the
presence of a `ShockSeizeWindow` on the mine *is* the "already sprung" check.

## Risks

| Risk | Why it matters | Mitigation |
|---|---|---|
| A game structure indexed by `ItemType` | Exactly what defeated the first Switcheroo pool attempt: a type of 201 reads past the end and throws | Audited before gameplay code was written (see History); `itemOrderLookup` rebuilt; pool injection falls back to a draw-time pick if it throws |
| A game update rewrites the landmine's use routines | `ShockUse.cs` mirrors them line for line | Read timings and effects from `ItemSettings` at runtime; re-diff the mirrors against `PlantLandmineRoutine` / `TossLandmineRoutine` after an update |
| A game update adds or moves an `ItemType.Landmine` special case | The Shock Mine silently stops following that rule, as it did with the movement locks until 0.4.0 | After an update, search the decompiled game for `ItemType.Landmine` and compare against finding 7's table. Local-function patches are found by name prefix and disable themselves with a warning (`Prepare`) rather than breaking `PatchAll` |
| Handing a player's physics to the wrong peer | Server writes to a remote player's body do nothing; client writes to a body it does not simulate fight the server | Server holds non-kinematic bodies only; each client holds only its own local player |
| Ice block state leaking to freeze bombs | Blocks are pooled; a hidden one handed to the next freeze-bomb victim would be invisible | Hiding scoped to `ShockVictims`; restored on `WillBeReturnedToPool`, not on our release message |
| SyncVar and message arriving out of order | A remote client can see `frozenState` before our seized message | The seized handler hides any ice block that already exists; the host marks the victim held before setting the SyncVar |
| A mine tripping twice | The prefix skips the landmine's own guards | The window component is the guard; `Finish` destroys the mine |
| Game update renumbers items or rebalances pools | Hardcoded weights and indices rot | Weights read from the pool at runtime (`GetSpawnChanceWeight(ItemType.Landmine)`), never written down |
| Mirror or the game drops a registration | Mirror clears registered prefabs and handlers on client shutdown; the Switcheroo hit this exactly | Every registration is an idempotent `Ensure*` re-asserted from `ModRunner.Update`, cheap and silent |
| Two mods claiming the same id | BallSwapItem owns 200 | This mod takes 201 / Rule 202 (201 retired) |
| Frozen balls in flight | `FrozenState` on a `GolfBall` may not arrest a ball mid-trajectory | Velocity zeroing covers anything with a rigidbody; still the lowest-confidence victim type, worth watching in play |

## Acceptance criteria

These are the test checklist for a play session. 0.1.1 confirmed the mod loads, registers and
injects cleanly. The 0.2.0 and 0.3.0 fixes came out of in-game play. The first two-player
session (against 0.3.0) found the knockout, remote-hold and deploy-lock gaps fixed in 0.4.0.
Nothing has been played on 0.4.0 yet.

### Deploying
1. Planting stops the player dead: no walking until the mine is in the ground.
2. No jumping or turning while planting or tossing; a toss still aims with the camera.
3. A mine cannot be planted in mid-air, and leaving the ground mid-plant cancels it and keeps
   the item.
4. Lobby / driving-range use is refused with a notice and does not consume the item.

### Logic
1. A mine trips only when a player moving above the landmine's minimum speed enters
   `LandmineDetectionRange`, or on contact within `LandmineCollisionRange`.
2. On tripping, every eligible target inside `LandmineExplosionRange` is held completely still
   where it stands: no sliding, no falling. This holds for **remote players on their own
   screen**, not just for the host.
3. The hold lasts the host-set duration (default 5s) measured **from the first trip**; anything
   entering during the window is caught and released with everyone else.
4. The mine is destroyed when the window closes and cannot trip again.
5. In Teams mode the owner's teammates are never caught; the owner themselves is.
6. A golf cart driven into a mine is emptied; each passenger is thrown clear and seized
   individually.
7. On release every held player — host and remote alike, including one who tripped their own
   mine — is knocked down where they stood, with the knockout animation, and the planter is
   credited (`EliminationReason.Landmine`). Recovery takes the game's standard knockout time.
   The trace shows `client: local player drops over (knocked=True ...)` on each victim's machine.
8. No ice block is visible on a victim, and a freeze bomb used afterwards still shows its ice
   block. No `NullReferenceException`s and no "kinematic body" warnings appear during a seize.
9. Shooting a Shock Mine (railgun, shotgun, lightning) sets it off, as it does a landmine: a
   visible lightning strike and thunderclap at the mine, catching anyone in range. It is not
   deleted without discharging. With nobody in range it is spent at once
   (`discharge caught nothing, mine spent at once` in the trace) rather than lingering.
10. Picking up someone else's armed Shock Mine shows the "disarmed" notice; picking up your own
    does not.

### Cosmetic
1. The 3D device is a tinted landmine, both in hand and once placed, and mimics all of the
   landmine's hold, plant and toss animations.
2. The 2D icon is distinct from the landmine's, and the item's name reads "Shock Mine", not
   `Data/ITEM_201`.
3. A held player plays the lightning-in-a-bottle zap for the full duration, on every client. It
   tracks the victim, does not pile up, and leaves no trail.
4. A thunderclap and camera shake land on being caught; the hum plays for the hold and stops on
   release.
5. The seized player sees a countdown.
6. The info feed names whoever planted the mine.

### Misc
1. The mine has its own probability slider in the match setup, next to the game's items, and
   appears on the Escape screen with them.
2. Its default weight equals the Landmine's, in every pool the Landmine is in (currently: Close,
   at 15).
3. The "Seize duration" row appears in the Battle section reading 3 / 5 / 7 / 9 (not On/Off),
   defaults to 5, and survives a lobby rebuild, a preset change and the reset button.
4. An unmodded client is disconnected from a lobby running this mod within 10 seconds of
   authenticating, with a clear message, unless the host has turned `BlockUnmodded` off.
5. Nothing (VFX, sound, UI, victim set) leaks into the next lobby after a disconnect.

## History

### Phase 0 audits (0.1.x)

Before any gameplay code, the three things that could have forced a redesign were checked. Each
is the same class of problem as the `itemOrderLookup` array that defeated an earlier Switcheroo
attempt: a game structure sized to the game's own item list that a type value of 200+ reads past
the end of. **All cleared:**

- `PlayerInventory.IncrementAndGetCurrentItemUseId` is `new ItemUseId(guid, ++latestItemUseIndex,
  itemType, false)`: one counter, no per-item array.
- `CourseManager.ServerSpawnItem` resolves through the `allItemData` dictionary and
  `itemData.Prefab`; nothing is indexed by `ItemType`.
- `ItemTypeExtensions.TryGetEliminationReason` is a switch whose `default` arm returns `None`.
  (The mine now sets `EliminationReason.Landmine` itself on release.)
- `_Write_ItemType` and friends are `WriteVarInt`, so 201 survives the wire.

### Where the build departed from the original plan

| Originally planned | Built | Why |
|---|---|---|
| Named **Grappler Trap** (0.1.x) | **Shock Mine** (0.2.0); new GUID and package, 0.1.x abandoned | Rename across namespace, GUID, package and strings |
| A Mirror `[Command]` / `[ClientRpc]` pair | Plain registered `NetworkMessage`s with hand-written serializers | `[Command]`/`[ClientRpc]` need Mirror's weaver, which a BepInEx mod cannot run |
| Looped Thunderstorm zap sound | Thunderclap on catch, Electromagnet shield hum for the hold | The game has no looping zap event; four claps over five seconds is unbearable |
| No knockout, no elimination credit | Knocked out via `TryKnockOut` on the victim's own client after cancelling the thaw immunity, credited to the planter by the server (0.2.0; reworked 0.3.0 and 0.4.0) | The hold should pay off. 0.2.0 used `ServerInformKnockedOut`, which only reports a knockout. 0.3.0 called `TryKnockOut` on the server, which the thaw immunity blocked for the host and which never reached a remote player's screen |
| Snap pull toward the mine on being caught | Removed (0.4.0); victims freeze where caught | The hold zeroed velocity in the same tick, so it moved victims ~12 cm at most |
| Server holds every victim | Server holds bodies it simulates; each player's own client holds them (0.4.0) | A remote player's body is kinematic on the host |
| Use routines mirrored; three `PlayerInventory` gates | Plus every other `ItemType.Landmine` special case: movement locks, vaporize immunity, rocket trail, disarm notice (0.4.0) | The landmine's halt lives in `PlayerMovement`, not the routine, so planting a Shock Mine did not stop the player |
| Hold = `FrozenState`, velocity zeroed once | All linear and angular motion cancelled every physics tick (0.2.0) | Frozen bodies still slid and fell |
| Carts held with passengers inside | Passengers ejected, then seized individually (0.2.0) | Passengers were getting no shock at all |
| Ice block suppressed | Built, then hidden (0.3.0) | Suppression caused ~900 NREs per session in `PlayerMovement.UpdateFrozenState` |
| Info feed via `HitInfoFeedMessageIcon` borrowed from the landmine | `HitInfoFeedMessageIcon = InfoFeedIconType.Landmine` set explicitly | The landmine's own is `None`: it credits through `ShowEliminationMessage`, so there was nothing to borrow |
| Duration 3 / 5 / 7 / 10 | 3 / 5 / 7 / 9 (0.2.0) | Host's preference |
| "One trap per hole" host rule (`Rule` 201) | Removed (0.3.0) | Mines are limited only by how often they are found |
| Held model not considered | Own `EquipmentType` with a tinted hand model (0.2.0) | The item was invisible in hand, which made planting look broken |
| Row's `LocalizeDropdown` destroyed | Left alive, silenced for our row via a `RefreshOptions` prefix (0.3.0) | `Destroy` was deferred and repopulated On/Off; `DestroyImmediate` then broke `DropdownOption`'s serialized reference to it |
| A fresh pooled zap per VFX cycle | One pooled zap per victim, parented, `registerWhenFinished: false` (0.2.0) | The zap never went away, piled up and left a trail |
| `EnsureNetworkPrefabRegistered` registers every frame | Checks `NetworkClient.prefabs` first (0.1.1) | Mirror warns on each replacement: 8,702 warnings in one session |

Two races found in review and fixed before 0.1.1: a victim must be recorded as ours **before**
`frozenState` is set (the SyncVar hook builds the ice block synchronously on the host), and a
remote client can receive the SyncVar before our message, so the seized handler deals with any
ice block that already exists.
