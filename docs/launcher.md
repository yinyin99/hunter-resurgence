# Launcher

`src/Launcher/` is a Windows desktop app that will replace the game's own launcher once the community server exists. It runs beside the game's own launcher and does not change it. It never writes into the game's install folder.

It is a Blazor app shown in a WPF window through the WebView2 control, so the Microsoft Edge WebView2 runtime must be installed (it comes with Windows 11). The pages are shared with the website and live in `src/Ui/`; the pages that only make sense in the launcher live in `src/Launcher/Pages/`.

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Launcher -c Release
dotnet run --project src/Launcher -c Release -- --selftest
```

The exe is `resurgence-launcher.exe`. There is no distributable build yet. For development, `--route=/store` opens that page first.

The selftest uses a temporary Steam folder tree, temporary settings files and temporary zips. It reads and changes nothing else on the PC. It prints one line per check and exits 0 when every check is ok.

## settings.json

`settings.json` sits next to the exe. Start from `src/Launcher/settings.example.json`; the build copies it with placeholders. The real file is git-ignored and never committed.

| Key | Meaning |
|---|---|
| `steam_app_id` | Steam app id of the game. Digits only. |
| `client_exe` | Path of the game's client exe, relative to the install folder. |
| `graphics_file` | Name of the game's graphics settings file, relative to `%APPDATA%`. |
| `options_file` | Name of the game's options file, relative to `%APPDATA%`. |
| `options_key_prefix` | The text that precedes each key name in the options file. |

A missing key, a missing file or a value that still has `<placeholders>` means "not configured". Only the part that needs the value stops: the launcher still starts and says what is missing. Absolute paths and paths with `..` are rejected.

## How it finds the game

The first of these that contains `client_exe` wins:

1. The folder chosen with **Browse** in Launcher settings (saved in `launcher.json`).
2. The command Steam passes when it starts the launcher through launch options. An argument that is an existing exe inside the install folder (or in one of its subfolders) gives the folder.
3. Steam's own data: the Steam path from the current user's registry key, every library listed in `steamapps\libraryfolders.vdf`, and the `installdir` of `steamapps\appmanifest_<steam_app_id>.acf`.

The Play page and Launcher settings show the folder found, or "not configured" or "not found".

## Start it from Steam

Install the launcher anywhere. In Steam, right-click the game, choose Properties, and paste this into Launch options with the real path:

```
"C:\path\to\resurgence-launcher.exe" %command%
```

Press Play in Steam. The game's own files stay untouched.

## Files on the PC

| File | Who writes it | Content |
|---|---|---|
| `%APPDATA%\hunter-resurgence\launcher.json` | the launcher | language, game folder chosen by hand, close the launcher when the game starts. Saved on every change. A missing or damaged file means the defaults. |
| the game's graphics file | Game settings, on Save | fullscreen, resolution and refresh rate, scene complexity, texture, shadow, anti-aliasing, anisotropic filtering, vertical sync |
| the game's options file | Game settings, on Save | volume, gamma, mouse and controller options, speeds, interface switches, scope quality |
| `<file>.resurgence-backup` | Game settings, once | copy of each of those two files made before the first write; never overwritten |

Game settings changes only the values you edit. Every other byte of both files stays as it was, and no line is ever added. A file with an unknown format is left alone. The game reads both files once at start and rewrites them itself, so Save is refused while the game runs. A missing file shows "Start the game once so it creates this file" and is read-only.

Nothing else is read from or written to the game's folders. Steam's registry keys and the Steam library files are only read.

## Restore backup

The Restore page opens a zip made by the [backup tool](backup.md) and checks it on this PC only:

- the file is at most 512 MB and is a zip;
- it contains `manifest.json` and the manifest parses;
- it contains `claim.key` and the SHA-256 of that file equals `claim_hash` in the manifest.

Then it shows the account id, handle, tool version, date, groups, the number of files and the files per endpoint. If the backup recorded a Steam account, it is compared with the one signed in on this PC. Each failure has its own message. Nothing is sent anywhere: the last step is disabled until the community server exists.

## What needs the community server

These pages work as screens but show sample data, or are disabled, until the community server exists:

- sign-in (the email and code form accepts anything);
- Hunt now, Join and Host (disabled with "Needs a community server");
- the server list and the wallet balance in the top bar;
- the store, missions and friends;
- profiles, trophies, hunt history, kill pages and leaderboards;
- linking and unlinking a Steam account;
- the final step of Restore backup.

Game settings, Launcher settings (game folder, Steam account shown read-only, open folder buttons) and the check of a backup zip are real today.

## Languages

All texts are .NET resources in `src/Ui/Strings.resx` (English, the fallback), with one `Strings.<culture>.resx` per added language. The language chosen in Launcher settings is saved in `launcher.json`.
