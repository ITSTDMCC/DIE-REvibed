# How the game is put together

Worked out from an installed copy of the game. Program names are the game's own;
everything else is our description.

## Programs in the install

| Program | Role |
|---|---|
| `Dead Island Epidemic - Launcher.exe` | Small .NET launcher. Resets some window settings, then starts the hub. |
| `Dead Island Epidemic - Crib.exe` | The **hub** ("Crib"): login, characters, inventory, crafting, shop, story map, matchmaking. Unity 4 app with its own `Crib_Data`. |
| `Dead Island Epidemic.exe` | The **match client**. The hub starts it when a match is found and it connects to a match server. |

## Online services the client expects

All addresses come from `ip.cfg` (branch `public`); ports use defaults unless
`ip.cfg` names them.

| Service | Default port | Transport | Used by | Status in our server |
|---|---|---|---|---|
| Request server | TCP 1555 | framed TCP (see protocol.md) | hub, match client | Implemented: login, account, inventory, currency, shop, story map, rewards |
| Matchmaking server | TCP 2555 | framed TCP | hub | Implemented: solo matches and one-player queues |
| Stats server | TCP 4555 | framed TCP | hub | Accepts connections and logs requests |
| Match server | TCP 3555 (the client tries UDP first) | framed TCP | match client | Implemented: runs the game's own match logic plus our match flow |

## Solo match flow (from the hub's code)

1. Hub asks the request server for a matchmaking ticket (`MatchTicket`).
2. Hub sends `SoloServer` (ticket, queue type, map) to the matchmaking server.
3. Matchmaking answers `solo server created` with a match server IP and port.
4. Hub starts the match client, which connects to that match server over Lidgren UDP and
   authenticates through the request server (`MatchSignIn`).

## The match server

The authoritative game simulation ran on Stunlock's servers and was never shipped. What
the client does contain is the shared game-logic library: entities, abilities, buffs,
game modes, pathfinding and physics. Many server-side paths are already in that shared
code.

DIE: Revibed's match server loads that library from the player's own install at run
time, under 32-bit Mono. It supplies the missing server side itself: the server hooks,
networking, match flow, spawning, objectives, scoring, bots and AI. No game code is
included in this repository.

## Finding the game's code at run time (v0.2, v0.3)

The server contains none of the game's class, field, method or enum value names. It uses
**roles**, our own descriptive names such as `Barricade.SetHealth`, `World.SpawnObject` or
`Team.Two`, and looks each one up when it starts. Most of the game's code has scrambled
names; those roles are found by structure (v0.2). The rest has readable names; those
roles are found by a hash of the name (v0.3):

- `server/src/Resolve/RoleTable.cs` maps each role to a **fingerprint**: a hash of the
  shape of the class (its kind, base class, and the counts and types of its fields and
  methods, with every scrambled name replaced by a placeholder), plus its position among
  classes of the same shape. A member's fingerprint is its class's fingerprint plus a
  hash of its own signature and its position among members of the same signature.
- A role for a readable name stores `N|` and a salted one-way hash of the name. At
  start-up the server hashes every readable name in the libraries (type, namespace,
  member and enum value names) and keeps the ones the table asks for.
- At start-up `R.Init` fingerprints every class in the three game libraries on the
  player's PC, and `R.Check` confirms every role resolves. A missing role stops the
  server with a clear message. The supported build is 0.8.5.38860, the last one.
- The code asks for `R.Type("Zombie.Walker")`, `R.Name("HordeState.WaveIndex")` and so
  on. The result exists only in memory.
- A few members are found by behaviour instead. For example, the hub's "length in bytes"
  getter is the parameterless int method that returns 3 after three bytes are written
  and 11 after eleven.

`tools/RoleGen` rebuilds `RoleTable.cs`. It reads a developer-only role map
(`local/roles.map`, git-ignored, never committed) that pairs each role with the current
name, fingerprints the installed game, checks that every fingerprint leads back to
exactly that class or member, and writes the table. A few readable names exist only in
the hub's own library; RoleGen lists them in `RoleTable.HubOnly`, and only the hub side
(the rewards writer) resolves them. `tools\run_rolegen.cmd
local\roles.map check` compares the committed table with the installed game.
