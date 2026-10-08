# Client server configuration

The client reads its server addresses from `ip.cfg` in the install root (XML,
one `<branch name="public">` element). It names four endpoints:

| Element | Purpose (inferred from name) |
|---|---|
| `RequestServerIP` | request/API host (a hostname) |
| `MatchmakingServerIPEU` | EU matchmaking server |
| `MatchmakingServerIPNA` | NA matchmaking server |
| `StatsServerIP` | stats server |

All original hosts are offline. Pointing these at `127.0.0.1` should let the
unmodified client reach a local server, which is a config change rather than a
change to the client program. Not yet tested. Ports and protocols are still unknown.
