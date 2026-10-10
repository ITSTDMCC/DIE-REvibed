# Patch history: what the public patch notes say about game setup

Source: the Dead Island Wiki's patch-note pages, v0.5.1 (1 Sep 2014) to v0.8 (12 Mar 2015).
- Index: https://deadisland.fandom.com/wiki/Dead_Island:_Epidemic_patch_notes
- Pages: `/v0.5.1`, `/v0.5.2`, `/v0.6`, `/v0.6.3`, `/v0.7`, `/v0.7.3`, `/v0.7.4`, `/v0.8`.

The pages copy Stunlock's announcements. Read on 2026-10-07. Everything below is a summary in our own words, kept to
what matters for running matches and the account (setup, difficulty, infection level, modes, rewards). Balance changes to
single abilities are left out.

The client we work with is **0.8.5** (build 38860, seen in the match client's corner text). That is later than the last page
here. Where the client's own code or data says something, it is cited as such.

## Timeline of game setup

### v0.5.1 (1 Sep 2014): Horde still live
- The end-of-Horde award changed from gold/silver/bronze **stars** to gold/silver/bronze **cups**. The client we have
  still says "Gold Star!" on the Horde scoreboard. Its medal icons are cups.
- A public Heroic Horde video from this era (client 0.5.2.29772, Oct 2014) shows the HUD text "Camp by Night –
  Infection Level 9 / Heroic Horde Mode" and an enemy "Elite Butcher – Level 7". It also shows the victory-condition
  panel with 12:00 / 20:00 medal times, which matches the 720 s / 1200 s medal limits in the client's Horde mode.
  - Source: a public YouTube recording of Heroic Horde from that period.

### v0.5.2 (18 Sep 2014)
- Gadgets were introduced. Players find their first gadget blueprints from Tier 4.
- **Elite zombies existed in Scout Missions.** Their health and damage there were lowered in this patch. So the
  original Scout missions (our "Practice") had elites. We spawn none there yet.

### v0.6 (20 Nov 2014): Crossroads replaces Horde
- **Crossroads, a new PvE mode, replaced Horde Mode in the play window.** From here on Horde was no longer offered.
  Our client (0.8.5) still contains Horde: its maps, WaveRules, the hub tiles and the queues. The hub shows them only
  when Crossroads (feature 45) is disabled, which is how we reach them. So our Horde is a **restoration of a mode retired
  in v0.6**, not of the game as it was at shutdown.
- Infection level after v0.6:
  - Missions and enemies scale with the **infection level**, with "a significant difference" between low and high levels.
  - The infection level follows the players' **character strength** (account level + crafted weapon tiers). Loot quality
    follows the infection level.
- Zombie **attributes** were added: permanent glowing buffs, more frequent at higher infection levels.
  - Deadly (+50% damage), Tank (−35% damage taken), Enraged (+50% attack speed), Speed (+50% move speed).
  - Projectile / Melee / Area Resistant (−75% of that damage type).
  - Plague (hurts nearby players), Unstable (explodes 2 s after death), Amplifier (attacks amplify), Numbing (attacks weaken).
  - Uncontrollable (immune to crowd control), Relentless (much more HP and damage).
  - Slaughterous (Massives only: stronger boss abilities).
- Progression and crafting:
  - Per-character levels were removed in favour of one global account level.
  - Weapon levelling was replaced by modifications.
  - Weapon tiers went from 6 to 16.
  - Duplicate blueprints turn into account XP.
- Scout Missions were reworked into single-player missions with one objective each, and became part of the starter quest.
- New enemy classification **Infected**: stronger than Walkers, weaker than specials.
  - Elite Walkers were renamed **Veterans** and moved to Infected.
  - New enemies: the Suicider (special), the Runner and the Spitter (infected).
- Play window: a "starter missions" tab with the Tutorial and the three Scout missions. The tab matches what our hub
  shows.

### v0.6.3 (6 Dec 2014)
- The tutorial's hostile scavengers were renamed.
- Melee attacks ignore 15% of zombie defence. This makes melee better against specials, hoarders and Massives.
- A fix for low-FPS players who could not capture the supply point in the Scout Mission. So Scout missions had a
  capture point.

### v0.7 (29 Jan 2015): power and infection rebalance
- Base power of characters and zombies halved (400 → 200). The power gap between weapon tiers was widened.
- **Enemy health and power rise significantly with each infection level.**
- In-match levels: **+6% max health and damage per level for players, +4% per level for zombies.**
- Matchmaking:
  - Scavenger sets the infection level from the strongest player's power.
  - Crossroads matches by chosen difficulty: Normal → Infectious → Contagious → Deadly → Endemic. Each level is unlocked
    by finishing the previous one several times with enough supplies.
  - Zombie health/damage and mission difficulty rise per difficulty. Spawn rate rises with the infection level.
- Lockboxes and keycodes were added to the inventory and shop. Daily bonuses became a guaranteed item drop.
- Scavenger elites drop 9 supplies, down from 15.

### v0.7.3 / v0.7.4 (Feb 2015)
- Attack-speed stacking was made linear.
- Lockbox duplicate rules were changed.
- Reward-box value thresholds were lowered: golden 300 → 200, silver 150 → 125.

### v0.8 (12 Mar 2015)
- Scavenger:
  - Looters get 1 / 2 attributes from infection level 5 / 10.
  - Hoarders get 1 attribute from infection level 12.
  - Looters spawn at in-match level 3 or higher.
- The starter quest was cut to 5 steps: play Scout, craft a weapon, play Crossroads, buy an item, play 3 matches. The
  steps can be done in any order, and the quest no longer locks the hub.
- The scoreboard gained damage, healing, takedowns and score.
- Players are invulnerable for 2.5 s after respawning in Crossroads.

## What the client's own data says about infection level

From the game's data, read at run time from the player's install (summarised, not copied):
- Character power thresholds for the infection levels go 100, 200, … 1500: 16 levels. The game maps a power value to an
  infection level with them, which agrees with the v0.6/v0.7 notes ("infection level follows character strength").
- Each infection level has a zombie multiplier: 1.0 at level 1, rising to about 2.96 at level 10 and about 5.12 at
  level 16. Zombie base stats are multiplied by it, and by a rating for the zombie's in-match level.
- The infection level the server sends in the match hail is what the client shows ("Infection Level N" on the loading
  screen and scoreboard).

## What DIE: Revibed does with this

1. **Infection level from the player's power**, as the post-v0.6 game did. Built.
2. **Zombie scaling** with the game's own multiplier for that level. Built.
3. **Zombie attributes** at higher infection levels (v0.6 list). Not built. The client has the attribute system, but the
   Horde and Practice maps don't load most attribute effects.
4. **Veterans in Practice**, standing in for the v0.5.2 elites. Built.
5. Horde stays a restoration of the pre-v0.6 mode. Crossroads (the mode that replaced it) is the shutdown-era PvE mode and
   is still to be studied. It is disabled today (feature 45) so that Practice and Horde show.
