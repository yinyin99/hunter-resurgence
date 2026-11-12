# Launcher

`src/Launcher/` is a Windows desktop app that will replace the old launcher once the community server exists. It runs beside the old launcher and does not change it. It never writes into the game folder.

It is a Blazor app shown in a WPF window (1280 x 760) through the WebView2 control, so the Microsoft Edge WebView2 runtime must be installed (it comes with Windows 11). The screens are shared with the [website](website.md) and live in `src/Ui/`; the launcher only adds the window and the real services below.

## State

The screens follow the old launcher and its website screen for screen: the same theme, the same places for things, the same navigation and the same English texts (French uses the original French texts). Where the original had a defect or a text that cannot be reused, the screen does the sensible thing instead.

Behind the screens the data is a **mock** (fixed, fictional data and fake actions) unless `server_url` is set; then the services listed under "What is real" are the community server's. The pictures are not included: the launcher shows the operator's pictures from the website at `server_url` ([images.md](images.md)); without it, or when the website does not answer within 5 s, each one is a neutral labelled box. To play on the community server the launcher also sets up the PC and answers the game ([The player kit](#the-player-kit)).

Until the official servers close, a large orange backup tool button shows under the top bar's logo; see [website.md](website.md#backup-tool).

## The service layer

Every screen talks to an interface in `src/Services/Contracts/` (one per area: session, signup, Discord widget, servers, store, wallet, bank, missions, competitions, leaderboards, profile, friends, messages, account, game management, help, patch, game settings, launcher bridge, player kit). `src/Services/Mocks/` implements each of them with fixed data; `AddMockServices()` registers them all. A host replaces a registration with a real one when it exists. The launcher does that for game settings, the player kit, the Discord widget (`DiscordNewsService`, from `link_discord`), and for the website's `Server*` services when `server_url` is set.

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Launcher -c Release
dotnet test tests/Resurgence.Tests -c Release
```

The exe is `resurgence-launcher.exe`. To make the release: `src\Launcher\publish.cmd <your settings.json> [version]` publishes the self-contained exe, copies your `settings.json` next to it and packs `resurgence-launcher.exe`, `settings.json` and `wwwroot` (the page the exe shows; the exe does not start without it) with Velopack (the dotnet tool `vpk`, installed with `dotnet tool install -g vpk --version <the version of the Velopack package in the csproj>`) into `src\Launcher\bin\Release\net10.0-windows10.0.17763.0\win-x64\releases`: `HunterResurgence-win-Setup.exe`, the full package `HunterResurgence-<version>-full.nupkg`, a delta package `HunterResurgence-<version>-delta.nupkg` when the folder already holds the packages of an older release, and `releases.win.json`. The release workflow does this for a tag when the repository secrets `PLAY_SERVER_URL` and `STEAM_APP_ID` are set (without `STEAM_APP_ID` the release has no launcher), with the example `settings.json`, `server_url` from that secret and `steam_app_id`, `client_exe`, `graphics_file`, `options_file`, `options_key_prefix`, `game_web_host`, `crash_host` from the secrets `STEAM_APP_ID`, `LAUNCHER_CLIENT_EXE`, `GRAPHICS_FILE`, `OPTIONS_FILE`, `OPTIONS_KEY_PREFIX`, `WEB_HOST`, `CRASH_HOST` (all required with the launcher; the links and names stay placeholders, i.e. not configured). It first downloads the earlier releases (`vpk download github`) so that the delta can be made, uploads the Setup, the packages of this version and `releases.win.json` to the tag's release, attests the Setup and puts its SHA-256 in the release notes; CI packs the launcher with the example settings, checks the publish folder and the release folder, and runs the Setup silently (see deploy.md).

The launcher's tests (folder `LauncherApp` of `tests/Resurgence.Tests`) use a temporary Steam folder tree, temporary settings files, temporary backup zips, a test website on 127.0.0.1 (the list of pictures), a local update feed (a folder with `releases.win.json` and a package) and a temporary registry key for the link scheme. They read and change nothing else on the PC, with one exception: `LauncherServicesTests`, `LauncherStoreTests` (a purchase through the real client and cookie against a test server on 127.0.0.1) and `LanguageSwitcherTests` build the launcher's real services, so they read `launcher.json` (`LanguageSwitcherTests` points it at a temporary file), the Steam registry keys and the game search the way a start does (missing sources give the defaults); they write nothing but that temporary file. `dotnet test` exits 0 when every test passes.

### Icon

The icon is `src/Launcher/wwwroot/favicon.ico`: the website's paw (`src/Web/wwwroot/favicon.svg`) in #1f1a17 on a #f2ebe3 rounded tile, at 16, 20, 24, 32, 40, 48, 64, 128 and 256 px. It has a light tile because the website's transparent dark paw would barely show on a dark taskbar.

The csproj's `ApplicationIcon` puts it on the exe, and WPF uses the exe's icon for the window's title bar and the taskbar. `wwwroot/index.html` links the same file as its favicon.

When `favicon.svg` changes, regenerate the icon. In an empty folder outside the repo run `npm init -y` and `npm i @resvg/resvg-js`, save this as `make-icon.mjs`, and run `node make-icon.mjs <repo folder>`:

```js
// node make-icon.mjs <repo>: writes src/Launcher/wwwroot/favicon.ico from src/Web/wwwroot/favicon.svg
import { readFileSync, writeFileSync } from 'node:fs';
import { Resvg } from '@resvg/resvg-js';
const repo = process.argv[2];
const paw = readFileSync(`${repo}/src/Web/wwwroot/favicon.svg`, 'utf8').match(/<g class="paw">[\s\S]*?<\/g>/)[0];
const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="12" fill="#f2ebe3"/><g fill="#1f1a17" transform="translate(8 9) scale(.75)">${paw}</g></svg>`;
const sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
const pngs = sizes.map(s => new Resvg(svg, { fitTo: { mode: 'width', value: s } }).render().asPng());
const head = Buffer.alloc(6 + 16 * sizes.length);
head.writeUInt16LE(1, 2);
head.writeUInt16LE(sizes.length, 4);
let offset = head.length;
sizes.forEach((s, i) => {
  const e = 6 + 16 * i;
  head.writeUInt8(s % 256, e);
  head.writeUInt8(s % 256, e + 1);
  head.writeUInt16LE(1, e + 4);
  head.writeUInt16LE(32, e + 6);
  head.writeUInt32LE(pngs[i].length, e + 8);
  head.writeUInt32LE(offset, e + 12);
  offset += pngs[i].length;
});
writeFileSync(`${repo}/src/Launcher/wwwroot/favicon.ico`, Buffer.concat([head, ...pngs]));
```

### Development switches

| Switch | Effect |
|---|---|
| `--route=/store` | opens that page first (the default is `/splash`, which goes on to the sign-in check) |
| `--mock=<state>` | puts one mock service in a state, to see a screen that is hard to reach |

States: `patch:new-patch`, `patch:new-patch-notes` (the same with a list of release notes), `patch:full-download`, `patch:launcher-update`, `patch:verifying`, `patch:extracting`, `patch:checksum`, `patch:files`, `patch:finishing`, `patch:done`, `patch:steam-update`, `patch:steam-only`, `patch:error`, `patch:launcher-ready`, `patch:exit-<code>` (the game ended with that code, for example `patch:exit-16001`), `settings:first` (the first-start dialog, on the mock settings), `signup:confirmed`, `account:bans`, `account:unconfirmed` (an address that is not confirmed), `account:steamfail` (disconnecting Steam fails), `account:resendfail` (an unconfirmed address whose resend fails), `profile:photos`, `profile:ongoing` (the latest hunt has not ended), `start:hold` (stay on the start screen), `session:signup` (an account without a username), `session:out` (signed out), `eula` (an account that has not accepted the agreements), `poll:fast` (the status bar and the messages page read again every 2 seconds instead of 30), `browser:hold` (the browser pages stay in their progress state).

Social and content: `friends:requests` (three friend requests), `friends:incoming` (a request arrives after five seconds), `messages:unread` (one unread conversation), `messages:incoming` (a message arrives after five seconds), `missions:resume` and `missions:noresume` (the resume item is offered, with one owned or none), `missions:soon` (the day ends in a few seconds).

Seasonal events: `events:running` (a Halloween 2026 event runs on the game's reserves 0, 1, 2, 8 and 9, so the launch map asks for the event's badges).

Store, wallet and bank: `store:fail` (every purchase fails), `store:nosales` (nothing is on sale, so no Sales entry), `wallet:many` (more than one page of wallet rows), `bank:steam206` and `bank:steamerror` (the two Steam errors of a bank purchase). Buying in the store or the bank changes one mock wallet that the status bar and the wallet show (per website session, or per launcher run). The bank codes are `SAVE20` (20 % off) and `GIFT` (500 em$, once); a payment reference with `fail` in it, as in `/steam_callback/fail-1`, is a failed payment.

The same states work on the website with `?mock=<state>`; see [website.md](website.md#development-switches), which also lists the states that exist only there.

## settings.json

`settings.json` sits next to the exe. Start from `src/Launcher/settings.example.json`; the build copies it with placeholders. The real file is git-ignored and never committed. Every value is a string. A missing key, a missing file or a value that still has `<placeholders>` means "not configured": only the part that needs it stops, the launcher still starts.

| Key | Meaning |
|---|---|
| `steam_app_id` | Steam app id of the game. Digits only. |
| `client_exe` | Path of the game's client exe, relative to the game folder. (The [backup tool's](backup.md#settingsjson) own `settings.json` uses the same key for a bare exe name.) |
| `graphics_file` | Name of the game's graphics settings file, relative to `%APPDATA%`. |
| `options_file` | Name of the game's options file, relative to `%APPDATA%`. |
| `options_key_prefix` | The text that precedes each key name in the options file. |
| `server_url` | The community server's `https://` address (its website shares it). At each start the launcher reads the list of the operator's pictures from its `/images.json` and shows them from its `/images/` ([images.md](images.md)). Only the scheme, host and port are used. Also switches the launcher's services to the community server's (see [What is real](#what-is-real-and-what-is-mock)), and is where the player kit forwards the game to. Not set: every picture is a labelled box and every service is a mock. `http://` is accepted only for a website on this PC (`localhost`, `dotnet run`); the player kit needs `https://` and is off without it. |
| `game_web_host` | The game's server name for everything it sends: the current build needs this one and `crash_host`. Never `web_host`: that is the community server's own website. For the player kit only ([the player kit](#the-player-kit)). |
| `crash_host` | The game's server name for crash reports. For the player kit only. |
| other `*_host` keys | The older `*_host` keys of the server's `settings.json` ([server.md](server.md#run-it)) are only for the old build. Every `*_host` key goes into the hosts block and into the certificate; only plain host names are accepted, and a name starting with `www.` also redirects the bare name. The host list is data: a new name needs a new `settings.json`, not a new build. |
| `game_name` | The short name shown wherever the screens say "the game" (the fallback when empty). |
| `game_full` | The name with its edition (falls back to `game_name`). |
| `link_*` | External links of the footer, the help pages and the menus; the key is `link_` plus `forum`, `support`, `support_connections`, `support_request`, `wiki`, `wiki_de`, `wiki_fr`, `wiki_es`, `steam_store`, `steam_community`, `developer`, `twitter`, `facebook`, `youtube`, `instagram`, `discord`, `fps_tool`, `health_utility`, `trailer` (the "Watch the trailer" button of the news), `backup` (the backup tool's release zip, linked from the news), `source` (the project's repository: "Follow the project on GitHub" in the news), or a numbered one: `link_guide_1` ..., `link_video_v1` ... (the launcher's help page only) and `link_community_1` .... A link that is not set is not shown. |

Texts say `{game}`, `{GAME}` or `{game_full}` where the original named the game; they are filled from `game_name` and `game_full`. Absolute paths and paths with `..` in the file names are rejected.

## What is real and what is mock

Real today:

- **Game settings** (the dialog from the Settings menu and the first-start dialog): reads and writes the game's graphics and options files; see below.
- **launcher.json**: the language chosen in the language dialog or on the Settings page is saved and applied at once (see [Languages](#languages)); so is whether game settings were saved once (the next game start no longer asks). The game folder chosen by hand is saved there too and used from the next start.
- **Finding the game** (`GameFinder`: the folder chosen by hand, the command Steam passes, Steam's own library files and the app manifest). It runs at start and is covered by the tests; no screen shows the result yet.
- **Sign-in** (only with `server_url`): the sign-in page has one button, "Sign in with Steam"; no code is shown or typed. It uses [the loopback sign-in](server.md#the-loopback-sign-in) (RFC 8252 with PKCE, RFC 7636): the launcher makes a random verifier, listens once on `127.0.0.1` (a free port) and opens the server's sign-in (`/auth/app/start`) in the player's browser with the verifier's SHA-256, the port and a random `state`. The player signs in with Steam there and presses "Yes, sign in"; the server sends the browser back to `http://127.0.0.1:<port>/?code=…&state=…`, the launcher checks the `state` (a request with another one is refused and the wait goes on), answers the browser "Signed in, you can close this tab" and trades the code and the verifier for the session cookie (`POST /auth/app/exchange`), so a sign-in link sent to someone else gives the sender nothing. While it waits the page says the browser is open and has a Cancel; after 5 minutes it says the sign-in was not completed in time. The cookie is stored in `%APPDATA%\hunter-resurgence\session.dat`, protected by Windows DPAPI for this Windows user, and read again at the next start; `GET /auth/me` says whether it still signs in. Sign out deletes the file and calls `POST /auth/signout`. The cookie is also in `ServerCookie`, for the services that read the server. Without `server_url` the sign-in stays the mock.
- **Starting the game**: `LauncherBridge.StartGame` runs `<game folder>\<client_exe> /connect:<url> /param:<n> /steam` in the exe's own folder (the client finds its data there) with `SteamAppId` and `SteamGameId` set to `steam_app_id` in its environment, so the client does not ask Steam to start it again (which would run Steam's launch option). Before that the launcher checks that Steam is signed in (`ActiveProcess\ActiveUser`); when nobody is signed in it starts Steam minimized (`steam.exe -silent`) and waits up to 60 seconds, and when nobody is signed in by then the start fails with "Start Steam and sign in". `<n>` is a random non-negative number with bit 128 clear. While that exe already runs, or when it is missing or fails to start, the start returns a failed `Result` (a translated text the page shows in a dialog) and the launcher keeps running.
- **The player kit** (only with an `https://` `server_url`): setup, Remove and the forwarder; see [The player kit](#the-player-kit).
- **Pictures**: from the website at `server_url`, see [images.md](images.md).
- **The news**: the project's own posts and their carousel, the same as on the website (`src/Ui/NewsPosts.cs`, [website.md](website.md#sign-in)).
- **The launch map and starting a hunt** (only with `server_url`, `ServerServersService`; the badges of a running event: [Seasonal events](#seasonal-events)): the 12 reserves, the starts on the map (lodges, campsites, towers and points of interest, each at its percent of the map from `GET /catalog/reserves`, [server.md](server.md#the-launch-map)) and the tutorials are the server's, and the loadout picker lists the player's own (`GET /account/loadouts`; it only shows with at least one). Start game sends the reserve, the lodge, the time and the loadout with the session cookie (`POST /auth/launch`), the server opens the hunt there as the game's own start does, and the launcher hands the launch URL it answers to the game (`ILauncherBridge.StartGame`). Without the session cookie, or when the server refuses or does not answer, the start fails with the page's message and no game starts. The server has no multiplayer: the games list is empty and creating or joining fails. A server that does not answer is an empty map, never the mock's. To change reserve, quit the game, pick another and start again.
- **Inventory**: the page exists but is turned off (no status bar link, its address goes to the feed); the store shows what the player owns (with `server_url`, from `GET /account/inventory`), as on the website ([website.md](website.md)).

- **With `server_url` set** the launcher registers the website's services (`src/Web/Program.cs`): profiles, friends, messages, wallet balances, missions, the store, the hunterscore board and the backup restore are the community server's, through one client and one session cookie, and runs in live mode: the pages the website hides (competitions, the bank and the like) redirect to the feed. The patch panel shows the launcher's own update when the launcher was installed with the Setup (see "Install, update and the link"), else it always reports ready; it ignores `--mock=patch:*` either way. The session cookie is filled by the launcher's sign-in (above); until the player has signed in the calls go out signed out. **Buying** in the store is `POST /account/buy` with that cookie and `X-Requested-With`, with em$ or gm$ ([server.md](server.md#buying-from-the-website)): the dialog says it is complete or that it failed; with fewer em$ or gm$ than the price the button of that currency stays off and a line says why ("Not enough em$: you have N, this costs M", the same for gm$; the same on the website). Besides, the em$ and gm$ in the status bar are read again when the dialog closes, so the new balance shows at once. Without `server_url` everything below stays mock.

Mock (the whole list without `server_url`; with it only what the server has no source for): sign-in (without the server only; the code step accepts any six digits), sign-up, the games list (the server has no multiplayer: it is empty with `server_url`), the map without `server_url`, hunting, the store, wallet and bank, missions, competitions, leaderboards, profiles and statistics, friends, messages, account settings, game management, the patch panel (except the launcher's own update, which is real when installed), and the buttons that open the game's folders (they only log).

## The player kit

The launcher lets the game reach the community server from your own PC, with your own copy of the game. It was a separate tool (`tools/play`, `play-win-x64.zip`); its code is now part of the launcher (`Hosts.cs`, `Pki.cs`, `Forwarder.cs`, `KitSettings.cs`, `Kit.cs` and `LauncherKit.cs` in `src/Launcher/`) and behaves as before. The game's files are never changed. While the PC is set up, the game talks only to the community server; Remove gives the official servers back. Make your backup with the [backup tool](backup.md) **before** the setup: it needs the official servers.

The kit exists only when `server_url` is an `https://` address (without it, or with an `http://` development server, Settings has no Set up and Remove and nothing below happens).

### For players

1. On its first start (and at every start while the PC is not set up) the launcher opens **Set up for the community server**: it lists what it changes and the game's server names. Choose **Set up**. Windows asks twice:
   - "Do you want to install this certificate?": choose **Yes**. This is the certificate authority that only your PC knows ([what it is](#what-setup-changes)).
   - A permission window for the launcher (administrator): choose **Yes**. Only the edit of the hosts file runs as administrator.

   You do this once. **Not now** keeps the launcher usable, but the game cannot reach the community server until you set up: Settings, **Set up for the community server** opens the same window again.
2. **Keep the launcher open** while you play, and start the game from it. The forwarder starts with the launcher and stops when it closes.
3. The first time the game starts, **Windows Firewall asks about the game: click Allow**. Without it the game cannot hunt.
4. To undo everything, choose Settings, **Remove the setup**. Windows asks to remove the certificate authority (choose Yes) and, as in setup, for administrator permission to edit the hosts file (choose Yes). After that the game talks to its own servers again (if they still exist).

If something does not work, see [Problems](#player-kit-problems).

### What setup changes

The setup window prints this before it starts. It changes three things on your PC and nothing else:

1. **The Windows hosts file** (`C:\Windows\System32\drivers\etc\hosts`): the game's server names (listed in `settings.json`) point to your PC (`127.0.0.1`), between two marked lines (`# >>> hunter-resurgence play (undo: play setup --remove)` and `# <<< hunter-resurgence play`; the lines keep the names of the old kit's block so that a block it made is found and removed too). Your other lines are not touched. Remove takes the block out again and leaves the file byte for byte as it was. If the end line is missing (a hand edit), the block ends at the first line that is not a `127.0.0.1 ` line: your lines after it stay.
2. **Your Windows trust list** (the current user's only, not the whole PC): a local certificate authority named `Hunter Resurgence local CA`. It is made on your PC, valid for two years, and **name-constrained**: Windows accepts a certificate it signed only for the game's server names, never for any other site. Its private key is created in memory, used once to sign the next item and then thrown away: it is never saved, so nobody can export or reuse it.
3. **A certificate for those names**, signed by that authority. Its public part is the file `%LOCALAPPDATA%\hunter-resurgence-play\leaf.cer`; its private key is a non-exportable key in your Windows key store. The forwarder uses it to answer the game over HTTPS.

Setting up again replaces all of it (and asks again). It is needed again when the certificate has expired, was made for other names than `settings.json` lists, or its authority is no longer trusted (removed, or you said No at the certificate prompt): the launcher says so at its start and opens the window. The check of the hosts block looks at the block only (its names, in any order); lines of other tools before or after it do not matter.

The administrator window: setup itself runs as you, so the certificate lands in your own trust list. Only when it needs to write the hosts file does the launcher start a second copy of its exe through the Windows permission window, with exactly the command line `--hosts on` (Remove: `--hosts off`); that copy edits the hosts file and exits, without a window or any other work. Any other command line starts the launcher normally. If the launcher itself runs as administrator, no window appears. The forwarder never needs administrator rights.

### How the forwarder works

While the launcher is open it listens on `127.0.0.1` port 443 (HTTPS, with the certificate above) and port 80 (plain HTTP: the game's built-in browser sends some requests that way). Both speak HTTP/1.1 only. Each request is relayed unchanged (method, path and query exactly as sent, headers, body) over normal HTTPS to the community server at `server_url`, and the reply is relayed back with its length in `Content-Length` (never chunked, the game cannot read that) and without `Alt-Svc`, otherwise unchanged. The name the game asked for travels in the header `X-Game-Host`, because the community server sits behind a proxy that hides it; the server reads it only from that proxy ([server.md](server.md#behind-a-reverse-proxy)). Redirects, cookies and compression are not touched. A request the server cannot be reached for gets a 502.

It starts when the launcher starts and the PC is set up (the certificate exists and is valid); a busy port or an expired certificate shows a message at the start (below). The kit sends everything the game sends, including your sign-in tokens, to the one address in `settings.json` and nowhere else, and keeps no log or copy of it.

### Player kit problems

- **"Could not listen on port 80 or 443"** at the launcher's start: another program uses the port (a web server, Skype, a local server of this project, a second copy of the launcher). Close it and start the launcher again.
- **"The certificate has expired"**: the setup window opens with that message; choose Set up, which makes a new one.
- **The game still reaches the official servers**: the hosts file has no block from setup (not set up, or removed): the setup window opens at the start. Some antivirus programs block edits of the hosts file; allow it or turn the protection off for the edit.
- **The game cannot connect**: check that the launcher is open and that you clicked Allow in the firewall prompt (Windows Defender Firewall, "Allow an app through the firewall", the game's exe).
- **The administrator window does not appear or you said No**: setup stops with a message; set up again. The certificate may already be installed: Remove undoes whatever was done.
- **A `settings.json` message** (not valid JSON, a key missing or still a placeholder, an `server_url` that is not `https://`, a name that is not a host name): fix the file; the player kit shows it at the start.

### Player kit tests

The tests (`tests/Resurgence.Tests/LauncherApp`: `HostsTests`, `PkiTests`, `ForwarderTests`, `KitSettingsTests`, `KitTests`) use temporary files and servers on `127.0.0.1` only: the hosts block round trip on a temporary file (byte for byte), the name constraints of the authority (the extension, and that Windows refuses another name signed by the same authority), the leaf's key being non-exportable, the relay (method, raw target, query, body, headers, `X-Game-Host` set, `Host` dropped, the reply unchanged except for its length and `Alt-Svc`, a chunked body, `HEAD`, a dead server) against a local test server, the HTTPS answer with a leaf whose key is in the key store (the test makes that key and deletes it again), the settings rules, and that only exactly `--hosts on|off` is the elevated copy. Nothing edits the real hosts file or trust list; setup, the administrator window and Remove are checked by hand on a real PC.

## Seasonal events

The Settings menu has "Seasonal events", in the launcher and on the website alike: a dialog with one box, "Play seasonal events", on by default (`SeasonalEventsModal`). With `server_url` it is the player's setting on the community server (`ServerSeasonalEventsService`: `GET` and `POST /account/events`, [server.md](server.md#seasonal-events)); without it the setting is a mock kept in memory. Off, the player's next game start gets no event and none of its content; a game that is running keeps the event it started with. Without a session or an answer the dialog shows the box on, and a refused change turns the box back with a message.

The launch map's reserve badges are the normal `reserve-<NN>` pictures. While the server says an event runs, the event covers the reserve and the player plays seasonal events, `LaunchMap` draws `reserve-<NN>-<event>` instead (the event `halloween_2026` is the slot `reserve-<NN>-halloween-2026`, [images.md](images.md)); the event's `reserves` are the game's reserve numbers (the launch map's reserve id minus 1), and none listed means every reserve. Where the operator has no file for that slot the normal badge stays. The launcher asks the server when the launch map opens.

**The event panel.** While the server says an event runs and the player plays seasonal events, the dashboard shows `EventPanel` above the hunt blocks: the event's name and dates, its community missions with a bar (progress / divided target) and the reward every contributor gets, the top contributors, the player's own event missions (objectives done of all, with Activate for an available one) and the event's store items, each a link to its store page. It reads `GET /account/events/panel` once when the dashboard opens (`ISeasonalEventsService.PanelAsync`); without a session, an answer, an event or with the setting off there is no panel. Mock `events:running` shows one with made-up numbers.

While an event runs, the launch map and the tutorials page show a line with the event's name and its reserves and the same "Play seasonal events" box (`EventSwitch`), reading and writing the same setting as the dialog (`ISeasonalEventsService`). Changing it there switches the badges at once and applies to the next hunt; a refused change turns the box back with a message. A reserve the event is on gets a small star on its badge when the operator has no picture for the slot. With no event the line and box are not shown; the Settings menu item stays.

## Files on the PC

| File | Who writes it | Content |
|---|---|---|
| `%APPDATA%\hunter-resurgence\launcher.json` | the launcher | language, game folder chosen by hand, whether game settings were saved once (`game_settings_saved`: the first game start no longer asks), and Steam's Play button (`steam_option_set`, `steam_option_before`, `steam_option_file`, `steam_option_asked`, `steam_option_never`). A missing or damaged file means the defaults. |
| the game's graphics file | Game settings, on Save | fullscreen, resolution and refresh rate, scene complexity, texture, shadow, anti-aliasing, anisotropic filtering, vertical sync |
| the game's options file | Game settings, on Save | volume, gamma, mouse and controller options, speeds, interface switches, scope quality |
| `%APPDATA%\hunter-resurgence\session.dat` | Sign-in, with `server_url` | the community server's session cookie, protected by Windows DPAPI for this Windows user; deleted by Sign out |
| the Windows hosts file | Player kit setup (an elevated copy of the launcher), removed by Remove and by the uninstall | the marked block that points the game's server names at this PC |
| `Hunter Resurgence local CA` in the current user's trust list | Player kit setup | the name-constrained local authority; removed by Remove and by the uninstall |
| `%LOCALAPPDATA%\hunter-resurgence-play\leaf.cer` and the key `hunter-resurgence-play-leaf` in the user's key store | Player kit setup | the certificate the forwarder answers with and its non-exportable key; removed by Remove and by the uninstall |
| `<Steam>\userdata\<account>\config\localconfig.vdf` and `localconfig.vdf.launcher-backup` | Steam's Play button, on OK | the one `LaunchOptions` value of the game; the backup is a copy of the file made before the first write, never overwritten; the value goes back on the uninstall |
| `<file>.resurgence-backup` | Game settings, once | settings copy of each of those two files made before the first write; never overwritten |
| `%LocalAppData%\HunterResurgence\` | the Setup and the updates | the installed launcher (`current\`) and the downloaded packages (Velopack); removed by the uninstall |
| `HKCU\Software\Classes\hunter-resurgence` | the Setup | the link `hunter-resurgence://`; removed by the uninstall |

Game settings changes only the values you edit. Every other byte of both files stays as it was, and no line is ever added. A file with an unknown format is left alone. The game reads both files once at start and rewrites them itself, so Save is refused while the game runs. A missing file shows "Start the game once so it creates this file" and is read-only.

Nothing else is read from or written to the game's folders. Steam's registry keys and the Steam library files are only read; the one write into Steam's files is the game's launch option, with the player's OK ([Start it from Steam](#start-it-from-steam)).

## Start it from Steam

Install the launcher with `HunterResurgence-win-Setup.exe` from the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest). It installs for your user only (no admin prompt) into `%LocalAppData%\HunterResurgence`, adds shortcuts and an Add/Remove programs entry, and registers the link `hunter-resurgence://` for your user. Windows may warn that the Setup is not code-signed: choose **More info**, then **Run anyway**. To remove it: Windows Settings, Apps, Installed apps, Uninstall; or `%LocalAppData%\HunterResurgence\Update.exe --uninstall`.

**Steam's Play button** (`SteamOption.cs`, `SteamVdf.cs`, `SteamOptionModal.razor`). The official servers are closed and the game's own launcher, which Steam opens on Play, cannot reach a server by itself: it needs this launcher. With the player's OK the launcher tells Steam to open it instead: it changes one setting of this game in Steam (its launch options), nothing else: not other games, not Steam itself, not the game's files. The player can see it in Steam: right-click the game, Properties, Launch options. Steam must be closed while the setting is written, because Steam saves its own copy when it exits. Uninstalling the launcher puts the setting back.

- **The line** is `"<install folder>\<exe>" %command%` with the exe at the root of the Velopack install folder (`VelopackLocator.Current.RootAppDir`), which stays the same across updates, not the one in `current\`. A launcher that the Setup did not install (a development or unpacked copy) says so and offers the Copy line only.
- **Where**: `<Steam>\userdata\<account>\config\localconfig.vdf` of the signed-in user (`ActiveProcess\ActiveUser`), else of the user whose file changed last; key `LaunchOptions` directly in the app's block (`steam_app_id`) under `UserLocalConfigStore/Software/Valve/Steam/apps`; the key is inserted when absent, the app's block too. It is a text edit of that one value: every other byte of the file stays. A copy of the original, `localconfig.vdf.launcher-backup`, is kept next to it before the first write. A file that does not parse (quotes, braces, key/value pairs, UTF-8) or has no `apps` block is left alone and the dialog says so with the Copy line.
- **Steam running**: the dialog asks to close it; the launcher runs `steam.exe -shutdown`, waits up to 30 seconds and never kills Steam (it then says to close Steam by hand). After the write the dialog offers to start Steam again (`steam.exe -silent`).
- **At every start** (the Splash, after the older-setup and player-kit dialogs) the launcher reads the option. When it is not ours (never set, or Steam or the player changed it) the dialog opens, at most once per 24 hours; its **Don't ask again** check box, with **Not now** or **Copy the line instead**, stops the offers for good (`launcher.json`: `steam_option_never`, `steam_option_asked`). The Settings item **Use Steam's Play button** opens the same dialog at any time. A launch option of the player's own is shown in the dialog and only replaced by **Close Steam and set it**; the dialog's **Copy the line instead** shows the line with a Copy button.
- **Uninstall** (Velopack's before-uninstall hook, after the other steps because it may wait for the player and for Steam): when the option is still our line it is replaced by the previous one (`steam_option_before` in `launcher.json`, in the file noted in `steam_option_file`) or removed when there was none; a changed option is left alone. A running Steam is closed first after a Yes/No question (default No: then the option stays and the player removes it in Steam).

Tests: `SteamOptionTests` (temporary vdf files: write, other bytes and other apps unchanged, insert, restore, unparseable file left alone).

Press Play in Steam. The game's own files stay untouched. On its first start the launcher asks to set up this PC for the community server ([The player kit](#the-player-kit)); keep it open while you play.

## Install, update and the link

- **Velopack** (`Program.cs`) runs first in `Main`: it handles the install, update and uninstall steps of the Setup and applies an update that was downloaded earlier when the launcher starts. A launcher that was not installed by the Setup (`dotnet run`, an unpacked build) never updates itself and shows no update.
- **The update** (`LauncherUpdater`) checks the releases of this repository at start (`GithubSource`, no pre-releases), downloads a newer one in the background (the patch bar shows it as a launcher update) and then shows the "launcher updated" message; its OK button applies the update and restarts the launcher. Nothing is checked, shown or applied while the game runs (`GameFiles.ClientRunning`), and a feed, download or checksum that fails leaves the installed version as it is, without a message.
- **The link** `hunter-resurgence://open` (`LinkScheme`) is written by the Setup and removed by the uninstall, under `HKCU\Software\Classes\hunter-resurgence`: Windows starts `resurgence-launcher.exe "hunter-resurgence://open"`. One launcher runs at a time: a second start (the link, a shortcut, Steam) brings the open window forward and exits. The elevated `--hosts on|off` copy is not a second start: it runs on its own.

### Install and uninstall

**The first start after the install** (Velopack's first-run hook, `Program.FirstRun`; `Cleanup.cs`) looks in fixed places for what an older setup left and shows one dialog, **Older setup found**. Nothing is deleted before the player chooses **Delete**; **Not now** leaves everything. Nothing outside the list below is touched.

| Found | Where | What happens |
|---|---|---|
| An older player kit setup: the hosts block, the authority `Hunter Resurgence local CA`, the leaf and its key | the hosts file, the current user's trust list, `%LOCALAPPDATA%\hunter-resurgence-play` | Shown as kept when it fits this version (the block has the names in `settings.json`, in any order, other lines do not matter; the leaf is valid, has exactly those names and is signed by the trusted authority), else as replaced: the setup window that follows opens with the reason and replaces all of it in its one hosts prompt. |
| A running `play.exe` (next to a `settings.json`) or a `resurgence-launcher.exe` that is not this install | any process | Listed; Delete asks it to close and stops it if it does not within a few seconds. |
| Old unpacked folders `launcher-win-x64` and `play-win-x64`, each with its exe still inside | Downloads, the desktop, the folder above a running old program's folder | Listed; Delete removes each with everything in it. |
| Stray game logs: `*.netlog`, `cef.log`, an empty `icons` folder | the game's install folder itself, never inside the game's own `game` folder | Listed; Delete removes them. |

If the player said No at the certificate prompt of a setup, no leaf is left (it is written last): the next start finds no setup and shows the setup window again.

**The uninstall** (Velopack's before-uninstall hook, `Cleanup.Uninstall`) removes the hosts block (the file is then byte for byte what it was before setup), the leaf and its key, the authority and the link scheme. Each step runs on its own: one that fails does not stop the others. Windows may ask in the hook (administrator permission for the hosts file, and to remove the authority); the hook has 30 seconds. If you say No, remove the block yourself (the lines between `# >>> hunter-resurgence play` and `# <<< hunter-resurgence play`) or run Settings, Remove the setup before uninstalling. The hook also asks, in a Yes/No box whose default is **No**, whether to delete `%APPDATA%\hunter-resurgence` (`launcher.json`, `session.dat`); No, closing the box without an answer and the 30 seconds running out all keep the data. The question is in the Windows display language. The game's files are never touched.

Tests: `CleanupTests` (temporary folders, hosts file, certificate store and registry key).

## Languages

The UI has 19 languages (`Languages.All` in `src/Ui/Languages.cs`): English, French, German, Spanish, Portuguese (Brazil), Russian, Italian, Dutch, Polish, Portuguese (Portugal), Swedish, Danish, Norwegian (bokmål), Finnish, Czech, Hungarian, Romanian, Slovenian and Turkish. All texts are .NET resources in `src/Ui/`: `Strings.resx` (English, also the fallback) and one `Strings.<code>.resx` per other language. The French texts are the original French of the old screens; the few new texts are in our own French.

Game vocabulary (animal names, collectables and a few labels) is not written by hand: the resx keys `Term.species.<id>`, `Term.collectable.<id>` and `Term.ui.<name>` hold one text per old id and language. A service passes the id (the mocks pass the English name, which is matched to its term); a text without a term is shown as it is. To add a language, add its texts to `Strings.<code>.resx` the same way and list it in `Languages.All`.

A text is a whole sentence: a link or bold part is a hole, never markup in the resx. `{0}...{1}` wrap the words (`L.Html`), or one hole is the whole link or name (`L.Frag`); a translation may move the holes. Counts are written without a plural noun (`Games shown: {0}`), so one string fits every number.

At start the launcher uses the language saved in `launcher.json`, else the Windows display language when one of the 19 matches (`Languages.Match`: the region is ignored, so `de-AT` is German; bare `pt` is Portuguese (Brazil); `no` and `nn` are Norwegian bokmål), else English. The player kit's texts are the `Kit.*` keys and `Settings.KitSetup`/`Settings.KitRemove` of the same files. A test (`tests/Resurgence.Tests/LauncherApp/TranslationsTests.cs`) checks that every other language file has the same keys and the same `{0}` placeholders as the English one, and that no key keeps its English text in all 18 (names such as `App.Name` are listed in the test). Native speakers are welcome to check any of the `src/Ui/Strings.<code>.resx` files.

Choosing a language (the language dialog, or Save under Settings, Language & time zone) saves it in `launcher.json`, makes it the process's default culture and reloads the page: the same page shows in the new language, still signed in, with the dialog closed. Only `CultureInfo.DefaultThreadCurrentCulture`/`DefaultThreadCurrentUICulture` are set, never `CurrentCulture`/`CurrentUICulture`: an assignment to those on the UI thread would keep it on the old language. For the same reason the launcher sets WPF's switch `Switch.MS.Internal.DoNotUseCulturePreservingDispatcherOperations` in `Resurgence.Launcher.csproj`; without it WPF writes the UI thread's culture back after every dispatcher operation, which keeps the language of the start.
