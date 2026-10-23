# Launcher

`src/Launcher/` is a Windows desktop app that will replace the game's own launcher once the community server exists. It runs beside the game's own launcher and does not change it. It never writes into the game's install folder.

It is a Blazor app shown in a WPF window (1280 x 760) through the WebView2 control, so the Microsoft Edge WebView2 runtime must be installed (it comes with Windows 11). The screens are shared with the [website](website.md) and live in `src/Ui/`; the launcher only adds the window, the real services below and the selftest.

## State

The screens follow the original launcher and its website screen for screen: the same theme, the same places for things, the same navigation and the same English texts (French uses the original French texts). Where the original had a defect or a text that cannot be reused, the screen does the sensible thing instead.

Everything behind the screens is a **mock** for now: fixed, fictional data and fake actions, until the community server exists. The pictures of the original are not included; each one is a neutral labelled box. The launcher is not usable for playing yet.

## The service layer

Every screen talks to an interface in `src/Services/Contracts/` (one per area: session, signup, news, servers, store, wallet, bank, missions, competitions, leaderboards, profile, friends, messages, account, game management, help, patch, game settings, launcher bridge). `src/Services/Mocks/` implements each of them with fixed data; `AddMockServices()` registers them all. A host replaces a registration with a real one when it exists. The launcher already does that for game settings.

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Launcher -c Release
dotnet run --project src/Launcher -c Release -- --selftest
```

The exe is `resurgence-launcher.exe`. There is no distributable build yet.

The selftest uses a temporary Steam folder tree, temporary settings files and temporary zips. It reads and changes nothing else on the PC. It prints one line per check and exits 0 when every check is ok.

### Development switches

| Switch | Effect |
|---|---|
| `--route=/store` | opens that page first (the default is `/splash`, which goes on to the sign-in check) |
| `--mock=<state>` | puts one mock service in a state, to see a screen that is hard to reach |

States: `patch:new-patch`, `patch:full-download`, `patch:launcher-update`, `patch:verifying`, `patch:extracting`, `patch:checksum`, `patch:files`, `patch:finishing`, `patch:done`, `patch:steam-update`, `patch:steam-only`, `patch:error`, `settings:first` (the first-start dialog, on the mock settings), `signup:confirmed`, `account:bans`, `profile:photos`, `start:hold` (stay on the start screen), `session:signup` (an account without a username).

The same states work on the website with `?mock=<state>`; see [website.md](website.md).

## settings.json

`settings.json` sits next to the exe. Start from `src/Launcher/settings.example.json`; the build copies it with placeholders. The real file is git-ignored and never committed. Every value is a string. A missing key, a missing file or a value that still has `<placeholders>` means "not configured": only the part that needs it stops, the launcher still starts.

| Key | Meaning |
|---|---|
| `steam_app_id` | Steam app id of the game. Digits only. |
| `client_exe` | Path of the game's client exe, relative to the install folder. |
| `graphics_file` | Name of the game's graphics settings file, relative to `%APPDATA%`. |
| `options_file` | Name of the game's options file, relative to `%APPDATA%`. |
| `options_key_prefix` | The text that precedes each key name in the options file. |
| `game_name` | The short name shown wherever the screens say "the game" (the fallback when empty). |
| `game_full` | The name with its edition (falls back to `game_name`). |
| `link_*` | External links of the footer, the help pages and the menus: `news`, `news_archive`, `forum`, `support`, `support_connections`, `support_request`, `wiki` and `wiki_de`, `wiki_fr`, `wiki_es`, `steam_store`, `steam_community`, `developer`, `twitter`, `facebook`, `youtube`, `instagram`, `discord`, `fps_tool`, `health_utility`, and the numbered lists `guide_1...`, `video_v1...`, `community_1...`. A link that is not set is not shown. |

Texts say `{game}`, `{GAME}` or `{game_full}` where the original named the game; they are filled from `game_name` and `game_full`. Absolute paths and paths with `..` in the file names are rejected.

## What is real and what is mock

Real today:

- **Game settings** (the dialog from the Settings menu and the first-start dialog): reads and writes the game's graphics and options files; see below.
- **launcher.json**: the language chosen in the language dialog is saved and used at the next start.
- **The check of a backup zip** made by the [backup tool](backup.md) (`BackupZip`: size, zip, `manifest.json`, `claim.key` against `claim_hash`). It has no screen in the new interface yet; the selftest covers it.
- **Finding the game** (`GameFinder`: the folder chosen by hand, the command Steam passes, Steam's own library files and the app manifest). It runs at start and is covered by the selftest; no screen shows the result yet.

Mock: sign-in (the code step accepts any six digits), sign-up, the news, the servers and the map, hunting, the store, wallet and bank, missions, competitions, leaderboards, profiles and statistics, friends, messages, account settings, game management, the patch panel, and the buttons that open the game's folders or start the game (they only log).

## Files on the PC

| File | Who writes it | Content |
|---|---|---|
| `%APPDATA%\hunter-resurgence\launcher.json` | the launcher | language, game folder chosen by hand, close the launcher when the game starts. A missing or damaged file means the defaults. |
| the game's graphics file | Game settings, on Save | fullscreen, resolution and refresh rate, scene complexity, texture, shadow, anti-aliasing, anisotropic filtering, vertical sync |
| the game's options file | Game settings, on Save | volume, gamma, mouse and controller options, speeds, interface switches, scope quality |
| `<file>.resurgence-backup` | Game settings, once | copy of each of those two files made before the first write; never overwritten |

Game settings changes only the values you edit. Every other byte of both files stays as it was, and no line is ever added. A file with an unknown format is left alone. The game reads both files once at start and rewrites them itself, so Save is refused while the game runs. A missing file shows "Start the game once so it creates this file" and is read-only.

Nothing else is read from or written to the game's folders. Steam's registry keys and the Steam library files are only read.

## Start it from Steam

Install the launcher anywhere. In Steam, right-click the game, choose Properties, and paste this into Launch options with the real path:

```
"C:\path\to\resurgence-launcher.exe" %command%
```

Press Play in Steam. The game's own files stay untouched.

## Languages

English and French. All texts are .NET resources in `src/Ui/`: `Strings.resx` (English, also the fallback) and `Strings.fr.resx`. The French texts are the original French of the old screens; the few new texts are in our own French.

At start the launcher uses the language saved in `launcher.json`, else the Windows display language when it is French, else English. The selftest checks that the French file has the same keys and the same `{0}` placeholders as the English one. Native speakers are welcome to check `src/Ui/Strings.fr.resx`.
