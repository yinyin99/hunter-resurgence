# Hunter Resurgence

Unofficial community server, launcher and website for theHunter Classic, whose official servers close on 2026-11-11.

## Disclaimer

Unofficial fan project. Not affiliated with, endorsed by or connected to the developer or publisher of theHunter Classic. theHunter Classic is a trademark of its respective owner. This repository contains no game files or game data; you need your own copy of the game.

Licensed under the [GNU AGPL-3.0](LICENSE).

## Status

| Part | State |
|---|---|
| Profile backup tool | available (`tools/backup`) |
| Community server | later |
| Launcher | later |
| Website | later |

## For players: the backup tool

A small Windows app that saves your profile (statistics, inventory, trophies, hunt history and more) as files on your PC while the official servers are still up. It only reads; it changes nothing on the server. Details: [docs/backup.md](docs/backup.md).

1. Download `backup-win-x64.zip` from the latest release and unpack it. Keep `backup.exe` and `settings.json` in the same folder.
2. Start `backup.exe`. For your own account, open the game and start a hunt (the app can be open before or after); or choose "Public profile" and type a handle or id.
3. Tick what to save, press Start. The result goes to `Documents\game-backup\backup-<id>` and a zip beside it. Keep that zip private.

**Windows SmartScreen.** The exe is not code-signed, so Windows may show "Windows protected your PC / Unknown publisher". Choose **More info**, then **Run anyway**.

**Check the download.** The release notes list the SHA-256 of the zip. Compare it with yours:

```
Get-FileHash backup-win-x64.zip -Algorithm SHA256
```

If the two differ, do not run it.

## Build

See [CONTRIBUTING.md](CONTRIBUTING.md).
