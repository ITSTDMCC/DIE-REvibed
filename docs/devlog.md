# Development log

How DIE: Revibed got from "the game can't log in" to playable, and the decisions made
along the way. Newest first. All of this was tested against one copy of the game,
version 0.8.5.38860, on Windows 10.

The terms used below:

- **Stand-in:** a number we chose because the original lived on the official servers.
- **Ours:** behaviour we invented where the original game had none, such as the bots.

---

## v0.3: readable names behind roles too (2026-10-10)

- **Why.** v0.2 removed the game's scrambled names, but the code still used the game's
  readable names: about 350 class, method, field and enum value names, such as the
  game's world manager or a stat type.
- **Name roles.** Each readable name the server needs is now a role, stored with a salted
  one-way hash of the name. At start-up the server hashes every readable name in the
  installed game and matches them. The role table holds 589 roles; every one resolves
  against 0.8.5.38860.
- **Our own names.** About 160 of our own identifiers had copied the game's names, mostly
  protocol message and field names. They now use our own words. Role names were checked
  too, and none repeats a game identifier.
- **Comments and docs** describe the game's code in plain words or by role name.
- **`ip.cfg`.** The setup script no longer names the file's entries. It points every
  entry of the public branch at this PC.
- **Tests.** The story-map and weapon checks now load the game by reflection through
  roles. The end-to-end reference test compiled directly against the game's libraries, so
  it was moved out of the repository and stays on the developer's PC.
- **Left as they are.** DLL file names (needed to load the libraries), map names, our
  own plain-English labels, generic programming names that happen to match (for example
  `IsDead` or `GetStat`), and third-party library names (Lidgren, Farseer).
- **Checked.** The offline checks pass, and the Horde, Practice and Scavenger probes play
  full matches through to rewards.

---

## v0.2: no game names in the repository

- **Why.** v0.1 referred to the game's scrambled class and field names directly in its
  source. v0.2 removes them, so the repository holds only our own code and words.
- **Roles and fingerprints.** Every class, field or method the server uses now has a
  descriptive role name. The role table stores a structural fingerprint for each one, and
  the server matches those fingerprints against the player's installed game at start-up
  (see `architecture.md`). All 206 roles resolve against 0.8.5.38860.
- **Found by behaviour.** The hub buffer's byte-length getter is picked by what it
  returns, not by its name. The first try skipped property getters and so found nothing.
  Rewards failed in the probes until that was fixed.
- **Character names.** The code, comments and docs now say "the player", "the companion"
  and "rival A/B" rather than the heroes' names.
- **Checked.** All the offline checks pass. The Horde, Practice and Scavenger probes play
  full matches through to rewards. The tutorial has no offline probe, so it needs a
  playtest.

---

## v0.1: release preparation (2026-10-07)

- The public repository contains only newly written code, scripts and documentation.
  The server builds the first match message from the player's own install, and no
  captured traffic is included.
- Infection level, Practice, Horde and Scavenger are all playable solo. Multiplayer is
  the next goal.

---

## Horde boss stage and the drainage pit (Outpost)

- **Problem:** the end of Outpost has a one-way drop into a pit. Once a player drops
  in, the boss and its minions can't find a way down, so they piled up at the edge.
- **Fix (ours):** a boss or minion that hasn't moved for 8 s while more than 25 units
  from the player is moved to a spot 45 units from the player that has a clear line to
  them.
- **Fuel tanks:** they have a fixed 3000 HP in the game's own code, with no infection
  scaling; only their explosion grows with infection level. We left them as the game
  has them.

## Zombies and minions

- **Zombies ignored bots.** Zombies only ever chased the human player. In Scavenger
  they now go for the nearest living hero of any team, and keep fighting while the
  human is dead.
- **Minions drew no aggro.** Zombies now count enemy minions as targets too, in every
  mode.
- **Minions didn't move.** In the original, a hero's minion was driven by the server.
  We wrote a stand-in minion AI: fight the owner's target or the nearest enemy, stay
  close, follow when idle. Confirmed working in play.
- A minion expiring no longer counts as a kill for its owner.

## Supply points all showed one point's state

- **Problem:** in Scavenger, capturing one supply point lit up every point on the
  minimap and on the ground.
- **Cause:** each point carries a number telling the client which entry in the shared
  capture lists is its own. Only the original server set it, so every point read entry
  0. Each point now gets its own number. Confirmed fixed in play.

## Knockback-stun objects ran out

- **Problem:** the "no stagger between heroes" rule (below) discarded the game's
  stun-effect object without handing it back to the game's object pool. After about 90
  seconds the pool was empty, and every melee hit failed partway through. Zombies died
  late, loot dropped twice, and supplies misbehaved.
- **Fix:** discarded effects are returned to the pool. A full offline match then ran
  with no errors.
- A related report of "a teammate killed me" turned out to be zombie strikes. The
  attack type in the log belongs to Looters, Floaters and Veterans. A friendly-fire
  detector now logs any damage between teammates; it has never fired.

## No stagger from basic attacks between heroes (deliberate change)

- Every weapon primary attack carries a knockback-stun. Bots attack quickly, so two or
  three of them could keep the player stunned.
- Footage of the original shows that basic attacks between players don't stun. We
  now drop the stun when one hero's basic attack hits another. Damage still applies,
  abilities that stun still do, and zombies are unaffected.

## End of match

- The victory screen flashed and vanished, because the "you finished" message reached
  the client a moment before the "match complete" state did. They are now sent in the
  same network frame, in every mode.
- The victory screen's "Crib" button closes the match client and returns to the hub,
  as in the original.
- Match history recorded the account's first character instead of the hero played.
  Fixed.
- One later match ended on the server but not on screen. The client stayed on "Green
  is winning in 0…". That match was also hit by the pool problem above. Watch for it.

## Hub details

- **Loadouts reset every login.** The login data's "first login" flag made the hub
  re-equip starter weapons each time. It is now cleared after the first login.
- **"Game Found" icons were off-centre.** The matchmaker sent the wrong player count.
  It now sends 1.
- **The Horde tile needs a double-click.** That's the original client's menu
  behaviour, so we left it.
- **The welcome popup spun forever.** The hub only loads it after the shop request
  succeeds, so we now answer with an empty shop. The welcome page is a simple card the
  server draws itself.

## Scavenger (ours, with bots)

- 11 bot heroes join the human (3 teammates, and 4 on each rival team). Each is driven
  through the same inputs a player uses: movement, aim, attack and action keys.
- **Navigation.** The game's own pathfinder only finds short routes on these maps, so
  bots combine three things:
  - a waypoint network over the map's spawn points;
  - wall-following when blocked;
  - as a last resort, a short hop forward when they've made no progress toward their
    goal for 20 s (10 s just after a respawn).
- **Stuck bots.** In one playtest a whole team paced against a wall for the entire
  match, because pacing counted as movement. Progress now means getting closer to the
  goal.
- **Capture.** Points are captured and denied with X, as the client's own prompts
  expect. Speed scales with the number of heroes present, and progress pauses while
  contested.
- **Looter replacements** wait 12 s. Reusing a just-killed looter in the same instant
  left a "ghost" on screen.
- Stand-in values: see `game-modes.md`.

## Zombie scaling

- Zombie health and damage now use the game's own infection multiplier, about ×5.1 at
  level 16.
- Bots earn kill XP, so the average hero level (which sets the zombies' LV label) rises
  during a match.
- The infection level follows the strength of the hero and gear you queue with, as the
  v0.6 and v0.7 patch notes describe.

## Preservation switches and loadouts

- **Unlock-all switches.** `unlockAll`, `maxLevel` and `unlimitedCurrency` let a player
  see everything. They change only what the hub is shown; real progress is untouched.
  - The item list is read from the player's own install at start-up: characters with
    a hub model, weapons, gadgets, designs, parts and consumables.
  - Vanity items and lockboxes are left out. Their content came from the official
    servers.
- **Loadouts.** Matches use the hero, weapons and gadgets the player queued with.

## Horde

- **Queueing.** Horde has no solo path, so the hub queues a one-player party and the
  matchmaker answers at once.
- **Restored mode.** Horde was retired in v0.6 (replaced by Crossroads). The 0.8.5
  client still contains it, so ours is a restoration of the pre-v0.6 mode.
- **Bosses.** The game's real Horde bosses are never loaded by the client, so each map
  uses its elite as the boss (stand-in).
- **Special zombies stood still.** On the server their movement values were never
  filled in, so they had zero acceleration. They're now set up when spawned.
- **Heroic.** Stand-in waves, at 1.5 times Normal, with Veterans and an elite. It's
  locked below account level 20, as in the original.
- **Scoreboard.** Kills, deaths and level are now sent to the scoreboard.

## Practice (scout missions)

- One objective per map, then a return to the truck. Objectives, announcer lines and
  goals come from the game's own data; timings are stand-ins.
- **Fixes from play:**
  - A softlock when supplies landed somewhere unreachable (safety net added).
  - Announcer lines playing too early.
  - The objective text not updating.
  - Barricades needing too many hits.

## Tutorial

Built stage by stage against the client's own tutorial script, then played end to end:

- spawning and AI;
- pathfinding for the companion and the zombies;
- the generator and the electric fence;
- the shared-health wooden barricade;
- the ambush;
- the storage room;
- supplies and delivery;
- the Floater;
- respawns;
- the client closing itself at the end.

Two details matter for any server:

- Zombies in a spawn pose (sitting, lying) can't move until the server releases them.
- An object only plays its death when the server sends its "destroyed" message.

## First contact

- **Login and hub.** The hub logged in to our request server: the Steam ticket is
  issued but not checked. It then fetched account, inventory, currency and catalogue
  data.
- **Starter weapons and first character.** Every account gets the two starter weapons,
  and the free first character pick was implemented.
- **Match server.** The match client tries UDP first, then falls back to TCP on the
  same port. Our match server answers over TCP. It runs the game's own gameplay code,
  loaded from the player's install under Mono, and supplies what the official server
  did.
- **Checks.** Our encodings are checked byte-for-byte against the game's own
  serializers, loaded from the player's install (`tests/`).
