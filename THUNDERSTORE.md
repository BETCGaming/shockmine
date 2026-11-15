# Shock Mine

**A landmine that doesn't blow you up. It's worse.**

The Shock Mine is a new item for Super Battle Golf. Plant it like a landmine and wait. When it
goes off, it doesn't throw anyone across the course: it **locks everyone nearby in place**,
crackling with lightning, for a few long seconds. Then it **drops them flat on their face**.

Set one by the green, on the cart path, or right where your rival is about to line up a putt.

---

## What it does

- **Freezes everyone in the blast.** Anyone caught is held completely still. Not slowed,
  stopped. Caught mid-jump, you hang there in the air until it lets go.
- **Zaps them for everyone to see.** Victims crackle with the Thunderstorm's lightning for the
  whole hold, with a thunderclap when they're caught and an electric hum until they're let go.
- **Knocks them down.** When the shock ends, every victim goes down with a full knockout and
  gets up on the game's usual recovery timer.
- **Gives the planter the credit.** The kill feed names whoever planted it. If a shocked player
  is still out of the running when the hole resolves, that one is theirs.
- **Keeps catching while it runs.** The shock lasts for one window, started by the first
  person to trip it. Anyone who wanders in while it's still crackling gets caught too, and
  everyone is released together.
- **Empties golf carts.** Driving into one doesn't save you: passengers are thrown clear and
  shocked where they land.

## Plays just like a landmine

If you know the landmine, you already know the Shock Mine. It's built from the landmine, so it
plays by the same rules:

- **Plant it or toss it.** Planting stops you in your tracks until it's in the ground. You
  can't jump or turn mid-plant or mid-toss, and you can't plant in mid-air.
- **Arms the same way**, blinks the same warning, and trips on the same proximity: walk into it
  at speed, or bump right into it.
- **Catches wider than it trips.** It's triggered at close range but shocks everything inside
  the landmine's full blast radius, so stopping just short doesn't save you.
- **Can be shot.** Hit one with a railgun, shotgun or lightning and it discharges on the spot,
  catching anyone standing too close.
- **Can be disarmed.** Pick up someone else's armed Shock Mine and you've disarmed it, notice
  and all.
- **Spares your teammates, not you.** In Teams mode it won't catch the planter's teammates,
  just like a landmine. It will catch the planter.

Shock Mines are recoloured an electric blue, in hand and once placed, with their own icon, so
you can tell them apart from a regular landmine before it's too late.

## Host settings

Everything is set by the host in **match setup**. No config files needed:

- **Shock Mine seize duration**: a new row in the Battle section. Choose **3, 5, 7 or 9
  seconds** (default 5).
- **Spawn rate**: the Shock Mine gets its own slider in the item probabilities, next to the
  game's own items. It starts at the same rarity as the landmine, in every pool the landmine
  appears in.

## Requirements

- **Everyone in the lobby needs the mod.** The Shock Mine is added to the item pool, which the
  host and every player have to agree on. Players without it are disconnected with a message
  saying why, so nobody ends up in a broken match. Hosts can turn this off.
- **Everyone needs the same version.** Updates can change how the host and players talk to each
  other, so update together.
- **Item probabilities reset once.** Adding an item changes the pool the game saves your item
  probabilities against, so they go back to defaults the first time you play with the mod. The
  same happens whenever you switch between a modded and an unmodded setup.

## Configuration

Optional. These live in the BepInEx config, and
[ModConfig](https://thunderstore.io/c/super-battle-golf/p/AtomicStudio/ModConfig/) is supported
for editing them in-game.

| Setting          | Default | What it does                                                     |
| ---------------- | ------- | ---------------------------------------------------------------- |
| `Enabled`        | `true`  | Turns the Shock Mine on or off. Off, it never spawns.            |
| `SoundVolume`    | `0.8`   | Volume of the hum while a player is held.                        |
| `BlockUnmodded`  | `true`  | Host only: disconnect players who don't have the mod.            |
| `DebugHotkeys`   | `false` | Host-only testing keys: F7 gives you a mine, F8 sets off the nearest one. |
| `VerboseLogging` | `false` | Logs every step of planting, tripping and releasing, for bug reports. |

## Plays well with others

The Shock Mine uses its own item, rule and network ids, chosen well clear of the game's own and
of other item mods such as the Switcheroo (BallSwapItem), so the two are designed to be
installed side by side.

## Found a bug?

Turn on `VerboseLogging`, reproduce it, and send `BepInEx/LogOutput.log` along with what
happened. See the changelog for what's new in each version.
