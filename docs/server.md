# Server

`server/Server` is the game server: the local server a player runs on their own PC, and the code of the community server. Tests: `server/Server.Tests`. One SQLite database per server ([ADR 0001](adr/0001-sqlite-for-profiles.md)).

At this stage it answers the hunt itself (connect, heartbeat, kills, harvest, score, purchases, expeditions, chat notices, photo upload) from a player state, and answers everything else from what the operator supplies: recorded replies, the game's install archives and a mirror folder. Importers that need no recordings and the new launcher are later steps ([README](../README.md#roadmap)).

## Run it

```
dotnet run --project server/Server -c Release -- [--settings <file>] serve | admin ... | coverage
```

`serve` is the default. The settings file is `--settings <file>`, else the environment variable `SERVER_SETTINGS`, else `settings.json` in the current folder. It is never committed (`.gitignore`); start from `server/Server/settings.example.json`. Relative paths are relative to the settings file.

| Key | Meaning |
|---|---|
| `gameserver_host`, `api_host`, `apiv2_host`, `signup_host`, `web_host`, `static_host`, `static_legacy_host`, `map_host`, `photo_host`, `avatar_host`, `news_host`, `telemetry_host`, `crash_host` | The 13 host names the game asks for. All are required. The server routes by the `Host` of a request |
| `install_dir` | Required. The game's install folder: its archives answer image and map requests (`game/archives`) and its sample files seed the item definitions. Read only |
| `data_dir` | Default `data`: `state.db`, `spawn.json`, `photos/<account id>/`, the miss log `misses.jsonl` and `telemetry.jsonl` |
| `recordings_dir` | Default `recordings`: HAR files the operator recorded of their own traffic. The newest 200 reply per request wins |
| `mirror_dir` | Default `mirror`: saved static pages, `<mirror_dir>/<host>/<path>` |
| `variants_file` | Optional: a JSON list of `{id, min_score, max_score, css_multiplier}` per trophy variant for the score sheets |
| `offline_mode` | Default `true`: a request that no token resolves is the local account's (a player's own PC). `false` on a community server: it gets the reply of an expired token, see [Accounts](#accounts) |
| `listeners` | Required: a list of `{address, port}`; add `cert` and `key` (PEM files) for HTTPS. Plain HTTP is a listener without them. `"admin": true` (loopback addresses only) makes it serve the admin API, see below. Every listener speaks HTTP/1.1 only |
| `trusted_proxies` | Addresses or `address/prefix` of the proxies in front of the server (default none) |
| `host_header` | Default `X-Game-Host`, see below |
| `max_body_bytes` | Default 16 MiB: the request body limit. The admin API's is 64 KiB |

`serve` needs no change to the PC: it does not read or require the Windows hosts file. How the client is pointed at the server is not part of the server.

`admin item <item_id> [count]`, `admin em|gm <amount>` and `admin spawn <reserve> <animal> <males> [females] [x z radius]` change the player in `data_dir` from the command line; `coverage` replays every request of the recordings through the server and lists what it cannot answer, naming hosts by their settings key.

## How a request is answered

1. A request for `telemetry_host` or `crash_host` gets 204 and is kept in `telemetry.jsonl`.
2. The player state answers what it owns: `heartbeat`, `disconnect`, `connect`, the photo upload and photo files on the game host; `Inventory/move_multiple2`, `Inventory/buy`, `Launch/start`, `User/jwt`, `Oauth/token`, `Chat/conversations`, `Chat/conversation`, `Skill/list`, and the lists that wrap a recorded base reply (`Splash/list_active`, `Public_user/expedition`, `Expedition/list`, `Me/poll`, `Me/me`) on the API host; the harvest pages on the API v2 host; the sign-in and start page on the website and signup hosts, and the old launcher's own calls on the game host ([Sign-in and tokens](#sign-in-and-tokens)); the kill details page on the website host.
3. Everything else, in this order: an archive file in the URL's own folder, the recorded reply (a JSONP callback is swapped), the mirror, an archive file found by its name, an empty 200 for a request that was only recorded as unchanged, else a miss: `null` under `/v1/`, `{"err": 0}` on the game host, 404 on the API v2 host. A miss is written to the miss log, keys only.

The state answer wins; a handler that has nothing to say returns control to step 3, so a reply is never made up where a recorded one exists.

## Accounts

Every player is in `state.db`. `accounts` holds the game's user id, the Steam id (optional, unique), the handle (unique, case does not matter), the time of the one-shot claim, a banned flag and the connect reply's `account` object; `tokens` maps a token to an account and keeps only its SHA-256 hash. No e-mail, real name or address is stored. Every per-player table has an `account_id`. The ids a restored backup brings (item guids, kill ids) are unique per account, so two accounts can both own item 100 or kill 1; the ids the server hands out (expeditions, photos) are unique in the whole database and always looked up together with the account. A new account starts with the recorded connect's inventory, wallet and skills (`State.CreateAccount`). A database from before accounts is refused: delete it.

A request's account comes from its token. The tokens it carries are tried in this order, the first that resolves wins: the `X-Hun-Session` header (game host), `Authorization: Bearer` (API v2 host), `oauth_access_token` in the form or the query (API host). A token that is unknown, expired or of a banned account does not resolve. When none does, the request is the local account's if `offline_mode` is true, else it is refused: `{"errorCode": 5, "message": "token expired"}` with status 200 on the API host, status 401 on the game host (`{"err": 1}`) and on the API v2 host. A CORS preflight carries no token and is answered by the replay. Photo links name the account (`/photos/<account id>/<file>`) and need no token; the kill details page finds the account by the handle in its URL (on a local server any handle is the local account). The `admin` commands and the admin API act on the local account.

The database is in WAL mode with `synchronous=NORMAL` and a 5 s busy timeout, connections are pooled, every transaction starts with `BEGIN IMMEDIATE`, and the schema and its migrations run once at startup under `PRAGMA user_version`. A database call is scoped to one account: the SQL names it as `$a`, and a statement that touches a per-player table without it throws, so a forgotten filter fails in the tests and never reads another player's rows.

## Sign-in and tokens

With `offline_mode` false (a community server) players sign in with Steam; nothing is served to a request without a token that resolves. With it true none of the following is on: the start page, the launcher's calls and the replies that carry tokens are the replay's, as before. Steam's OpenID 2.0 needs no key. No Steam name, e-mail or address is read or kept: only the 17-digit Steam id, the handle the player picks and the game data.

**The sign-in, in steps** (on `web_host` for the website, on `signup_host` for the launcher; the origin is `https://<that host>`):

1. The browser opens `/auth/steam/login?next=<local path>`. The server keeps a login in memory (15 minutes), sets the cookie `hr_login` and sends the browser to Steam with `return_to=https://<host>/auth/steam/return?login=<id>`.
2. Steam asks for the password and sends the browser back. The server needs the `hr_login` cookie to match `login` (another browser or a replayed URL fails), then asks Steam to confirm (`check_authentication`) and accepts only an assertion that Steam confirms, whose `return_to` is exactly its own, whose `op_endpoint` is Steam's, whose `claimed_id` is `https://steamcommunity.com/openid/id/<17 digits>` and equals `identity`, whose signature covers `op_endpoint`, `claimed_id`, `identity`, `return_to`, `response_nonce` and `assoc_handle`, with no repeated `openid.*` field, and whose `response_nonce` is at most 5 minutes old (or ahead) and not seen before (kept in memory).
3. A known Steam id gets a session: the cookie `hr_session` (HttpOnly, Secure, SameSite=Lax, 30 days; its SHA-256 is in `web_sessions`, a table of its own, so a cookie is never a game token and a game token never a cookie) and a redirect to `next` (a local path; anything else becomes `/`).
4. A new Steam id is sent to `/auth/handle`, where the player picks a handle (Steam gives none we keep). The account is created with the next user id of the server's range (below) and the server's starting data, then step 3. Two tabs of one sign-in make one account.

**The launcher's start page** is `GET /` on `signup_host` (the launcher loads it with `#/launcher/start`; the hash never reaches the server). The old launcher's browser (Chromium 25, TLS 1.1) cannot be expected to load Steam's pages, so the page signs in through the player's own browser, in the style of a device code: the page asks `POST /auth/link` for a code and opens `https://<signup-host>/auth/steam/login?link=<code>` with `ewlauncher.openURL` (a second link on the page signs in inside the launcher's window instead, which may fail). The player's browser runs steps 1 to 4, then shows "Sign in the launcher that shows the code `ABCD-EFGH`?" (the first 8 characters of the code); the player confirms, and the launcher, polling `GET /auth/link/<code>` every 2 s, gets `ok` with the `hr_session` cookie once and reloads. The confirmation matters: without it, anyone could send a victim a link made from their own code. Signed in, the page shows "Play": `POST /auth/launch` returns `{"url": <launch URL>}`, and the page calls `ewlauncher.startGame(url)`. The page is plain HTML and ES5, English only.

**The launch URL** is `<scheme>://<gameserver_host>/<session>/lang/en_US/access_token/<access>/refresh_token/<refresh>`, three fresh tokens of the signed-in account; the scheme (`ProtocolLiterals.LaunchScheme`, the one literal that names the game) carries the name the client's environment check looks for anywhere in the URL. `gameserver_host` must not contain `dev` or `beta` (the same check). Whether the client takes a scheme other than the one it was shipped with is untested.

**Tokens** (all in `tokens`, hashed; every token resolves everywhere: the lifetimes limit a leak, they are not different powers):

| Token | Handed out by | Lifetime |
|---|---|---|
| session (`X-Hun-Session`) | `Launch/start`, the start page | 7 days |
| access (`oauth_access_token`) | `Launch/start`, the start page, `Oauth/token` | 1 day |
| refresh | `Launch/start`, the start page | 30 days; `Oauth/token` (`oauth_refresh_token`) returns a new access token and the same refresh token |
| JWT (`Bearer`) | `User/jwt`, on the game host and the API host, for the `oauth_access_token` of its JSON or form body | 30 minutes; three dot-separated parts whose payload has `sub`, `iat` and `exp` (the client reads `exp`); the third part is random, not a signature |
| web session cookie | the sign-in | 30 days |

Expired rows are deleted at startup. `Launch/start`, `User/jwt` and `Oauth/token` answer a request without a valid token as every other state endpoint does (the expired-token reply).

**The launcher's own calls** (`EmoteLauncher/token`, `initialise`, `compatibleHardware`, `ping`, under the game host) are answered without recordings: `err` 0, placeholder `access_token` and `refresh_token` (the launcher only needs them to be something other than `"0"`), no files to download, `compatible` 1. The Steam session ticket that `token` receives is not checked: only the game's publisher can verify it (with a publisher Web API key for the game), so any client could send any ticket. Signing in is the browser's Steam sign-in above.

**User ids**: an account of the server's own gets the next id from 2,000,000,000 (`Store.FirstServerId`) to 2,147,483,647 (int32, the game's width); ids that come from a backup are below that, so they never collide. The allocation and the insert are one step under a lock (one server process, [ADR 0001](adr/0001-sqlite-for-profiles.md)). A restore that brings a user id and handle replaces the id and handle of an account that has not played (no row in `expeditions` and `events`, only its starting rows); it must not change the id of an account that has. The rest of the account (Steam id, tokens, sessions) stays.

**Handles**: 3 to 20 characters, ASCII letters, digits, `-` and `_`, starting with a letter or a digit; unique whatever the case (`Handles.Problem`, the column is NOCASE).

**Endpoints** (the OpenAPI document `game` lists them): `GET /auth/steam/login`, `GET /auth/steam/return`, `GET|POST /auth/handle`, `GET|POST /auth/confirm`, `POST /auth/link`, `GET /auth/link/<code>`, `GET /auth/me` (`{userId, handle}`, or 401), `POST /auth/signout`, `POST /auth/launch`, `GET /` on `signup_host`, on both hosts. A state-changing request must come from a page of the same origin: its `Origin` is the origin, or it has no `Origin` and carries `X-Requested-With`. The website reaches them through the proxy; the website's own use is in [website.md](website.md#sign-in).

A restart forgets the logins in progress (the player starts the sign-in again); sessions, accounts and tokens are in the database.

## Behind a reverse proxy

A proxy in front of the server speaks plain HTTP to it (a listener without `cert`). The game routes by host name, which a proxy hides, so a local forwarder in front of the proxy puts the name the client asked for in a request header. The server reads `host_header` (default `X-Game-Host`) and routes by its value, only when the peer address is in `trusted_proxies`; from any other peer the header is ignored. A value that is not a host name (optionally with a port) is ignored too.

## Admin API

`/admin/*`, Swagger UI (`/swagger`) and the OpenAPI documents are for a person at the server's machine. A request is served only when all of these hold: its `Host` is `localhost` or `127.0.0.1`; it came in on a listener marked `"admin": true`; its peer address (not a header) is a loopback address; and it carries no header that says a proxy relayed it (`X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `Forwarded`, `Via`, `X-Real-IP`, the `host_header`). Any other request for those names gets 403. No listener is an admin listener unless marked, and a proxy on the same machine is a loopback peer too, so keep the listener a proxy connects to unmarked and give the admin API its own loopback listener (for example `127.0.0.1:8081`, reached from the VPS through an SSH tunnel). Admin bodies are limited to 64 KiB.

## Tests

`dotnet test --project server/Server.Tests -c Release`: xUnit v3, no network and nothing outside a temporary folder. Fixtures are built in code. CI runs them on `windows-latest` (with the rest of the solution) and on `ubuntu-latest` ([CONTRIBUTING.md](../CONTRIBUTING.md#ci-and-updates)).
