# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A BepInEx 5 plugin for **Super Battle Golf** (Unity Mono, Mirror networking, host-as-server) that
adds the **Shock Mine**: the game's landmine cloned into a new item that, instead of exploding,
freezes everyone in its blast radius for a host-configured window and then knocks them down.

`README.md` is the player-facing description of the mechanic. `plans/shock-mine-plan.md` is the
design document — read its "Findings from the decompiled game that shaped the design" and
"Architecture" sections before changing how the mine spawns, trips or seizes; it records the game
internals the design depends on. It describes the design as built (current to 0.3.0); its
"History" section records where the build departed from the original plan. Keep both it and
`CHANGELOG.md` in step when the design changes. `CHANGELOG.md` records *why* several non-obvious workarounds
exist and is worth reading before "simplifying" them.

The sibling repo `~/workbench/superbattlegolfmod` (BallSwapItem / Switcheroo) is the reference mod
this one was ported from. Every hard problem in registering a modded item for this game is solved
there first; check it before inventing a new approach.

## Build and deploy

```sh
dotnet build -c Release -v d       # builds, deploys to BepInEx/plugins, packs for Thunderstore
dotnet build                       # Debug: deploys only, no Thunderstore package
```

- There are **no tests**. Verification is in-game: build, launch, read the BepInEx console. The
  `VerboseLogging` and `DebugHotkeys` config entries exist for exactly this (F7 grants a mine,
  F8 trips the nearest one; host only).
- A Release build runs `PackTS` automatically, writing `artifacts/thunderstore/`. Add
  `-property:PublishTS=true` to publish via `tcli`. To build a package without the deploy loop:
  `dotnet build -c Release -target:PackTS -v d`.
- `Config.Build.user.props` (gitignored, copy from the `.template`) sets
  `SuperBattleGolfGameRootDir` / `SuperBattleGolfBepInExDir` and `DeployModFilesEnabled`. This
  checkout points at a Thunderstore Mod Manager profile, not the Steam folder, because the mod is
  tested through the mod manager.
- The game's code is in **`GameAssembly.dll` and `SharedAssembly.dll`** — there is no
  `Assembly-CSharp.dll`. Both are publicized at build time (`BepInEx.AssemblyPublicizer`), so
  internal and private members are reachable directly; reach for reflection only for
  compiler-generated locals and genuinely inaccessible members. Without a local game install the
  build falls back to NuGet `UnityEngine.Modules` and will not compile against game types.
- `Version` lives in `src/ShockMine/ShockMine.csproj` and is what `tcli` packages; bump it
  alongside `CHANGELOG.md`.

## Architecture

The plugin is a set of Harmony patches plus one `MonoBehaviour`. There is no central manager: each
file owns one integration point with the game.

**Entry and polling.** `Plugin.cs` binds config, registers Mirror serializers, calls `PatchAll`,
and adds `ModRunner`. `ModRunner.Update` is the plugin's only per-frame heartbeat and re-asserts
every registration the game or Mirror tears down on its own schedule (network handlers, network
prefab, equipment type, localization entry). Anything named `Ensure*` is idempotent by design and
called from there — and must stay cheap and silent, because it runs every frame (an early version
logged 8,702 Mirror warnings in one session).

**Reserved modded ids**, all deliberately far above the game's own ranges so a game update cannot
collide, and all chosen to coexist with the Switcheroo mod's 200:

- `ShockItem.Type` = `(ItemType)201`
- `ShockEquipment.Type` = `(EquipmentType)201` — the model held *in hand*, a separate system from
  `ItemData.Prefab`
- `ShockItem.SeizeDurationRule` = `(MatchSetupRules.Rule)202`. `Rule` 201 belonged to the
  "One Shock Mine per hole" rule removed in 0.3.0; leave it unused so a host's saved value for
  it is never read as something else.
- `NetworkAssetId` = `0xB5A50101` — fixed so every client resolves the same prefab

**The item.** `ShockItem.cs` clones the landmine's `ItemData` field-by-field (so a game update
adding fields keeps working) and clones its prefab, keeping the game's `Landmine` component. That
component is the whole design: `LandmineManager`'s job system then drives arming, the warning
blink and proximity detection for free. Because real landmines carry it too, every Landmine patch
tells ours apart with `TryGetComponent<ShockMarker>` — a marker component on the prefab, so every
networked copy has it.

**The use path.** `ShockUse.cs` prefixes `PlayerInventory.TryUseItem`, because the game's
dispatcher (`GetItemUseRoutine`, a compiler-generated local function) throws
`ThrowSwitchExpressionException` for an unknown `ItemType`. It runs line-for-line mirrors of
`PlantLandmineRoutine` / `TossLandmineRoutine` that read timings, VFX and audio from the game's
own settings, and refuses use on the driving range. `ShockLandmineGatePatches` in the same file
re-applies three `ItemType.Landmine` special cases in `PlayerInventory` (no mid-air planting,
aim-swing-held selection) that a modded type otherwise slips past. When the game changes how
the landmine is planted, these mirrors are what need updating.

`LandmineParity.cs` covers every *other* `ItemType.Landmine` special case in the game, most
importantly the deploy locks in `PlayerMovement` (no move while planting, no jump or turn while
planting or tossing) — the landmine's halt is not in its plant routine. After a game update,
grep the decompiled game for `ItemType.Landmine` and compare against the table in the plan's
finding 7. Patches on compiler-generated local functions find their target by name prefix
and use `Prepare()` to switch themselves off with a warning if it is missing; a null
`TargetMethod` would otherwise throw out of `PatchAll` and take every patch with it.

**The divert.** `LandminePatches.cs` prefixes `Landmine.ServerExplode` — the single point both the
proximity and contact trip paths funnel into — and starts a `ShockSeizeWindow` instead. Nothing
about detection is reimplemented. The prefix skips the landmine's own one-shot guards, so the
presence of the window component *is* the "already sprung" check.

**The seize.** `ShockSeize.cs` (`ShockSeizeWindow`) is server-only and lives on the mine's own
GameObject, so a destroyed mine takes its window with it. Each `FixedUpdate` it overlaps
`LandmineExplosionRange` (wider than the trip radius), catches newcomers, and zeroes the linear
and angular velocity of every victim **it simulates** — `FrozenState` alone only changes
damping and gravity. `ShockVictims` is the static set of held netIds, maintained on **every**
client, because a client cannot otherwise tell our freeze from a freeze bomb's.

**Who owns a player's physics.** A player is simulated by their own client; on the host a
remote player's rigidbody is kinematic. So anything that moves or knocks down a *player* runs on
that player's machine, in `ShockLocalVictim.cs`: it holds the local player still each physics
tick and, on the release message, waits for the thaw, cancels the thaw immunity the game grants
on unfreezing, and calls `TryKnockOut`. `TryKnockOut` is a local-player API; called from the
server it is refused for the host (thaw immunity) and never reaches a remote player. The server
keeps the decisions (who, when, the elimination credit) and holds only non-kinematic bodies.
Test anything in this area with **two players**: host-only testing hides all of it.

**Networking.** A BepInEx mod cannot run Mirror's weaver, so there are no `[Command]`/`[ClientRpc]`
attributes: `ShockNetwork.cs` uses plain registered `NetworkMessage`s, and `MirrorSerializers.cs`
fills in `Writer<T>.write` / `Reader<T>.read` by hand. Omitting a serializer fails at runtime with
"No writer found for ...". The hold itself replicates through the game's existing `frozenState`
SyncVar; our messages only carry what clients cannot infer (who froze them, and the countdown).
The spawn message is validated server-side with the game's own
`ServerValidateAndConsumeItemUse(useHash)` plus a per-connection rate limit.

**Pools and the match setup screen.** `ShockPool.cs` injects the item into every pool that already
holds a landmine, at the landmine's own weight, into both the runtime copies and the backing
assets (the screen reads its reset defaults from the assets). `ShockRulesScreen.cs` must run
first: the screen maps an item to its slider via `itemOrderLookup[(int)itemType - 1]`, an array
the editor sizes to the game's item count, so it is rebuilt long enough to be indexed by 201. If
either step throws, `ShockPool.UseFallback` withdraws the injection and the item is instead picked
inside `ItemPool.GetWeightedRandomItem` at the weight it would have had — it still spawns, but
loses its slider. Adding an item changes the pool hash the game stores item probabilities against,
which is why installing the mod resets saved probabilities once.

**Host rules.** `ShockRules.cs` reads the seize duration (a dropdown *index* into
`DurationOptions`, not a seconds value) and patches `GetDefaultValue`, whose default arm would
otherwise return 0. `ShockRuleRows.cs` clones an existing Battle-section row at runtime, since
rule rows are authored in the scene rather than generated.

**Presentation**, all client-side and all keyed by victim netId: `ShockVfx.cs` (one pooled particle
system per victim, parented to them, `registerWhenFinished: false`), `ShockAudio.cs` (thunderclap
on catch, Electromagnet shield hum for the hold — the game ships no looping zap), `ShockUi.cs`
(TMP countdown and refusal notices on our own canvas). Each exposes a `Reset`/`Clear` called from
`ShockNetwork.EnsureHandlers` on disconnect, so nothing leaks into the next lobby.

**`ModGate.cs`** disconnects clients that do not send a `ShockHelloMessage` within 10 seconds of
*authenticating*. Required, not optional politeness: the host can hand any player an `ItemType` an
unmodded client cannot resolve. It is on by default and gated on the host's `BlockUnmodded`
config entry; clients ignore that entry.

`ShockLocalization.cs` adds the `ITEM_201` entry to the Data string table (otherwise the UI
prints `Data/ITEM_201`). It is re-applied after every language change, because each locale
has its own table. The icon is an embedded resource (`Assets/`), so the plugin ships as a
single DLL.

## Conventions that matter here

- `Nullable` is enabled and `AnalysisMode` is `recommended`; the existing code is
  annotation-clean. Note that Unity's `== null` on a destroyed object is true while the reference
  is not actually null, which is why `MonoBehaviour` fields are `= null!` and compared with
  `== null`.
- Every patch and runtime mutation checks `Plugin.Enabled.Value` and degrades rather than
  throwing: a half-registered item hands the game an unresolvable `ItemType`. Prefer "log loudly
  and fall back" over an exception, and set a `failedOnce` latch so a failure is not retried every
  frame.
- `Trace.Log` for step-by-step gameplay tracing (gated on `VerboseLogging`); `Plugin.Log` for
  anything a player or bug report should see.
- The comment style is unusual and deliberate: every workaround carries a paragraph on what was
  tried first and why it failed. Match it. These comments are the only record of game internals
  that are not visible from this repo — do not strip them when editing nearby code.
