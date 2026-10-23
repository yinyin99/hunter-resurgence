# Profile backup tool

`tools/backup/` is a Windows desktop app that saves a player's profile from the official servers as raw replies, before they shut down on 2026-11-11. It only reads: it never changes anything on the server.

## Build and run

```
dotnet build tools/backup -c Release
dotnet run --project tools/backup -c Release -- --selftest
tools\backup\publish.cmd <path\to\your\settings.json>
```

The window is WPF with the WPF UI library (Fluent theme, follows the system light/dark theme). The published exe is self-contained: players install nothing. The project is a console `Exe` so `dotnet run -- --selftest` prints; the publish profile makes the published exe a GUI one.

`publish.cmd` takes the path of a `settings.json` you filled in (see below). It publishes `tools\backup\bin\Release\net10.0-windows\win-x64\publish\backup.exe`, copies your `settings.json` beside it, and writes `backup-win-x64.zip` with both into `tools\backup\bin\Release\net10.0-windows\win-x64\`. The zip is what players download. The exe is stored uncompressed on purpose: it starts faster and needs less memory.

### settings.json

`settings.json` sits next to the exe. It holds `client_exe` (the game's exe name, used to find the running game), `oauth_consumer_key` (the website's public key) and the host names the app talks to: `api_host`, `apiv2_host`, `web_host`, plus `photo_host`, `static_host`, `avatar_host` (pictures are fetched only from https URLs on a host listed here, so every picture host needs a key) and any other `*_host` that pictures may come from. The repository only contains `tools/backup/settings.example.json` with placeholders; the app refuses to start a backup while a placeholder is left. The real values are not in this repository: whoever builds a release supplies them (the release workflow reads them from repository secrets, see `CONTRIBUTING.md`).

## Languages

The window is in English, French, Spanish, Portuguese (Brazil), German or Russian. It starts in the Windows display language when that is one of them, otherwise in English. The picker in the window switches it (not while a backup runs) and the choice is kept in `%APPDATA%\game-backup\language.txt`, next to `claim.key`. The texts are standard .NET resources: `Strings.resx` (English, also the fallback) and one `Strings.<culture>.resx` per language. The log lines and everything in `manifest.json` stay English. To add a language, add its `Strings.<culture>.resx` with the same keys, list it in `Loc.Languages` and in `SatelliteResourceLanguages` of the publish profile; the self-test fails when a resx lacks a key or has other `{0}` placeholders than the English one.

## What is saved

One file per request, named by endpoint and parameters (for example `Inventory_dict__user_id=<id>.json`), body exactly as the server sent it. `manifest.json` lists every file (`path`, `endpoint`, `params` without the token, `status`, `sha256`, `time_utc`) and the requests that did not give a file (`error`), plus `tool_version`, `groups`, `uid`, `handle` and `claim_hash`. The output folder is `backup-<uid>`; the zip is `backup-<uid>-<time>.zip` beside it. A second run with "skip files already saved" only asks for what is missing.

| Group | Content | Needs the running game |
|---|---|---|
| Profile | statistics, skills, achievements, challenges, personal leaderboards (every season), minigame positions, membership bundles, the item catalogue, avatar picture, antler boards | no |
| Inventory and dogs | owned items and dogs, placed-item positions, loadout | partly |
| Trophies and lodges | trophy list, lodges, harvest record of each own trophy | partly |
| Hunt history | every hunt with its kills, shots and scores | no |
| Kill pages and photos (off by default) | score sheet page and picture of every scored kill; many requests | no |
| Account | missions, wallet history, bank transactions, competition states, message settings, recruits, backgrounds | yes |
| Friends | friends list, friend requests | partly |
| Photos (off by default) | gallery pictures at full size | no |
| Messages (off by default) | conversation list, not the message texts | yes |

Pictures are fetched only from https URLs on a host listed in `settings.json`; anything else is logged and skipped. `oauth_consumer_key` is sent for the item catalogue request only and never appears in a file name, the log or the manifest.

Requests run in one lane per host: at most one request at a time and one start per second per host, with retries and back-off. A host that stays silent after its retries gets one attempt per request until it answers again. Cancel ends the run after writing `manifest.json`, so a resumed run continues.

### Two modes

- **My account**: reads the signed-in player's data using the session of the running game.
- **Public profile**: type a handle or id. Only public data is saved; requests that need the game session are logged as "skipped, needs the game" and are not counted as failed.

## What is not saved

- Placed-object layout and orientation (tents, bait sites, feeders, blinds, towers and other large equipment): the server keeps only the raw per-item `location` and `metadata`, saved as they are.
- In-game settings and controls (they stay on the player's PC) and any client state that was never sent to the server.
- Other players' data (only the public profile of a typed handle is saved).
- Full message threads.
- Tokens: a reply that contains an access token, a JWT or the string `refresh_token` is dropped and logged. The JWT the app obtains for some requests is kept in memory only.
- Gift codes, and any endpoint that changes state.

The app shows a short version of this list in its window.

## Privacy

- The session token is never written to a file, the log, the manifest or the zip. It is sent only to the API host, as the form field the website uses.
- In My account mode the manifest has `steam_id64`: the Steam account signed in on this PC. The app reads the value `ActiveUser` under `HKCU\Software\Valve\Steam\ActiveProcess` from the registry, read-only; nothing else from Steam, no Steam API, no network. It is stored only in the player's own folder and zip and is never sent anywhere by the app. If nobody is signed in the field is omitted. Public profile mode never writes it. A community server can use it as a hint that the Steam account signing in owns the backup, not as proof.
- Keep the zip private: it holds the account's data, the Steam id and `claim.key`.

## How the token is found

The launcher starts the game with a connect argument on its command line that carries the session. The app reads the command line of the running game process every 2 seconds (Windows `NtQueryInformationProcess`, same user, no administrator rights, no injection, no memory read) and keeps the access token. A new hunt means a new process and a new token, which replaces the old one. The refresh token in the same argument is parsed away and dropped. The launcher's own command line has no token, so the app waits until a hunt has started.

If the API refuses the token, the app says "start a hunt to refresh" and skips the requests that need it. Start a hunt and run again.

## Claim key

On first run the app writes 32 random bytes to `%APPDATA%\game-backup\claim.key`. Every backup folder and zip contains that file as `claim.key`; `manifest.json` records only its SHA-256 as `claim_hash`. Keep the zip private: the key, together with the Steam account, is what a future restore uses as a hint that the backup is yours.

The first restore will be local: a server on your own PC reads your own zip. A community server is planned after that, with Steam sign-in first (email sign-in may follow). The Steam account recorded in the backup links it to your sign-in. These are hints, not proof: if two people claim the same profile, the claim may be reviewed.

## Self-test

`dotnet run --project tools/backup -c Release -- --selftest` runs the checks against a fake HTTP handler and a harmless fake process; no real server is contacted. It exits 0 when every check passes.
