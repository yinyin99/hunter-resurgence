# Server

`server/Server` is the game server: the local server a player runs on their own PC, and the code of the community server. Tests: `server/Server.Tests`. One SQLite database per server ([ADR 0001](adr/0001-sqlite-for-profiles.md)).

At this stage it answers the hunt itself (connect, heartbeat, kills, harvest, score, purchases, expeditions, chat notices, photo upload) from a player state, and answers everything else from what the operator supplies: recorded replies, the game's install archives and a mirror folder. Accounts, importers that need no recordings and the new launcher's sign-in are later steps ([README](../README.md#roadmap)).

## Run it

```
dotnet run --project server/Server -c Release -- [--settings <file>] serve | admin ... | coverage
```

`serve` is the default. The settings file is `--settings <file>`, else the environment variable `SERVER_SETTINGS`, else `settings.json` in the current folder. It is never committed (`.gitignore`); start from `server/Server/settings.example.json`. Relative paths are relative to the settings file.

| Key | Meaning |
|---|---|
| `gameserver_host`, `api_host`, `apiv2_host`, `signup_host`, `web_host`, `static_host`, `static_legacy_host`, `map_host`, `photo_host`, `avatar_host`, `news_host`, `telemetry_host`, `crash_host` | The 13 host names the game asks for. All are required. The server routes by the `Host` of a request |
| `install_dir` | Required. The game's install folder: its archives answer image and map requests (`game/archives`) and its sample files seed the item definitions. Read only |
| `data_dir` | Default `data`: `state.db`, `spawn.json`, `photos/`, the miss log `misses.jsonl` and `telemetry.jsonl` |
| `recordings_dir` | Default `recordings`: HAR files the operator recorded of their own traffic. The newest 200 reply per request wins |
| `mirror_dir` | Default `mirror`: saved static pages, `<mirror_dir>/<host>/<path>` |
| `variants_file` | Optional: a JSON list of `{id, min_score, max_score, css_multiplier}` per trophy variant for the score sheets |
| `listeners` | Required: a list of `{address, port}`; add `cert` and `key` (PEM files) for HTTPS. Plain HTTP is a listener without them. `"admin": true` (loopback addresses only) makes it serve the admin API, see below. Every listener speaks HTTP/1.1 only |
| `trusted_proxies` | Addresses or `address/prefix` of the proxies in front of the server (default none) |
| `host_header` | Default `X-Game-Host`, see below |
| `max_body_bytes` | Default 16 MiB: the request body limit. The admin API's is 64 KiB |

`serve` needs no change to the PC: it does not read or require the Windows hosts file. How the client is pointed at the server is not part of the server.

`admin item <item_id> [count]`, `admin em|gm <amount>` and `admin spawn <reserve> <animal> <males> [females] [x z radius]` change the player in `data_dir` from the command line; `coverage` replays every request of the recordings through the server and lists what it cannot answer, naming hosts by their settings key.

## How a request is answered

1. A request for `telemetry_host` or `crash_host` gets 204 and is kept in `telemetry.jsonl`.
2. The player state answers what it owns: `heartbeat`, `disconnect`, `connect`, the photo upload and photo files on the game host; `Inventory/move_multiple2`, `Inventory/buy`, `Launch/start`, `Chat/conversations`, `Chat/conversation`, `Skill/list`, and the lists that wrap a recorded base reply (`Splash/list_active`, `Public_user/expedition`, `Expedition/list`, `Me/poll`, `Me/me`) on the API host; the harvest pages on the API v2 host; the kill details page on the website host.
3. Everything else, in this order: an archive file in the URL's own folder, the recorded reply (a JSONP callback is swapped), the mirror, an archive file found by its name, an empty 200 for a request that was only recorded as unchanged, else a miss: `null` under `/v1/`, `{"err": 0}` on the game host, 404 on the API v2 host. A miss is written to the miss log, keys only.

The state answer wins; a handler that has nothing to say returns control to step 3, so a reply is never made up where a recorded one exists.

The server holds no sign-in: tokens are not checked.

## Behind a reverse proxy

A proxy in front of the server speaks plain HTTP to it (a listener without `cert`). The game routes by host name, which a proxy hides, so a local forwarder in front of the proxy puts the name the client asked for in a request header. The server reads `host_header` (default `X-Game-Host`) and routes by its value, only when the peer address is in `trusted_proxies`; from any other peer the header is ignored. A value that is not a host name (optionally with a port) is ignored too.

## Admin API

`/admin/*`, Swagger UI (`/swagger`) and the OpenAPI documents are for a person at the server's machine. A request is served only when all of these hold: its `Host` is `localhost` or `127.0.0.1`; it came in on a listener marked `"admin": true`; its peer address (not a header) is a loopback address; and it carries no header that says a proxy relayed it (`X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `Forwarded`, `Via`, `X-Real-IP`, the `host_header`). Any other request for those names gets 403. No listener is an admin listener unless marked, and a proxy on the same machine is a loopback peer too, so keep the listener a proxy connects to unmarked and give the admin API its own loopback listener (for example `127.0.0.1:8081`, reached from the VPS through an SSH tunnel). Admin bodies are limited to 64 KiB.

## Tests

`dotnet test --project server/Server.Tests -c Release`: xUnit v3, no network and nothing outside a temporary folder. Fixtures are built in code. CI runs them on `windows-latest` (with the rest of the solution) and on `ubuntu-latest` ([CONTRIBUTING.md](../CONTRIBUTING.md#ci-and-updates)).
