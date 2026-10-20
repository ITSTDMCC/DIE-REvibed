# Changelog

## Unreleased

### Horde
- **3 AI teammates in Horde.** Three random bot heroes join your team, fight zombies near them or near you,
  follow you otherwise, and come back beside you 8 seconds after going down. Zombies target the nearest hero,
  bots included.
- **Pick the Horde map.** `server/bin/horde-map.txt` (made on first use) holds `outpost`, `lab` or `club` (or 5, 6, 7),
  or `rotate` for the old rotation, which restarts at Outpost with every server start.

### Hub
- **Weapon modifications work.** The hub's request to put designs in a weapon's slots (type 74) was not handled, so
  every purchase showed "unknown error". The slots are now saved with the weapon in `account.txt` (an optional
  6th field on `unique=` lines) and sent back in the inventory; the price is charged unless unlimited currency is on.

### Performance
- **Reflection lookups are cached.** Profiling a Scavenger match showed the server at about 8 ticks a second
  instead of 30 (AI, frame building and after-update hooks each spent tens of ms per tick), so hits and cooldowns
  showed up seconds late. Much of that was looking up the game's properties, fields and methods by name on every
  tick. Lookups now go through a cache keyed by type, name and flags (`ReflectionCache.cs`: about 0.2-0.3 µs a call
  instead of 0.7-2.3 µs), and role names are memoized (`R.Name`).

## v0.3 (2026-10-10)

### No game names in the code
- Every readable class, method, field and enum value name from the game is now a "role" stored as a one-way
  hash; the server finds the real names in your installed game at start-up (602 roles, all checked).
- Our own code no longer copies the game's names (protocol messages, account fields, result codes).
- Comments and docs describe the game's code in plain words.
- The `ip.cfg` setup script points every server address in the file at your PC without naming the entries.
- The developer test that compiled directly against the game's libraries is no longer in the repository.

### Fixes
- **Tutorial uses your hero** ([#1](../../issues/1)). It uses the hero you queued with, or the one you picked when you
  made your account (the main-menu tutorial sends no loadout). You still start with fists at infection level 1.
- **Zombies ignored any hero but the default one.** The AI watched the default hero's idle copy; it now follows
  your own hero.
- **Special zombies use their special moves** ([#4](../../issues/4)): charges, the Puller's hook, spit, stomp,
  ravage, fear, summon and the hoarder's bomb, each on the game's own cooldown. When to use them is ours.
- **Special zombies can't be stun-locked** ([#5](../../issues/5)): basic attacks stagger a special at most once every
  2 seconds; damage always lands, and abilities meant to stun still do.
- **Hoarder death** ([#6](../../issues/6)): the Horde HUD's "hoarder active" state is cleared as it dies.
- **Horde boss in the pit** ([#7](../../issues/7)): the boss appears at the map's own trigger position for its event
  (down in the drainage pit on Outpost), and a boss or minion that can't reach you is moved closer, as a last
  resort right beside you.
- **Starter heroes:** a new account gets all four starter heroes at its first hero pick; existing accounts that
  already picked get the rest at their next login.
- **Infection level** ([#2](../../issues/2), tuning): a match uses 30% of the level your loadout's strength gives,
  so a walker takes four paddle swings at starter gear.
- **Queued scout missions** no longer crash the match build (they now rotate through the practice maps).
- **A failed match build** no longer leaves the server unable to start matches; it reloads the tutorial and the
  next request works.
- **Tutorial companion:** coming back after a second death no longer stops the match; she faces the way she walks
  while following you.

## v0.2 (2026-10-08)
- The game's scrambled (obfuscated) names were removed from the repository; the server finds them by structural
  fingerprint at start-up.
- Character names removed from code, comments and docs.
- Fix: rewards could fail to be written at the end of a match.

## v0.1 (2026-10-07)
- First release: login and hub, tutorial, practice missions, Horde and Scavenger against bots, and the
  unlock-all switches.
