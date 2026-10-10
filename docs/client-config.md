# Client server configuration

The client reads its server addresses from `ip.cfg` in the install root: a small XML file with one
`<branch name="public">` element. That branch holds four entries, each a server address:

- the request (login and account) host, given as a hostname;
- two regional matchmaking servers;
- the stats server.

All the original hosts are offline. `tools/point-client-at-local.ps1` sets every entry of the public
branch to `127.0.0.1`, so the unmodified client reaches the local server. This is a change to a
configuration file only, never to the game's programs. The original file is kept as
`ip.cfg.original`, and `-Restore` puts it back.
