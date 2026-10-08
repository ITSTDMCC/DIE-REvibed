# Game modes on DIE: Revibed

How each mode runs on this server, in plain terms. The game's own code still handles
combat, abilities, physics and the HUD. This document covers what the original server
did and we had to rebuild: match flow, spawning, objectives, scoring and rewards.

Numbers marked **stand-in** are our own choices, because the original values lived on
the official servers and are lost. Everything else follows behaviour the game client
itself shows or expects.

---

## Shared rules (all modes)

- **Infection level.** Worked out from the strength of the hero and gear you queue
  with: account level, weapon tiers and gadgets, levels 1 to 16. This follows the
  public patch notes from v0.6 and v0.7. The tutorial is always level 1.
- **Zombie toughness.** Health and damage are multiplied by the game's own multiplier
  for the infection level, about ×5.1 at level 16.
- **Zombie level label (LV).** The average in-match level of all heroes in the match,
  taken when the zombie spawns. This is the client's own rule.
- **In-match leveling.** Kills give XP the game's own way, shared with teammates
  nearby. Bots earn it too. Ability upgrades work.
- **Rewards (stand-in).** 10 account XP per match minute, split across the reward boxes
  the match earned. The length counted is limited to the game's own minimum and maximum
  for each mode. Level-up rewards come from the game's own reward table.
- **Basic attacks between heroes don't stagger.** Weapon primary attacks from one hero
  to another deal damage but no knockback-stun. Abilities that are meant to stun still
  do, and zombies and heroes stagger each other as usual. This is a deliberate choice,
  matching footage of the original.
- **Hero minions** (for example the player hero's summoned minion). Stand-in AI:
  - attacks the target its owner ordered;
  - otherwise attacks the nearest enemy within 90 units of itself or its owner, never
    straying more than 200 units from the owner;
  - with nothing to fight, follows its owner and stops within 45 units.
  Zombies treat minions as targets too.

---

## Tutorial

The game's own tutorial script runs stage by stage. The server supplies what the
original server did:

- spawning the zombies, the companion, the Puller and the two hostile scavengers;
- barricades (including the shared-health wooden barricade group) and the generator;
- the paddle and shotgun pickups;
- the ambush;
- the storage room capture and the supply pickups;
- the delivery and the Floater;
- respawns.

At the last stage the client fades out and closes itself, and the hub records the
tutorial as completed.

Stand-ins:
- barricade health (100);
- ambush rate: up to 7 walkers, one every 0.5–1 s;
- some stage timers.

---

## Practice (starter / scout missions)

Three maps: Outpost, Lab and Club. Each is one objective, then a return to the truck.

1. Go to the map's event point.
2. Do the event:
   - **Outpost:** steal the supplies (5 s interaction).
   - **Lab:** kill the Looter, which drops the supplies.
   - **Club:** capture the flag (5 s), which drops the supplies.
3. Return to the truck and deliver with X. The mission ends when the team has
   delivered the goal: Outpost 60, Lab 30, Club 30. These goals come from the game's own
   data.

Also:
- Zombies stand at the map's spawn points, up to 55 (stand-in).
- An ambush starts when you come near (30 s, stand-in).
- Assaults spawn their roster from the map, including Butchers, Floaters, Walking
  Bombs and Veterans.
- Announcer lines play near the map's transmission points.
- If supplies end up somewhere unreachable, a safety net drops the missing amount at the
  truck (stand-in).

---

## Horde (Normal and Heroic)

Maps rotate Outpost → Lab → Club.

1. **Capture a supply point.** Hold X at it, or stand in its range for 12 s
   (fallback). Its barricades go up.
2. **Survive the waves** (stand-in): 3 waves per point, with a 10 s countdown before
   each.
   - **Normal:** 12 walkers and 1 special per wave (Butcher, Floater, Puller, Ram or
     Siren in turn). At most 16 walkers and 2 specials alive at once.
   - **Heroic:** 18 walkers (4 of them Veterans) and 2 specials, one of them an elite.
3. The **second point** adds a final wave with the map's **hoarder**: Outpost Floater,
   Lab Butcher, Club Puller.
4. **Boss stage**, 5 s after both points are held. The game's real Horde bosses are
   never loaded by the client, so the boss is the map's elite (stand-in): Outpost
   Butcher, Lab Floater, Club Ram. It gets minions from the map's own boss settings.
5. **Medal by match time** (the game's own limits): Gold within 12:00, Silver within
   20:00, otherwise Bronze.

Supplies looted (stand-in): 5 per walker, 50 per special, 100 for the hoarder.

Dying during a point's waves restarts that point's waves. You can concede after 5
minutes.

Heroic Horde unlocks at account level 20, as in the original (or with unlock-all).

Safety nets (ours):
- A wave zombie that hasn't moved for 8 s is moved back near the point.
- In the boss stage, a boss or minion that can't reach you (for example after you
  drop into Outpost's one-way pit) is moved next to you after 8 s.

---

## Scavenger

Three teams of four: you and 3 bots on Orange, 4 bots on Blue and 4 bots on Green.
Maps rotate Resort → Jungle → Expedition. The first team to deliver 800 supplies to
its truck, and hold that for 15 s, wins.

1. **Barricade race.** Each team breaks its two wooden barricade groups (150 HP each,
   stand-in) to reach the supply area. The first team through earns a medal.
2. **Supply points.**
   - Press X in a point to start capturing it.
   - Capture takes 10 s with one hero (stand-in), and goes faster with more teammates.
   - It pauses while another team is in the point, and stops if your team leaves.
   - When an enemy team is taking over your point, press X there to deny it.
   - Bots press X too, 1.5 s after arriving and at most every 5 s.
3. **Income** (stand-in): every minute, 25 supplies per point held go into your truck.
4. **Looters and hoarders.**
   - Up to 6 looters wander near the points (one every 12 s, stand-in) and drop
     supplies when killed.
   - A hoarder appears every 4 minutes (stand-in) and drops a large haul.
   - Zombies go for whichever hero is nearest, bots included.
5. **Deliver** carried supplies at your truck with X. Bots deliver once they carry 20.
6. **Night** falls when the first truck is full (500).
7. **Win and rewards.** A team at 800 leads for 15 s, then wins. Placements go by
   supplies delivered. Medals: barricade race, first full truck and victory.

### Bots (all ours)

The original had no bots. Ours:

- Break their barricades, then take points they don't own, deny takeovers, hunt
  looters and hoarders, fight enemy heroes within 45 units and zombies within 25, and
  deliver.
- Find their way using a network of waypoints over the map, wall-following, and as a
  last resort a short hop forward when they've been stuck. You may occasionally see a
  bot blink.
- Respawn after 8 s at a walkable spot near their team's respawn point.

An offline test at infection 16 finished in 16 to 18 minutes on each map, with every
team scoring.
