# Network protocol (request, matchmaking and stats links)

Our own description of the wire format, worked out from the client and
verified by the reference tests in `tests/`.

## Framing

Each frame on the TCP stream is:

| Field | Size | Notes |
|---|---|---|
| length | uint32, little-endian | Total frame size **including** these 4 bytes. `0` alone is a keep-alive (sent after 20 s idle). |
| kind | 1 byte | 1 = message, 2 = request, 3 = response |

Then, by kind:

- **Message**: uint16 type, body.
- **Request**: 16-byte request id (a GUID), uint16 type, body.
- **Response**: the request's 16-byte id, varint result code, body (only when result is OK).

Result codes: 0 OK, 1 Disconnected, 2 Timeout, 3 DeserializeFail, 4 UnrecognizedError, 5 PermissionDenied.
Clients time requests out after 30 s by default (15 s for matchmaking tickets).

## Value encoding

| Type | Encoding |
|---|---|
| byte, bool | 1 byte |
| ushort, short | 2 bytes, little-endian |
| float | 4 bytes, IEEE little-endian |
| int, uint, long, ulong | LEB128 varint of the unsigned bit pattern (negative ints take 5 bytes, negative longs 10) |
| enum | varint of its value, except enums whose underlying type is byte, which take 1 byte |
| string | varint byte count, then UTF-8 |
| byte[] | varint count, then the bytes (null is sent as count 0) |
| array / list of T | varint count, then each element |
| nested struct | its fields in declaration order |

## Login sequence (request server)

1. Client connects to port 1555 and sends **LoginRequest** (type 43):
   Steam ticket (byte[]), version major/minor/build/revision (varints),
   branch (string), language (varint), profile name (string).
2. Server responds OK with **LoginResponse**: result, queue position, logins per second (float).
3. Server then sends the **LoginDataMessage** message (type 44): result,
   session ticket, user id, currencies, spending, account data, first-login
   flag, inventory lists, DLC list, disabled features, gameplay changes,
   login rewards, session id, ban info, welcome URL, server time.
   The client treats login as complete only when this message arrives.
4. On reconnect the client re-authenticates with **AuthRequest** (type 1,
   session ticket) or **SteamAuthRequest** (type 25, Steam ticket).

Our server accepts any ticket: there is no account system to protect, and
Steam itself is left untouched.

## Request ids handled so far

See `server/src/Protocol/GameMessageIds.cs`. Unknown requests are logged with
their body and answered with UnrecognizedError so the client never waits for
a timeout.

## Story map unlocks and owned characters

Checked against the game's own account serializer, loaded from the player's install, by
`tests/run_windows_checks.cmd` (all cases byte-identical).

- **UnlockStoryMapNodeRequest** (type 20): varint node id, varint choice
  count, one byte per choice, varint story map revision (the hub sends -1).
- Reply: varint result (1 Success, 2 CannotUnlock, 3 RevisionOutdated,
  4 WrongAmountOfChoices, 5 NotEnoughPoints, 6 AlreadyUnlocked), varint count
  of unique rewards (we send 0), varint revision (echoed).
- The free first character is node 2, one choice: 0-3 the four starting heroes
  (character ids 7, 5, 8, 6), or 4 for 7200 character points.
- Owned characters and unlocked nodes live in the account data's story map
  blob: byte 1; byte 5, varint node count, then per node varint id, byte
  times unlocked, byte choice count, choice bytes; then byte 1, varint
  character count, then per character byte id, bool owned, varint xp. An
  account with nothing unlocked is sent as an empty blob.
- **GetMatchmakingTicketRequest** (type 58) reply: bool success, byte[] ticket
  (we send 16 random bytes).

The server keeps the account in `server/bin/account.txt` (git-ignored, one
`key=value` per line) and loads it at startup.

## Matchmaking service (TCP 2555, same framing as 1555)

The field layout matches the type 10 request the hub sends, byte for byte.

- **SoloServerCreateRequest** (type 10): auth data (byte[] session ticket,
  four varint version numbers, string branch, varint int), byte[] matchmaking
  ticket, varint queue type, varint map index. We accept any ticket.
- Reply (SoloServerCreated): varint auth result (1 Success), varint join
  queue result (1 Success), byte[] match server IPv4 address, ushort port
  (little-endian). With a nonzero port the hub starts the match client with
  `+connect <ip> <port>`; port 0 makes it show a "server failed" popup.
- We send 127.0.0.1:3555. A UDP listener on 3555 logs every datagram (hex and
  sender) and never replies, to capture the match client's first handshake.

## Inventory: default weapons

- Every account owns the default melee weapon (schematic 1005) and default
  ranged weapon (schematic 1009). They are the account's starting rewards
  (story map node 0), and the tutorial looks for exactly these two ids. At
  login the server adds whichever is missing and marks node 0 unlocked.
- A weapon is a *Unique*: byte[] GUID (16 bytes, made once per weapon and kept),
  ushort schematic id (little-endian), varint user id, bool in inventory,
  varint durability (100 full, 0 broken), varint EP, byte[] slots (empty).
- Uniques are sent in the uniques list of the login data message and of the
  inventory reply. Equipping happens in the game with no server request.
- `tests/run_windows_checks.cmd` compares this encoding byte-for-byte with the
  game's own weapon serializer. That library only runs under Mono, so the
  script uses 32-bit Mono when it is installed and reports the weapon cases as
  skipped otherwise.

## Match client handshake (UDP 3555)

Source: datagrams captured by our UDP logger, read against the public message
format of Lidgren.Network (the open-source networking library the match client
uses).

- After the hub gets 127.0.0.1:3555 from matchmaking, the match client sends an
  85-byte datagram to it, and resent it every second while nobody answered.
- Header (5 bytes): message type 0x83 (Lidgren *Connect*), 2 bytes of
  fragment flag and sequence number (0), 2 bytes payload length in bits
  (0x0280 = 640 bits = 80 bytes).
- Payload: string "StunNet" (the application identifier, length-prefixed),
  the sender's 64-bit unique id, the sender's local time as a float, then a
  57-byte hail with the game's own connect data. Between resends only the
  time changed; the hail stayed identical.
- With no answer, the client sends Connect 8 times, one second apart, then a Lidgren *Disconnect* (0x87) whose reason
  string is "Failed to establish connection - no response from remote host".
  Twenty seconds later it starts over from a new UDP port with a
  new 64-bit unique id, and the 57-byte hail was byte-identical to the first
  attempt. So the hail does not carry a per-attempt value; whether it changes
  per match or per account is not known yet.
- Our server doesn't answer over UDP; the client then falls back to TCP (below).

## Match server over TCP (3555)

- After a few seconds of unanswered UDP, the match client connects to the
  same port over TCP, using the request-server framing.
- **GameplayAuthRequest** (type 5): varint server port, byte[] client hail
  (the same bytes as in the UDP Connect).
- Reply OK: varint 1 (Success), byte[] server hail. The server builds the hail
  at run time with the game's own serializers, loaded from the player's install:
  a flag, the client index, the client info (Steam id, user id, connection
  flags, the player's chosen hero, name, team and gear), then the game info
  (map, infection level, match length and the heroes to load).
- The client's connect hail carries its version, the session ticket from the
  request server, and its connection data (camera direction, name, ...). The
  camera direction must reach the server's copy of the client info, or the
  player can't move.
- Then both sides exchange message type 38 (**GameplayData**), one game frame
  per message.
  - **Client frames:** controller input, then two reliable message layers
    (in-order and unordered). Reliable payloads start with a byte kind
    (0 game message, 1 debug) and a byte type, for example 20 LoadingComplete,
    28 ClientStats, 14 SkipTutorialIntro.
  - **Server frames:** the two reliable layers (acks and queued messages), the
    controllers of every client slot (the player and any bots), then the
    synchronised objects (a count, then per object its index and its own state).
    Before LoadingComplete the server sends empty frames.

## Match server: combat

- The client's shared game logic leaves the server's side of several actions
  empty: applying stat changes, effects and buffs, spawning objects, knockback,
  combat text, the per-tick update and object destruction. Our server supplies
  them, plus its own AI.
- Pooled objects such as attack spells are spawned through the server: it sets
  the object's team and owner from whoever cast it, then spawns it. A spell
  checks the target's team against its own when it hits.
- In the PvE modes players are Team1 and zombies Team3. NPC stats scale with the match's infection
  level and multiplier, which must not be 0.
- The client's attacks arrive in its controller data (use id, combo step,
  target index), and the game's own code applies them to the player's ability
  bar.
- **Destroy message** (one per destroyed object and client that has seen it,
  reliable, in order, channel 0): byte 1, byte 0, byte 21 (GameObjectDestroyed),
  the client's spawn id for the object (ranged 0..3), its global index (ranged
  0..number of game objects), then the object's own destroy serializer (for an
  NPC: dead flag and killer). The client plays the death from it. Objects that
  go inactive are not synced again; that would remove them without a death.
- The server also adds its client to the network base's active and connected
  client lists, which the game's own update and spawn code walk.

## Match server: map objects

- Weapons given during a match (for example the tutorial's paddle) are sent as a reliable game message: bytes 01 00 07 00, then the player's updated client info.
- The pickup objects (the paddle) are not entities. Their position is a field on the pickup base type, and the server never needs to move them after spawning.
- Barricades are pooled destructibles. The server sets position, facing, part, barricade type and team (we use Neutral), spawns them, then sets Health and MaxHealth together through the barricade's own hp setter.
