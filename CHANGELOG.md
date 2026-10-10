# Changelog

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
