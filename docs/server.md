# Server

`server/Server` is the game server: the local server a player runs on their own PC, and the code of the community server. Tests: `server/Server.Tests`. One SQLite database per server ([ADR 0001](adr/0001-sqlite-for-profiles.md)).

At this stage it answers the hunt itself (connect, heartbeat, kills, harvest, score, purchases, expeditions, chat notices, photo upload) from a player state, and answers everything else from what the operator supplies: the game's install archives, recorded replies and a mirror folder. It needs no recordings to start: the player state is built from the operator's own install and the facts file ([Starting data](#starting-data)). The current game build (one web host, signed tokens) is described in [The current build](#the-current-build).

## Run it

```
dotnet run --project server/Server -c Release -- [--settings <file>] serve | admin ... | import backup ... | review ... | coverage | backup <folder> [keep]
```

`serve` is the default. The settings file is `--settings <file>`, else the environment variable `SERVER_SETTINGS`, else `settings.json` in the current folder. It is never committed (`.gitignore`); start from `server/Server/settings.example.json`. Relative paths are relative to the settings file.

| Key | Meaning |
|---|---|
| `web_host` | Required. **Our website's public domain**: the sign-in pages and the account pages are served under it (the website forwards the browser's `Host`, and the Steam return address is built from it) |
| `game_web_host` | Required. **The game's own web host**: the one name the current build sends everything to ([The current build](#the-current-build)). It is not our domain: a player's hosts file points it to their PC, and the request reaches the server in the `host_header` header |
| `crash_host` | The host the game's crash reports go to (answered 204). Recommended |
| `gameserver_host`, `api_host`, `apiv2_host`, `signup_host`, `static_host`, `static_legacy_host`, `map_host`, `photo_host`, `avatar_host`, `news_host`, `telemetry_host` | The names the old build asked for; only needed for it. Left out, each is a placeholder (`<role>.invalid`) that no client asks for. The server routes by the `Host` of a request |
| `install_dir` | Required. The game's install folder: its archives answer image and map requests (`game/archives`) and hold the item and species definitions, and its sample files (`game/json_connect_results.json`, `game/json_inventory.json`) seed the connect reply and the item definitions ([Starting data](#starting-data)). Read only |
| `data_dir` | Default `data`: `state.db`, `spawn.json`, `photos/<account id>/`, the miss log `misses.jsonl` and `telemetry.jsonl` |
| `recordings_dir` | Default `recordings`: Optional: HAR files the operator recorded of their own traffic. The newest 200 reply per request wins |
| `mirror_dir` | Default `mirror`: saved static pages, `<mirror_dir>/<host>/<path>` |
| `variants_file` | Optional: a JSON list of `{id, min_score, max_score, css_multiplier}` per trophy variant for the score sheets |
| `offline_mode` | Default `true`: a request that no token resolves is the local account's (a player's own PC). `false` on a community server: it gets the reply of an expired token, see [Accounts](#accounts) |
| `listeners` | Required: a list of `{address, port}`; add `cert` and `key` (PEM files) for HTTPS. Plain HTTP is a listener without them. `"admin": true` (loopback addresses only) makes it serve the admin API, see below. Every listener speaks HTTP/1.1 only |
| `trusted_proxies` | Addresses or `address/prefix` of the proxies in front of the server (default none) |
| `host_header` | Default `X-Game-Host`, see below |
| `max_body_bytes` | Default 16 MiB: the request body limit. The admin API's is 64 KiB |

`serve` needs no change to the PC: it does not read or require the Windows hosts file. How the client is pointed at the server is not part of the server.

`admin item <item_id> [count]`, `admin em|gm <amount>` and `admin spawn <reserve> <animal> <males> [females] [x z radius]` change the player in `data_dir` from the command line; `coverage` replays every request of the recordings through the server and lists what it cannot answer, naming hosts by their settings key and masking ids and tokens in paths. `coverage --no-recordings` replays them against the server as it starts without recordings and without the mirror (player state from the install and the facts file, on a temporary database): what it lists as unanswered still needs a recording.

`backup <folder> [keep]` copies every `*.db` of `data_dir` to `<folder>/<name>-<yyyymmdd>.db` with SQLite's `VACUUM INTO` (consistent while the server runs) and keeps the newest `keep` (default 7) copies of each; the community server's daily backup is this command ([deploy.md](deploy.md#backups)).

## How a request is answered

1. A request for `telemetry_host` or `crash_host` gets 204 and is kept in `telemetry.jsonl`.
2. The player state answers what it owns: `heartbeat`, `disconnect`, `connect`, the photo upload and photo files on the game host; `Inventory/move_multiple2`, `Inventory/buy`, `Launch/start`, `User/jwt`, `Oauth/token`, `Chat/conversations`, `Chat/conversation`, `Skill/list`, `Statistics/total`, `Achievement/stats` (the last three for a restored account's own user id, see [Import a backup zip](#import-a-backup-zip)), the lists that wrap a base reply (`Splash/list_active`, `Public_user/expedition`, `Expedition/list`, `Me/poll`, `Me/me`: the recorded reply on a local server, the empty shape on a community server) and, on a community server, `Inventory/list` and `Trophy/list` on the API host; the harvest pages and the restored trophies and lodges on the API v2 host; the sign-in and start page on the website and signup hosts, and the old launcher's own calls on the game host ([Sign-in and tokens](#sign-in-and-tokens)); the kill details page on the website host.
3. Everything else, in this order: an archive file in the URL's own folder, the recorded reply (a JSONP callback is swapped; on a community server only for a global endpoint, [below](#recordings-on-a-community-server)), the mirror, an archive file found by its name, an empty 200 for a request that was only recorded as unchanged, else a miss: `null` under `/v1/`, `{"err": 0}` on the game host, 404 on the API v2 host. A miss is written to the miss log, keys only.

The state answer wins; a handler that has nothing to say returns control to step 3. On a local server a reply is never made up where a recorded one exists.

## Recordings on a community server

A recording holds one player's replies, so with `offline_mode` false step 3 replays a recorded reply only for a **global** endpoint, one that is the same for everyone. Every other request on the API, API v2, game and website hosts is **per player**: the state answers it from the database, or it gets the empty reply of its shape, and a path the table does not list gets the miss reply (default deny). The table is `ReplayScope.Rules`; a test checks that no per-player rule ever returns recorded bytes. The asset hosts (static, map, photo, avatar, news, signup) and the mirror are not affected. With `offline_mode` true nothing changes: the recordings are the operator's own.

| Host | Global (recorded reply) | Per player, from the database | Per player, empty reply |
|---|---|---|---|
| API (`/v1/`) | `Application/application`, `Application/discounts`, `Store/products2`, `Splash/list_active`, the two `get_active_stylesheet`, `Membership/get_type`, `Membership_Bundle/get_active`, `Avatar/getAvatars`, `Challenge/get_challenges`, `Sidebar_Counter/getTemplate` | `Me/poll`, `Me/me` (every account: the restored profile, else a new account's), `Expedition/list`, `Public_user/expedition`, `Inventory/list`, `Trophy/list` (the restored trophies), `Skill/list`, `Statistics/total`, `Achievement/stats` (own user id only), `Chat/conversations`, `Chat/conversation`, `Inventory/buy`, `Inventory/move_multiple2`, `Launch/start`, `User/jwt`, `Oauth/token` | another player's user id on the reads above; friends, wallet histories, `Inventory/dict`, gallery, network list, missions, challenges, leaderboards, competitions, `Public_user/getByHostname`, message settings, and the writes of these pages (answered with a failure) |
| API v2 | | the harvest pages (a server kill, else a restored harvest), the account's restored trophies (`/game/user/trophies`) and lodges (`/game/user/<id>/lodges[/<lodge>]`, own user id only) | `loadout`, profile and ranks pages, any of the above the account has none of (empty list, or 404 for one record) |
| Game | the client's ping, `POST /` | heartbeat, connect, disconnect | |
| Website | `static`, `content`, `dist`, `feed`, the splash feed, the launcher ping | `/user/<handle>/score/` | every other path (the miss reply; a GET still finds a page of the operator's mirror folder) |

The website's `/v1/` paths are the API's, in any case. Empty replies are the shapes in the table (`[]`, `{}`, `null`, or an object with empty members). A new account's connect reply and `Me/me` carry its own ids, handle and empty statistics, never the recorded player's. Another player's data is not served yet (a profile page of a friend shows empty); missions and competitions are the operator's to decide.

## The current build

The current client build sends all its traffic to `game_web_host`, with lower-case paths and JSON bodies. `NewBuild.Rewrite` (before routing) turns a request for that host into the one the handlers know, so every handler, recording and rule below is the old one:

| Request on `game_web_host` | Becomes |
|---|---|
| `/v1/json/<service>/<method>` (the game's RPC: `connect`, `heartbeat`, `disconnect`) | the game server's path and host; the body stays JSON. Other `/v1/json/...` paths stay on the web host (the launcher's ping) |
| any other `/v1/<module>/<method>`, any case | the API host, the path in the case of the handlers (or the recordings'); a JSON body becomes a form (`a=1&b=2`, nested values as their JSON text) |
| everything else (`/client`, `/static/...`, `/content/...`, `/media/...`) | unchanged: the archive, the recordings, the mirror (`<mirror_dir>/<game_web_host>/...`) answer it |

The server's own answers for this build:

| Path | Answer | Replay scope |
|---|---|---|
| `POST /auth/jwt/refresh` | A new access JWT (`exp` = now + 900 s) and refresh JWT, in the body (`expires_in`, `access_token`, `refresh_token`) and the access and refresh cookies (the refresh one for the path `/auth/jwt/refresh` only). The only credential is a refresh JWT this server signed (`typ` refresh: body `refresh_token` or the cookie); an access JWT, a session id or any other token gets 401. Nothing valid: 401. A local server: the local account. `scp` is 4 for `X-App-Id: client`, else 1 | - |
| `POST /auth/emotelauncher/token` | Always 401 `auth.invalid_steam_ticket` (a Steam ticket cannot be checked, [above](#sign-in-and-tokens)): the launcher falls back to the page | - |
| `GET /launcher` | The start page ([above](#sign-in-and-tokens)): sign in, then `ewlauncher.startGame(<launch URL>)`. A local server: the recorded page | global |
| `POST /v1/session/start_single_player` | `{session_id, network_id}` (the session is the first segment of the launch URL a launcher builds from it, and the game's `X-Hun-Session`) and the session and network id as cookies; opens the expedition at `lodge_id` in `reserve` | - |
| `connect`, `heartbeat` | As before (they read `X-Hun-Session`, else `Authorization: Bearer`). The first heartbeat's `event 6` rows with quantity 0 use nothing up | - |
| `POST /v1/application/client_application` | The recorded reply, else the catalogue of the player state (species with their permitted ammo, items, reserves, locations; missions only if the recording has them) | global |
| `GET /v1/splash/feed_data` | `{posts, splashes}`: the recording with the server's notice first in `splashes`, else only the notice | global |
| `GET /v1/application/application` | The recorded reply (recordings are matched by method, so one of the current build's GET; none without a recording) | global |
| `GET /v1/game/user/loadout` | `[]` | per player, empty reply |
| `POST /v1/mission/missions` | `{active, completed, available, states}`, all empty | per player, empty reply |
| `POST /v1/network/list` | `[]` | per player, empty reply |
| `POST /v1/inventory/move_multiple2`, `inventory/buy`, `me/me`, `me/poll` (adds `active_campaign`, `campaign_stylesheet`, `community_campaign_stylesheet`, `last_hunt_update_ts`), `expedition/list` (`has_more` next to `total`) | The old handlers | per player, from the database |
| `/client`, `/static/...`, `/content/...`, `/media/...` | The mirror | global |

**Recordings of the current build** are found too: `NewBuild.Alias` adds, for every recorded request to `game_web_host` that the rewrite would move, the key of the moved request (the original stays).

**The old build** is no longer served by the server's own start page and connect reply: the launch URL, `config.domains`, `network` and the photo links are the current build's. Its other handlers keep their paths.

**Tokens.** The launch URL is `<scheme>://<game_web_host>/<session>/lang/en_US/access_token/<JWT>/refresh_token/<JWT>`. Both JWTs are HS256-signed with a key the server makes in a file beside the database (`state.db.jwtkey`) and carry `sub`, `user_id` (the account), `iss`, `scp`, `typ` (`access`/`refresh`), `iat`, `exp`, `jti`. Each is also stored in `tokens` like every other token, so a ban or a delete ends it, and every endpoint that takes a token takes them. The client does not check the signature; the server does when a refresh token comes back. An expired token is never sent: the game crashes on one.

**Sign-in on two hosts.** On our website (`web_host`) it is as above. In the launcher (`game_web_host`, through the player's forwarder) the page asks `POST /auth/link` for a code; the reply's `url` points the player's own browser to `https://<web_host>/auth/steam/login?link=<code>`, because only our domain has a certificate the browser trusts. The browser signs in and confirms there, and the launcher, polling `GET /auth/link/<code>` on the game host, receives the session cookie for that host.

**Settings the current build needs**: `web_host`, `game_web_host` and `crash_host`. The player's kit redirects `game_web_host` and `crash_host` only; `web_host` is ours and is never redirected.

## Starting data

`serve` builds the player state from three sources and needs none of the recordings. For each input the first source that has it wins (a recording never loses to the install, and the install never loses to the facts file), except where noted:

| Input | 1. Recording | 2. Install (`install_dir`) | 3. Facts file |
|---|---|---|---|
| Connect reply (the game data, and the local account's start) | the newest recorded `connect` | `game/json_connect_results.json`, with the sample account replaced (below) | - |
| Application catalogue (`species`, `items`, `reserves`, `locations`) | the recorded `Application/application` | `species.json` and `item.json` of the archives | reserves, lodge positions and the enum tables (`types`, `groups`, `categories`, `client_types`) |
| `populations` and `species_ammo` of a reserve | the newest connect after a `Launch/start` of that reserve | - | `reserves[].populations` and `species`; the ammo of a species is its entry of the connect's `ethical`, also over a recorded connect (a recorded one may hold a seasonal event's ammo) |
| Kill page labels and texture names | the recorded locale and web scripts | - | `labels`, `textures` |
| Item definitions of update 10 | the recorded connect's | `game/json_inventory.json`, then the catalogue above | - |
| Discounts, the player's skill list | recorded only (without them: no discounts, fresh skills) | - | - |
| Animal ids, score families | - | - | `animals`, `families` (always) |

With recordings that cover every input, the replies are the ones the recordings gave, except that the connect reply always has `ingame_event` null (no seasonal event) and `species_ammo` from the `ethical` list. Without a recorded connect, `connect` is the install's sample made neutral: the account, `account_info`, `network`, `user_stats` and `boost` of the sample's test player are replaced (handle `hunter`, user id 1, no e-mail, Steam id, address or counters; `config.domains` names the settings' hosts), `spawn` and `competitions` are empty and everything that is game data (species, items text, locations, store, ethical ammo, achievements, ...) stays. `serve` says `player state: off` only when there is neither a recorded connect nor a connect sample in the install.

**The starting profile of a new account** is neutral and the same for everyone, recordings or not: the items of `start.items` of the facts file, one each in the first free slot of its type above the backpack and the pockets (a full stack for items with a stack size; the client crashes on a kit item in the backpack or pockets), the wallet of `start.wallet` and every skill at level 1 (no weapon skill rows until a first hit), and an `account` object made from the install's sample (no avatar, name or other field of the operator's recorded account). Today that is a single-shot shotgun with a stack of shells, binoculars, a T-shirt, 1000 `em` and 0 `gm`. Change the facts file to change it; a restored backup replaces it. Only the local account of a local server starts from a recorded connect. A community server (`offline_mode` false) with a recorded connect and no connect sample in the install does not start.

Still answered only from recordings (without them: a miss, `null` under `/v1/`): the launcher and website pages and their assets, the sign-up host, the avatar and photo hosts, news, `Launch/start` (its tokens), `User/jwt`, the lists that wrap a recorded base reply (`Me/poll`, `Me/me`, `Expedition/list`, `Public_user/expedition`, `Splash/list_active`) and the profile, statistics, wallet-history, mission, challenge, leaderboard and friend pages. `coverage --no-recordings` prints the exact list.

## The facts file

`server/facts/facts.json` holds what the install does not carry, in this project's own format; it is copied next to the server (`facts/facts.json`). One JSON object, `"format": 1`:

| Key | Content |
|---|---|
| `animals` | The animal ids in catalogue order; the index is the animal's id and the index of `spawn_licenses` (the admin spawner's names) |
| `families` | `{family: [species id]}`: which score formula a species uses (`Scoring`) |
| `reserves` | `[{id, name, species: [species id], populations: [...]}]` for all 12 reserves. `species` is the species of the reserve (the keys of its `species_ammo`). A population is `{index, animal, density?, reserve?, endangered, license, groups: [{x, z, radius, males, females, enabled?}], random?: {males, females, min_size, max_size, avg_size, max_males, max_females, radius, single_gender}}`; `density` (default the animal), `reserve` (default the reserve; `-1` for the rows every reserve carries, the dogs) and `enabled` are left out when they have their default. The loader writes the client's rows from them (`Facts.ReserveConnect`) |
| `locations` | `[{id, reserve, type, x, y, z}]`: the start places and where they are |
| `types` | `{item type: {id, slots, capacity}}`: the numeric type of an item type and the slots and capacity of its items |
| `groups`, `categories`, `client_types` | `{name: number}` tables for the item definitions of the archives; an item without a client type has its type's number |
| `labels` | `{locale key: English text}`: the words of the kill page (poses, animal states, body parts, organs, textures) |
| `textures` | `{texture id: name}` |
| `start` | `{items: [item id], wallet: {currency: amount}}`: the starting profile |

The file holds names, numbers and positions of the game's world only: no id or handle of a person, no saved reply of a server and no host name. The install is the source of everything it already carries; add to the facts file only what no install file has.

## Accounts

Every player is in `state.db`. `accounts` holds the game's user id, the Steam id (optional, unique), the handle (unique, case does not matter), the time of the one-shot claim, a banned flag and the connect reply's `account` object; `tokens` maps a token to an account and keeps only its SHA-256 hash. No e-mail, real name or address is stored. Every per-player table has an `account_id`. The ids a restored backup brings (item guids, kill ids) are unique per account, so two accounts can both own item 100 or kill 1; the ids the server hands out (expeditions, photos) are unique in the whole database and always looked up together with the account. A new account starts with the connect template's inventory, wallet and skills (`State.CreateAccount`; [Starting data](#starting-data)). A database from before accounts is refused: delete it.

A request's account comes from its token. The tokens it carries are tried in this order, the first that resolves wins: the `X-Hun-Session` header (game host), `Authorization: Bearer` (API v2 host), `oauth_access_token` in the form or the query (API host). A token that is unknown, expired or of a banned account does not resolve. When none does, the request is the local account's if `offline_mode` is true, else it is refused: `{"errorCode": 5, "message": "token expired"}` with status 200 on the API host, status 401 on the game host (`{"err": 1}`) and on the API v2 host. A CORS preflight carries no token and is answered by the replay. Photo links name the account (`/photos/<account id>/<file>`) and need no token; the kill details page finds the account by the handle in its URL (on a local server any handle is the local account). The `admin` commands and the admin API act on the local account.

The database is in WAL mode with `synchronous=NORMAL` and a 5 s busy timeout, connections are pooled, every transaction starts with `BEGIN IMMEDIATE`, and the schema and its migrations run once at startup under `PRAGMA user_version`. A database call is scoped to one account: the SQL names it as `$a`, and a statement that touches a per-player table without it throws, so a forgotten filter fails in the tests and never reads another player's rows.

## Sign-in and tokens

With `offline_mode` false (a community server) players sign in with Steam; the endpoints that act for a player need a token that resolves (recorded replies, archives, the mirror, the launcher's calls and the start page are public). With it true none of the following is on (the `/auth/*` endpoints, the start page, the launcher's calls and the replies that carry tokens are the replay's, as before). Steam's OpenID 2.0 needs no key. No Steam name, e-mail or address is read or kept: only the 17-digit Steam id, the handle the player picks and the game data.

**The sign-in, in steps** (on `web_host` for the website, on `signup_host` for the launcher; the origin is `https://<that host>`):

1. The browser opens `/auth/steam/login?next=<local path>`. The server keeps a login in memory (15 minutes), sets the cookie `hr_login` and sends the browser to Steam with `return_to=https://<host>/auth/steam/return?login=<id>`.
2. Steam asks for the password and sends the browser back. The server needs the `hr_login` cookie to match `login` (another browser or a replayed URL fails), then asks Steam to confirm (`check_authentication`) and accepts only an assertion that Steam confirms, whose `return_to` is exactly its own, whose `op_endpoint` is Steam's, whose `claimed_id` is `https://steamcommunity.com/openid/id/<17 digits>` and equals `identity`, whose signature covers `op_endpoint`, `claimed_id`, `identity`, `return_to`, `response_nonce` and `assoc_handle`, with no repeated `openid.*` field, and whose `response_nonce` is at most 5 minutes old (or ahead) and not seen before (kept in memory).
3. A known Steam id gets a session: the cookie `hr_session` (HttpOnly, Secure, SameSite=Lax, 30 days; its SHA-256 is in `web_sessions`, a table of its own, so a cookie is never a game token and a game token never a cookie) and a redirect to `next` (a local path; anything else becomes `/`).
4. A new Steam id is sent to `/auth/handle`, where the player picks a handle (Steam gives none we keep). The account is created with the next user id of the server's range (below) and the server's starting data, then step 3. Two tabs of one sign-in make one account.

**The launcher's start page** is `GET /` on `signup_host` (the launcher loads it with `#/launcher/start`; the hash never reaches the server). The old launcher's browser (Chromium 25, TLS 1.1) cannot be expected to load Steam's pages, so the page signs in through the player's own browser, in the style of a device code: the page asks `POST /auth/link` for a code and opens `https://<signup-host>/auth/steam/login?link=<code>` with `ewlauncher.openURL` (a second link on the page signs in inside the launcher's window instead, which may fail). The player's browser runs steps 1 to 4, then shows "Sign in the launcher that shows the code `ABCD-EFGH`?" (the first 8 characters of the code); the player confirms, and the launcher, polling `GET /auth/link/<code>` every 2 s, gets `ok` with the `hr_session` cookie once and reloads. The confirmation matters: without it, anyone could send a victim a link made from their own code. Signed in, the page shows "Play": `POST /auth/launch` returns `{"url": <launch URL>}`, and the page calls `ewlauncher.startGame(url)`. The page is plain HTML and ES5, English only.

**The launch URL** is `<scheme>://<game_web_host>/<session>/lang/en_US/access_token/<access JWT>/refresh_token/<refresh JWT>` ([The current build](#the-current-build)), three fresh tokens of the signed-in account; the scheme (`ProtocolLiterals.LaunchScheme`, the one literal that names the game) carries the name the client's environment check looks for anywhere in the URL. `game_web_host` must not contain `dev` or `beta` (the same check).

**Tokens** (all in `tokens`, hashed; every token resolves everywhere: the lifetimes limit a leak, they are not different powers):

| Token | Handed out by | Lifetime |
|---|---|---|
| session (`X-Hun-Session`) | `Launch/start`, the start page | 7 days |
| access (`oauth_access_token`) | `Launch/start`, the start page, `Oauth/token` | 1 day |
| refresh (starts with `r`) | `Launch/start` | 30 days; the only ones `Oauth/token` renews; `Oauth/token` (`oauth_refresh_token`) returns a new access token and the same refresh token |
| JWT (`Bearer`) | `User/jwt`, on the game host and the API host, for the `oauth_access_token` of its JSON or form body | 30 minutes; three dot-separated parts whose payload has `sub`, `iat` and `exp` (the client reads `exp`); the third part is random, not a signature |
| access JWT (`Bearer`, `oauth_access_token`) | `/auth/jwt/refresh`, the launch URL ([The current build](#the-current-build)) | 900 s; HS256-signed, kept in `tokens` |
| refresh JWT | the same, the cookie | 30 days; renews only through `/auth/jwt/refresh`; not rotated (the game may keep presenting the one of its launch URL), so it ends with its 30 days, a ban or a delete |
| web session cookie | the sign-in | 30 days |

Expired rows are deleted at startup. `Launch/start`, `User/jwt` and `Oauth/token` answer a request without a valid token as every other state endpoint does (the expired-token reply).

**The launcher's own calls** (`EmoteLauncher/token`, `initialise`, `compatibleHardware`, `ping`, under the game host) are answered without recordings: `err` 0, placeholder `access_token` and `refresh_token` (the launcher only needs them to be something other than `"0"`), no files to download, `compatible` 1. The Steam session ticket that `token` receives is not checked: checking it takes a Steam Web API key (Valve's documentation asks for the game's publisher key, and one page also allows an ordinary key on the public host, rate limited), and the server runs without any key, so any client could send any ticket. Signing in is the browser's Steam sign-in above.

**User ids**: an account of the server's own gets the next id from 2,000,000,000 (`Store.FirstServerId`) to 2,147,483,647 (int32, the game's width); ids that come from a backup are below that, so they never collide. The allocation and the insert are one step under a lock (one server process, [ADR 0001](adr/0001-sqlite-for-profiles.md)). The rule for the restore (not built yet): a restore that brings a user id and handle may replace the id and handle of an account that has not played (no row in `expeditions` and `events`, only its starting rows); it must not change the id of an account that has. The rest of the account (Steam id, tokens, sessions) stays.

**`web_host` must be the website's public domain** (the website sends the host the browser used, and the server picks its sign-in by it); the game's own web host is `game_web_host`.

**Handles**: 3 to 20 characters, ASCII letters, digits, `-` and `_`, starting with a letter or a digit; unique whatever the case (`Handles.Problem`, the column is NOCASE).

**Endpoints** (the OpenAPI document `game` lists them): `GET /auth/steam/login`, `GET /auth/steam/return`, `GET|POST /auth/handle`, `GET|POST /auth/confirm`, `POST /auth/link`, `GET /auth/link/<code>`, `GET /auth/me` (`{userId, handle}`, or 401), `POST /auth/signout`, `POST /auth/launch`, on `web_host`, `signup_host` and `game_web_host`; `GET /` on `signup_host` and `GET /launcher` on `game_web_host` (the start page); `POST /auth/jwt/refresh` and `POST /auth/emotelauncher/token` on `game_web_host` only. A state-changing request must come from a page of the same origin: its `Origin` is the origin, or it has no `Origin` and carries `X-Requested-With`. The website reaches them through the proxy; the website's own use is in [website.md](website.md#sign-in).

A restart forgets the logins in progress (the player starts the sign-in again); sessions, accounts and tokens are in the database.

## Import a backup zip

```
dotnet run --project server/Server -c Release -- [--settings <file>] import backup <zip> [--account <id>]
```

Restores a [backup zip](backup.md) into `state.db` in `data_dir` and prints the report: what the reader found (restored, unsupported, missing, warnings), the rows stored per table, and what was dropped and why. It runs on its own, with no server running, and needs the recordings like `admin` does. Any zip is accepted, and it replaces the account's profile (items, wallet, skills, profile, statistics, achievements, trophies, lodges, saved replies); hunt history stays, no merge. The old database is copied first to `state.db.bak-<UTC time>`; the zip is kept as `restores/<account id>/<SHA-256>.zip`; importing the same zip twice gives the same rows (and a second audit row). The account is the zip's user id (created from the zip's handle when it does not exist), `--account <id>` names an existing one, and a local server (`offline_mode` true) restores into the local account, the one the game connects as. Exit code 1 for a refused zip, an unreadable file, a missing `--account` target or a handle another account has. The model, the dropped fields and what the server then answers from the rows are in [docs/restore.md](restore.md#storing-the-model).

## Upload a backup zip

On a community server (`offline_mode` false) a signed-in player uploads their own backup from the website ([docs/website.md](website.md#restore-and-your-data)). The website's server calls these endpoints over `SERVER_URL`, with the player's session cookie (`hr_session`), on `web_host` under `/account/`. An upload is never restored by itself: an operator approves it (below). A browser never calls them itself, so the reverse proxy does not have to route `/account/` to the server (and `/auth/` still has to be). With `offline_mode` true they do not exist (the replay answers), and `import backup` above is the local way.

| Endpoint | |
|---|---|
| `GET /account/restore` | `{restoredUserId, review: [{ts, note}]}`: the backup user id this account restored (once), or null, and the zips kept for the operator. 401 when signed out |
| `POST /account/restore` | The zip is the request body, at most 64 MiB. Answers `{status, message}` (below). Needs the cookie and `X-Requested-With` or a same-origin `Origin` (a form of another site cannot set either) |
| `POST /account/delete` | Form `confirm=delete`, same checks: deletes the account and all its data (below), clears the cookie |

**Limits.** One upload at a time per account and four on the whole server (`429`, status `busy`); a body over 64 MiB is `413` (`too_large`) and is not read; the reader's caps apply (`Limits`, with 256 MiB of content instead of 1 GiB, 100:1 compression, 20,000 entries, JSON depth 64), so a zip bomb is `400` (`invalid`) before anything is parsed. An account may have 3 zips waiting for an operator; a fourth is `429` and not kept. The zip is held in memory and in a temporary file of `data_dir/uploads-tmp`, deleted when the upload ends.

**Rules.** Every zip that the reader accepts is kept for an operator and never restored automatically. Whether the zip's Steam id is the one the player signed in with is shown to the operator as information, not as proof: anyone holding the zip can edit the field.

| The zip | Result | `status` |
|---|---|---|
| Not a zip, or refused by the reader (caps, unsafe names, no manifest, a claim key that does not match) | `400`, nothing kept | `invalid` |
| A user id that was restored before (by this account or any other) | Refused, nothing kept: "a backup can be restored once" | `already` |
| A user id another account holds | Refused, nothing kept | `taken` |
| Any other zip | Kept as `restores/<account id>/<SHA-256>.zip`, a row in `uploads`; nothing is restored. The player is told that an operator will check it | `review` |

**Review.** The operator works the kept zips with the server's command line (no web page):

```
dotnet run --project server/Server -c Release -- [--settings <file>] review list | approve <id> | reject <id>
```

`list` prints, per kept zip: its id, the account, the zip's user id and handle, the time, the note, and whether the zip's Steam id equals the account's verified one (information only). `approve <id>` runs the restore with the rules below and prints the restore report or the reason it refused; `reject <id>` deletes the kept zip and its `uploads` row. Like `import backup` it runs on its own and needs the recordings.

| `approve` | Result |
|---|---|
| The user id was not restored before, is not held by another account, is from 1 to 1,999,999,999, and the account has not played (no row in `expeditions` and `events`) or already has that user id | Full restore, once per game user id: the profile rows are replaced, the account takes the zip's user id and (when it is a plain name of at most 32 characters nobody else has; otherwise it keeps its own) the zip's handle. The Steam id, tokens and sessions follow it. One audit row, one row in `restored_ids`; the row in `uploads` goes, the zip stays. The whole database is **not** copied (`Store.Backup` is the local rule): the kept zip is the way back |
| A user id restored before, held by another account, outside that range, or an account that has played and would have to change its user id | Refused with the reason; the zip stays kept until the operator rejects it |

A user id is restored once, enforced by the primary key of `restored_ids`, so two uploads at the same moment cannot both win. If a restore turns out wrong, the operator deletes the account's rows (or the account) and imports the kept zip again with `import backup <zip> --account <id>`; deleting the account frees its user id.

**Delete my data.** `POST /account/delete` deletes the `accounts` row, and with it, by the foreign keys, every per-player row, the tokens, the cookie sessions, the review rows and the restored user id; then the account's `restores/<id>/` and `photos/<id>/` folders. The Steam id and handle go with the row, so signing in again makes a new account. It refuses (`429`) while an upload of the account is running. The replies are `{status, message}`; the cookie is cleared.

## Behind a reverse proxy

A proxy in front of the server speaks plain HTTP to it (a listener without `cert`). The game routes by host name, which a proxy hides, so a local forwarder in front of the proxy puts the name the client asked for in a request header. The server reads `host_header` (default `X-Game-Host`) and routes by its value, only when the peer address is in `trusted_proxies`; from any other peer the header is ignored. A value that is not a host name (optionally with a port) is ignored too.

## Admin API

`/admin/*`, Swagger UI (`/swagger`) and the OpenAPI documents are for a person at the server's machine. A request is served only when all of these hold: its `Host` is `localhost` or `127.0.0.1`; it came in on a listener marked `"admin": true`; its peer address (not a header) is a loopback address; and it carries no header that says a proxy relayed it (`X-Forwarded-For`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `Forwarded`, `Via`, `X-Real-IP`, the `host_header`). Any other request for those names gets 403. No listener is an admin listener unless marked, and a proxy on the same machine is a loopback peer too, so keep the listener a proxy connects to unmarked and give the admin API its own loopback listener (for example `127.0.0.1:8081`, reached from the VPS through an SSH tunnel). Admin bodies are limited to 64 KiB.

## Tests

`dotnet test --project server/Server.Tests -c Release`: xUnit v3, no network and nothing outside a temporary folder. Fixtures are built in code. CI runs them on `windows-latest` (with the rest of the solution) and on `ubuntu-latest` ([CONTRIBUTING.md](../CONTRIBUTING.md#ci-and-updates)).
