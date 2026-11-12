# Launcher

`src/Launcher/` is a Windows desktop app that will replace the old launcher once the community server exists. It runs beside the old launcher and does not change it. It never writes into the game folder.

It is a Blazor app shown in a WPF window (1280 x 760) through the WebView2 control, so the Microsoft Edge WebView2 runtime must be installed (it comes with Windows 11). The screens are shared with the [website](website.md) and live in `src/Ui/`; the launcher only adds the window and the real services below.

## State

The screens follow the old launcher and its website screen for screen: the same theme, the same places for things, the same navigation and the same English texts (French uses the original French texts). Where the original had a defect or a text that cannot be reused, the screen does the sensible thing instead.

Behind the screens the data is a **mock** (fixed, fictional data and fake actions) unless `server_url` is set; then the services listed under "What is real" are the community server's. The pictures are not included: the launcher shows the operator's pictures from the website at `server_url` ([images.md](images.md)); without it, or when the website does not answer within 5 s, each one is a neutral labelled box. The launcher is not usable for playing yet.

Until the official servers close, a large orange backup tool button shows under the top bar's logo; see [website.md](website.md#backup-tool).

## The service layer

Every screen talks to an interface in `src/Services/Contracts/` (one per area: session, signup, Discord widget, servers, store, wallet, bank, missions, competitions, leaderboards, profile, friends, messages, account, game management, help, patch, game settings, launcher bridge). `src/Services/Mocks/` implements each of them with fixed data; `AddMockServices()` registers them all. A host replaces a registration with a real one when it exists. The launcher does that for game settings, and for the website's `Server*` services when `server_url` is set.

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Launcher -c Release
dotnet test tests/Resurgence.Tests -c Release
```

The exe is `resurgence-launcher.exe`. To make the release: `src\Launcher\publish.cmd <your settings.json> [version]` publishes the self-contained exe, copies your `settings.json` next to it and packs `resurgence-launcher.exe`, `settings.json` and `wwwroot` (the page the exe shows; the exe does not start without it) with Velopack (the dotnet tool `vpk`, installed with `dotnet tool install -g vpk --version <the version of the Velopack package in the csproj>`) into `src\Launcher\bin\Release\net10.0-windows10.0.17763.0\win-x64\releases`: `HunterResurgence-win-Setup.exe`, the full package `HunterResurgence-<version>-full.nupkg`, a delta package `HunterResurgence-<version>-delta.nupkg` when the folder already holds the packages of an older release, and `releases.win.json`. The release workflow does this for a tag when the repository secrets `PLAY_SERVER_URL` and `STEAM_APP_ID` are set (without `STEAM_APP_ID` the release has no launcher), with the example `settings.json`, `server_url` from that secret and `steam_app_id`, `client_exe`, `graphics_file`, `options_file`, `options_key_prefix` from the secrets `STEAM_APP_ID`, `LAUNCHER_CLIENT_EXE`, `GRAPHICS_FILE`, `OPTIONS_FILE`, `OPTIONS_KEY_PREFIX` (all required with the launcher; the links and names stay placeholders, i.e. not configured). It first downloads the earlier releases (`vpk download github`) so that the delta can be made, uploads the Setup, the packages of this version and `releases.win.json` to the tag's release, attests the Setup and puts its SHA-256 in the release notes; CI packs the launcher with the example settings and checks the publish folder and the release folder.

The launcher's tests (folder `LauncherApp` of `tests/Resurgence.Tests`) use a temporary Steam folder tree, temporary settings files, temporary backup zips, a test website on 127.0.0.1 (the list of pictures), a local update feed (a folder with `releases.win.json` and a package) and a temporary registry key for the link scheme. They read and change nothing else on the PC, with one exception: `LauncherServicesTests` and `LanguageSwitcherTests` build the launcher's real services, so they read `launcher.json` (the second test points it at a temporary file), the Steam registry keys and the game search the way a start does (missing sources give the defaults); they write nothing but that temporary file. `dotnet test` exits 0 when every test passes.

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
| `server_url` | The community server's `https://` address (its website shares it). At each start the launcher reads the list of the operator's pictures from its `/images.json` and shows them from its `/images/` ([images.md](images.md)). Only the scheme, host and port are used. Also switches the launcher's services to the community server's (see [What is real](#what-is-real-and-what-is-mock)). Not set: every picture is a labelled box and every service is a mock. `http://` is accepted only for a website on this PC (`localhost`, `dotnet run`). |
| `game_name` | The short name shown wherever the screens say "the game" (the fallback when empty). |
| `game_full` | The name with its edition (falls back to `game_name`). |
| `link_*` | External links of the footer, the help pages and the menus; the key is `link_` plus `forum`, `support`, `support_connections`, `support_request`, `wiki`, `wiki_de`, `wiki_fr`, `wiki_es`, `steam_store`, `steam_community`, `developer`, `twitter`, `facebook`, `youtube`, `instagram`, `discord`, `fps_tool`, `health_utility`, `trailer` (the "Watch the trailer" button of the news), `backup` (the backup tool's release zip, linked from the news), `source` (the project's repository: "Follow the project on GitHub" in the news), or a numbered one: `link_guide_1` ..., `link_video_v1` ... (the launcher's help page only) and `link_community_1` .... A link that is not set is not shown. |

Texts say `{game}`, `{GAME}` or `{game_full}` where the original named the game; they are filled from `game_name` and `game_full`. Absolute paths and paths with `..` in the file names are rejected.

## What is real and what is mock

Real today:

- **Game settings** (the dialog from the Settings menu and the first-start dialog): reads and writes the game's graphics and options files; see below.
- **launcher.json**: the language chosen in the language dialog or on the Settings page is saved and applied at once (see [Languages](#languages)); so is whether game settings were saved once (the next game start no longer asks). The game folder chosen by hand is saved there too and used from the next start.
- **Finding the game** (`GameFinder`: the folder chosen by hand, the command Steam passes, Steam's own library files and the app manifest). It runs at start and is covered by the tests; no screen shows the result yet.
- **Sign-in** (only with `server_url`): the sign-in page has one button. It asks the server for a code (`POST /auth/link`), shows it, and opens the server's sign-in in the player's browser; the player signs in there and confirms the same code, and the launcher (polling `GET /auth/link/<code>`) keeps the session cookie. The cookie is stored in `%APPDATA%\hunter-resurgence\session.dat`, protected by Windows DPAPI for this Windows user, and read again at the next start; `GET /auth/me` says whether it still signs in. Sign out deletes the file and calls `POST /auth/signout`. The cookie is also in `ServerCookie`, for the services that read the server. Without `server_url` the sign-in stays the mock.
- **Starting the game**: `LauncherBridge.StartGame` runs `<game folder>\<client_exe> /connect:<url> /param:<n> /steam` in the exe's own folder (the client finds its data there) with `SteamAppId` and `SteamGameId` set to `steam_app_id` in its environment, so the client does not ask Steam to start it again (which would run Steam's launch option). Before that the launcher checks that Steam is signed in (`ActiveProcess\ActiveUser`); when nobody is signed in it starts Steam minimized (`steam.exe -silent`) and waits up to 60 seconds, and when nobody is signed in by then the start fails with "Start Steam and sign in". `<n>` is a random non-negative number with bit 128 clear. While that exe already runs, or when it is missing or fails to start, the start returns a failed `Result` (a translated text the page shows in a dialog) and the launcher keeps running.
- **Pictures**: from the website at `server_url`, see [images.md](images.md).
- **The news**: the project's own posts and their carousel, the same as on the website (`src/Ui/NewsPosts.cs`, [website.md](website.md#sign-in)).
- **The launch map and starting a hunt** (only with `server_url`, `ServerServersService`): the 12 reserves, the starts on the map (lodges, campsites, towers and points of interest, each at its percent of the map from `GET /catalog/reserves`, [server.md](server.md#the-launch-map)) and the tutorials are the server's, and the loadout picker lists the player's own (`GET /account/loadouts`; it only shows with at least one). Start game sends the reserve, the lodge, the time and the loadout with the session cookie (`POST /auth/launch`), the server opens the hunt there as the game's own start does, and the launcher hands the launch URL it answers to the game (`ILauncherBridge.StartGame`). Without the session cookie, or when the server refuses or does not answer, the start fails with the page's message and no game starts. The server has no multiplayer: the games list is empty and creating or joining fails. A server that does not answer is an empty map, never the mock's. To change reserve, quit the game, pick another and start again.

- **With `server_url` set** the launcher registers the website's services (`src/Web/Program.cs`): profiles, friends, messages, wallet balances, missions, the store, the hunterscore board and the backup restore are the community server's, through one client and one session cookie, and runs in live mode: the pages the website hides (competitions, the bank and the like) redirect to the feed. The patch panel shows the launcher's own update when the launcher was installed with the Setup (see "Install, update and the link"), else it always reports ready; it ignores `--mock=patch:*` either way. The session cookie is filled by the launcher's sign-in (above); until the player has signed in the calls go out signed out. Without `server_url` everything below stays mock.

Mock (the whole list without `server_url`; with it only what the server has no source for): sign-in (without the server only; the code step accepts any six digits), sign-up, the games list (the server has no multiplayer: it is empty with `server_url`), the map without `server_url`, hunting, the store, wallet and bank, missions, competitions, leaderboards, profiles and statistics, friends, messages, account settings, game management, the patch panel (except the launcher's own update, which is real when installed), and the buttons that open the game's folders (they only log).

## Files on the PC

| File | Who writes it | Content |
|---|---|---|
| `%APPDATA%\hunter-resurgence\launcher.json` | the launcher | language, game folder chosen by hand, and whether game settings were saved once (`game_settings_saved`: the first game start no longer asks). A missing or damaged file means the defaults. |
| the game's graphics file | Game settings, on Save | fullscreen, resolution and refresh rate, scene complexity, texture, shadow, anti-aliasing, anisotropic filtering, vertical sync |
| the game's options file | Game settings, on Save | volume, gamma, mouse and controller options, speeds, interface switches, scope quality |
| `%APPDATA%\hunter-resurgence\session.dat` | Sign-in, with `server_url` | the community server's session cookie, protected by Windows DPAPI for this Windows user; deleted by Sign out |
| `<file>.resurgence-backup` | Game settings, once | settings copy of each of those two files made before the first write; never overwritten |
| `%LocalAppData%\HunterResurgence\` | the Setup and the updates | the installed launcher (`current\`) and the downloaded packages (Velopack); removed by the uninstall |
| `HKCU\Software\Classes\hunter-resurgence` | the Setup | the link `hunter-resurgence://`; removed by the uninstall |

Game settings changes only the values you edit. Every other byte of both files stays as it was, and no line is ever added. A file with an unknown format is left alone. The game reads both files once at start and rewrites them itself, so Save is refused while the game runs. A missing file shows "Start the game once so it creates this file" and is read-only.

Nothing else is read from or written to the game's folders. Steam's registry keys and the Steam library files are only read.

## Start it from Steam

Install the launcher with `HunterResurgence-win-Setup.exe` from the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest). It installs for your user only (no admin prompt) into `%LocalAppData%\HunterResurgence`, adds shortcuts and an Add/Remove programs entry, and registers the link `hunter-resurgence://` for your user. Windows may warn that the Setup is not code-signed: choose **More info**, then **Run anyway**. In Steam, right-click the game, choose Properties, and paste this into Launch options with the real path (the installed exe is `%LocalAppData%\HunterResurgence\current\resurgence-launcher.exe`):

```
"C:\path\to\resurgence-launcher.exe" %command%
```

Press Play in Steam. The game's own files stay untouched.

## Install, update and the link

- **Velopack** (`Program.cs`) runs first in `Main`: it handles the install, update and uninstall steps of the Setup and applies an update that was downloaded earlier when the launcher starts. A launcher that was not installed by the Setup (`dotnet run`, an unpacked build) never updates itself and shows no update.
- **The update** (`LauncherUpdater`) checks the releases of this repository at start (`GithubSource`, no pre-releases), downloads a newer one in the background (the patch bar shows it as a launcher update) and then shows the "launcher updated" message; its OK button applies the update and restarts the launcher. Nothing is checked, shown or applied while the game runs (`GameFiles.ClientRunning`), and a feed, download or checksum that fails leaves the installed version as it is, without a message.
- **The link** `hunter-resurgence://open` (`LinkScheme`) is written by the Setup and removed by the uninstall, under `HKCU\Software\Classes\hunter-resurgence`: Windows starts `resurgence-launcher.exe "hunter-resurgence://open"`. One launcher runs at a time: a second start (the link, a shortcut, Steam) brings the open window forward and exits. The elevated `--hosts on|off` copy is not a second start: it runs on its own.

## Languages

The UI has 19 languages (`Languages.All` in `src/Ui/Languages.cs`): English, French, German, Spanish, Portuguese (Brazil), Russian, Italian, Dutch, Polish, Portuguese (Portugal), Swedish, Danish, Norwegian (bokmål), Finnish, Czech, Hungarian, Romanian, Slovenian and Turkish. All texts are .NET resources in `src/Ui/`: `Strings.resx` (English, also the fallback) and one `Strings.<code>.resx` per other language. The French texts are the original French of the old screens; the few new texts are in our own French.

Game vocabulary (animal names, collectables and a few labels) is not written by hand: the resx keys `Term.species.<id>`, `Term.collectable.<id>` and `Term.ui.<name>` hold one text per old id and language. A service passes the id (the mocks pass the English name, which is matched to its term); a text without a term is shown as it is. To add a language, add its texts to `Strings.<code>.resx` the same way and list it in `Languages.All`.

A text is a whole sentence: a link or bold part is a hole, never markup in the resx. `{0}...{1}` wrap the words (`L.Html`), or one hole is the whole link or name (`L.Frag`); a translation may move the holes. Counts are written without a plural noun (`Games shown: {0}`), so one string fits every number.

At start the launcher uses the language saved in `launcher.json`, else the Windows display language when one of the 19 matches (`Languages.Match`: the region is ignored, so `de-AT` is German; bare `pt` is Portuguese (Brazil); `no` and `nn` are Norwegian bokmål), else English. A test (`tests/Resurgence.Tests/LauncherApp/TranslationsTests.cs`) checks that every other language file has the same keys and the same `{0}` placeholders as the English one, and that no key keeps its English text in all 18 (names such as `App.Name` are listed in the test). Native speakers are welcome to check any of the `src/Ui/Strings.<code>.resx` files.

Choosing a language (the language dialog, or Save under Settings, Language & time zone) saves it in `launcher.json`, makes it the process's default culture and reloads the page: the same page shows in the new language, still signed in, with the dialog closed. Only `CultureInfo.DefaultThreadCurrentCulture`/`DefaultThreadCurrentUICulture` are set, never `CurrentCulture`/`CurrentUICulture`: an assignment to those on the UI thread would keep it on the old language. For the same reason the launcher sets WPF's switch `Switch.MS.Internal.DoNotUseCulturePreservingDispatcherOperations` in `Resurgence.Launcher.csproj`; without it WPF writes the UI thread's culture back after every dispatcher operation, which keeps the language of the start.
