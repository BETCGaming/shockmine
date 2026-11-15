# Changelog

## 0.4.1

**Every player needs 0.4.1**: it adds a network message that 0.4.0 does not know.

### Fixed

- **A shot mine silently vanished a few seconds later.** Shooting a Shock Mine (railgun,
  shotgun, lightning) sets it off, as it does a landmine: the landmine's own hit handlers call
  `ServerExplode`. With nobody in range nothing was caught, and the zap only ever showed on
  victims, so the mine gave no sign it had gone off. It then sat out its full window and
  disappeared. Every discharge now shows at the mine itself — a lightning strike and a
  thunderclap on every client. A discharge that catches nobody on its first sweep is spent
  immediately, as a landmine's explosion is.

## 0.4.0

**Every player needs 0.4.0.** The release message gained fields, so a 0.3.0 client cannot read
it from a 0.4.0 host, or the reverse.

### Changed

- **Deploying follows the landmine's rules.** Planting halts you until the mine is in the
  ground, and you cannot jump or turn while planting or tossing. 0.3.0 mirrored the plant
  routine line for line, but the landmine's halt is not in the routine: it lives in
  `PlayerMovement.CanMove`, `CanJump` and `ApplyRotation`'s `CanRotate`, which special-case
  `ItemType.Landmine` and never matched a modded type.
- **The rest of the landmine's special cases now apply too**, found by searching the game for
  every `ItemType.Landmine`:
  - Lightning strikes and railgun shots no longer delete a Shock Mine outright, as they spare
    a landmine. The hit still sets it off, as it sets off a landmine.
  - A planted Shock Mine struck by a rocket-driver swing no longer draws the rocket trail.
  - Picking up someone else's armed Shock Mine shows the game's "disarmed" notice.
- **No more pull toward the mine.** Victims freeze where they are caught. The 6 m/s "snap" never
  did anything visible: the hold zeroed velocity in the same physics tick, so it moved a victim
  ~12 cm at most.

### Fixed

- **Nobody fell over when the shock ended.** The 0.3.0 knockout was called on the server and
  failed both ways. For the host it was refused every time, because unfreezing a local player
  starts the game's thaw immunity synchronously, and that blocked the knockout on the next line.
  For a remote player it succeeded on the server's copy only, threw "Only the local player is
  allowed to remove their own status effects", and never reached their screen:
  `TryKnockOut` is a local-player API. The release message now carries the planter, the use id
  and the mine's position. The victim's own client waits for the thaw, cancels the thaw
  immunity, and knocks itself down. The server still credits the planter.
- **Remote players were not held still.** On the host a remote player's rigidbody is
  kinematic, because their own client simulates them, so the server's velocity zeroing did
  nothing but log "Setting linear velocity of a kinematic body is not supported" twice a tick
  (500 in one five-second seize). Each player's own client now holds them; the server holds only
  the bodies it simulates.

### Known

- Leaving a match with a mine still armed logs ~30 `NullReferenceException`s from
  `LandmineManager.FixedUpdate` for a few frames. This is the game's own code: it skips
  deregistering mines while shutting down, so by the code it should happen with an armed real
  landmine too (not yet confirmed in play). It is harmless and is left alone on purpose.

## 0.3.0

### Fixed

- **900 NullReferenceExceptions per session, from suppressing the ice block.** The game's own
  frozen-player code reads `PlayerInfo.AsHittable.IceBlock.Collider` with no null check, every
  physics tick, in `PlayerMovement.UpdateFrozenState`. Preventing the block from existing threw
  on every tick of every seize and left `FrozenPlayerState` never computed. The block is now
  built exactly as the game expects and then hidden — renderers off, collider disabled — which
  the grounding boxcast tolerates because it only reads the collider's size and transform.
  Restoration hangs off the block's own `WillBeReturnedToPool` event, so a pooled block is never
  handed to the next freeze-bomb victim invisible.
- **Two more NREs, from destroying the row's localizer.** 0.2.0 fixed the On/Off dropdown with
  `DestroyImmediate` on `LocalizeDropdown`, but `DropdownOption` keeps a serialized reference to
  it and calls `RefreshOptions` through it unguarded, so `InitDropdown` threw. The component is
  now left alive and silenced for our row only, via a prefix on `RefreshOptions`.
- **No knockout animation on release.** `ServerInformKnockedOut` only *reports* a knockout to
  the feed and scoring — it never touches `knockoutState`. Releasing now goes through
  `TryKnockOut`, which actually knocks the player down and brings the immunity and
  teammate-protection checks with it. No impulse: victims crumple where they were held, and
  recovery is the game's standard knockout duration.

### Removed

- The **One Shock Mine per hole** host rule, along with its lock message, hole hook and
  use-time refusal. Mines are limited only by how often they are found.

## 0.2.0

Renamed from GrapplerTrap to **ShockMine**: namespace, plugin GUID, Thunderstore package,
repository folder and every player-facing string. Testers install this as a new mod rather than
an upgrade; the 0.1.x package is abandoned.

### Fixed

- **The zap never went away, piled up, and left a trail.** One root cause: the effect was driven
  by pulling a *fresh* pooled particle system each time the previous one ran out, never stopping
  the old one, and the pool reclaimed instances mid-hold because `Play` defaults to
  `registerWhenFinished: true`. Now one pooled effect per victim, parented to the victim so it
  tracks them, replayed on the same instance, and explicitly stopped, unparented and returned on
  release.
- **The seize duration row read On/Off instead of durations.** `Object.Destroy` on the row's
  `LocalizeDropdown` is deferred to end of frame, so the component was still alive and
  repopulated On/Off immediately after the durations were set. Now `DestroyImmediate`, and the
  options are re-asserted on every visit to the screen before the stored value is applied.
- **Players in golf carts got no shock.** A cart is now emptied on being caught: passengers are
  thrown clear on the game's own knockout exit and seized individually, each with their own zap,
  countdown and release.
- **Held players were not really held.** `FrozenState` only changes a body's damping and
  gravity, so victims slid and fell. All linear and angular motion is now cancelled every
  physics tick for the duration.
- **The mine was invisible in hand**, which also made the planting animation look broken — the
  character went through the landmine's motions holding nothing. Held models come from a
  separate `EquipmentType` system that falls through to `None` for an unrecognised item; the
  mine now registers its own, carrying a recoloured clone of the landmine's hand model, so what
  you carry matches what you place.

### Changed

- Seize duration options are now **3, 5, 7, 9** seconds (was 3, 5, 7, 10), default 5.
- When the shock ends the victim is **knocked out and falls over**, and the planter is credited
  with `EliminationReason.Landmine`. This reverses the earlier "no elimination credit" decision:
  a Shock Mine can now cost someone the hole.

## 0.1.1

First build shared for testing.

Phases 0-5 of `plans/grappler-trap-plan.md` are implemented. The mod loads cleanly in-game:
item registered as ItemType 201, both match rules added to the Battle section, pool injection
done, no errors. Gameplay itself - planting, tripping, seizing - is still untested.

### Phase 0 - audits

All three risky assumptions cleared, so the design stands as planned:

- `PlayerInventory.IncrementAndGetCurrentItemUseId` is one counter plus a struct field, not a
  per-item array, so a modded `ItemType` passes straight through.
- `CourseManager.ServerSpawnItem` resolves through the `allItemData` dictionary and
  `itemData.Prefab`; nothing is indexed by `ItemType`.
- `ItemTypeExtensions.TryGetEliminationReason` is a switch with a `default` arm returning
  `EliminationReason.None` - which is the "no elimination credit" the trap wants anyway.
- Also checked: `_Write_ItemType` and friends are `WriteVarInt`, so type 201 is safe on the wire.

### Added

- The Grappler Trap as `ItemType` 201, cloned from the landmine's item data and prefab, tinted
  steel blue, with an embedded icon and its own Mirror asset id.
- Plant and toss use routines mirroring `PlantLandmineRoutine` / `TossLandmineRoutine`, with the
  three `ItemType.Landmine` handling gates ported so the trap cannot be planted mid-air.
- A seize that diverts `Landmine.ServerExplode`: one shared window from the first trip, catching
  anything that enters the mine's explosion radius, held by `FrozenState.Frozen` with the ice
  block suppressed, released together, then the trap is spent.
- A grapple snap toward the trap as a victim is caught, and horizontal velocity cancelled once a
  held player has settled so they cannot slide out.
- The lightning zap looped over each victim for the hold, the thunderclap on being caught, an
  electrical hum for the duration, a camera jolt and an on-screen countdown for the player held,
  and an info-feed line crediting whoever planted it.
- Item pool injection at the landmine's own weight in every pool the landmine is in, with the
  `itemOrderLookup` rebuild that gives the trap its own probability slider, and a draw-time
  fallback if either fails.
- Two Battle-section match rules: One Grappler Trap per hole, and Seize duration (3/5/7/10s,
  default 5).
- `ModGate`, keeping unmodded clients out of a lobby whose item pools hold a type they cannot
  resolve.

### Found by the first in-game run

- The landmine's item data has `HitInfoFeedMessageIcon = None`: the mine credits itself through
  `EliminationReason.Landmine` and `ShowEliminationMessage`, not through `ShowItemHitMessage`.
  A seize eliminates nobody, so the trap takes the other route and now sets
  `InfoFeedIconType.Landmine` explicitly, or no seize would ever be credited.
- `EnsureNetworkPrefabRegistered` ran every frame and Mirror logs a warning on each replacement -
  8,702 of them in one session. It now checks `NetworkClient.prefabs` before registering.
- The landmine appears in **only** the Close item pool, so "wherever the mine goes" currently
  means the trap spawns only from close-range boxes, at weight 15. Working as specified, but
  worth a decision: the Switcheroo, which injects unconditionally, reaches five pools.

### Notes

- Networking uses plain registered `NetworkMessage`s rather than `[Command]`/`[ClientRpc]`: a
  BepInEx mod cannot run Mirror's weaver.
- The game ships no looping zap sound, so the hold borrows the Electromagnet shield's hum and the
  thunderclap marks the catch.
