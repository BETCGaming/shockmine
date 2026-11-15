# ShockMine

Adds the **Shock Mine** to Super Battle Golf: a pickup built from the game's landmine that,
instead of exploding, shocks everyone it catches and holds them rigid — then drops them.

Plant it or toss it exactly as you would a mine. Planting stops you in your tracks until it is in
the ground, and you cannot jump or turn while planting or tossing, just as with a landmine. It
arms the same way, blinks the same warning, and trips on the same proximity. Shooting one sets
it off just as it would a landmine — you will see and hear it discharge — and picking up
someone else's armed one counts as disarming it.

What happens next is different: anyone caught is pinned exactly where they stand, zapping like
they have been struck by lightning. When the shock finally stops, they fall over.

## How the mine works

The Shock Mine keeps the landmine's split between where it trips and what it catches. It trips
when a player moving at speed comes inside the mine's detection range, or on contact. It then
seizes everything inside the mine's **explosion** range, which is wider — so being just outside
where you tripped it does not save you.

The hold runs on **one shared timer from the first trip**. Players who wander in while it is
still running are caught too, and everyone is released together when the timer ends. A mine is
spent after one window.

While held you are completely still — not slowed, stopped. Caught in mid-air, you hang there
until it lets go. When the shock ends you are knocked out and fall over, and whoever planted the
mine is credited: if you are still out of the running when the hole resolves, that one is theirs.
The fall is an ordinary knockout, so you get up on the game's usual recovery timer.

Driving a cart into one does not help. The cart's passengers are thrown clear on the game's own
knockout exit and shocked where they land.

In Teams mode a mine spares the planter's teammates, the way a landmine does. It does not spare
the planter.

## Requirements

**Every player in the lobby needs this mod.** The Shock Mine is added to the item pool, which
changes data the host and clients agree on, so an unmodded player cannot take part.

Installing it also resets your saved item-probability settings once, because adding an item
changes the pool hash the game stores them against.

## Configuration

| Key              | Default | Description                                                        |
| ---------------- | ------- | ------------------------------------------------------------------ |
| `Enabled`        | `true`  | Registers the item. With this off the mine never appears.          |
| `SoundVolume`    | `0.8`   | Volume of the zapping loop played while a player is held.          |
| `BlockUnmodded`  | `true`  | Host only: disconnect players without the mod.                     |
| `DebugHotkeys`   | `false` | Host-only testing keys: F7 grants a mine, F8 springs the nearest.  |
| `VerboseLogging` | `false` | Log every step of planting, tripping and releasing.                |

How long the shock lasts is **not** a config setting. It is a **Shock Mine seize duration** row
in the match setup's Battle section — **3, 5, 7 or 9 seconds**, default 5 — set by the host per
match. There is no limit on how many mines a hole may see — they are limited only by how often
they are found.

How often the mine spawns is **not** a config setting either. It has its own slider in the match
setup's item probabilities, alongside the game's own items, and ships at the same rarity as the
landmine in every pool the landmine appears in.

[ModConfig](https://thunderstore.io/c/super-battle-golf/p/AtomicStudio/ModConfig/) is supported
for editing these in-game; no extra setup is needed.

## Building

Requires the .NET SDK 10 and a local copy of the game.

```sh
dotnet build -c Release -v d
```

The Thunderstore package is written to `artifacts/thunderstore/`. Copy
`Config.Build.user.props.template` to `Config.Build.user.props` first if the game is not at the
default Steam path; with it in place, builds deploy straight into the game's `BepInEx/plugins/`.
