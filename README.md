# Hunter Resurgence

Unofficial community server, launcher and website for theHunter Classic, whose official servers close on 2026-11-11.

## Disclaimer

Unofficial fan project. Not affiliated with, endorsed by or connected to the developer or publisher of theHunter Classic. theHunter Classic is a trademark of its respective owner. This repository contains no game files or game data; you need your own copy of the game.

Licensed under the [GNU AGPL-3.0](LICENSE).

## For players: the backup tool

A small Windows app that saves your profile (statistics, inventory, trophies, hunt history and more) as files on your PC while the official servers are still up. It only reads; it changes nothing on the server. The app speaks English, French, Spanish, Portuguese, German and Russian, follows your Windows language and can be switched in the window. Details: [docs/backup.md](docs/backup.md).

1. Download `backup-win-x64.zip` from the latest release and unpack it. Keep `backup.exe` and `settings.json` in the same folder.
2. Start `backup.exe`. For your own account, open the game and start a hunt (the app can be open before or after); or choose "Public profile" and type a handle or id.
3. Tick what to save, press Start. The result goes to `Documents\game-backup\backup-<id>` and a zip beside it. Keep that zip private.

**Windows SmartScreen.** The exe is not code-signed, so Windows may show "Windows protected your PC / Unknown publisher". Choose **More info**, then **Run anyway**.

**Check the download.** The release notes list the SHA-256 of the zip. Compare it with yours:

```
Get-FileHash backup-win-x64.zip -Algorithm SHA256
```

If the two differ, do not run it.

To check that the zip was built by this repository's release workflow from its public source, use the [GitHub CLI](https://cli.github.com/):

```
gh attestation verify backup-win-x64.zip -R yinyin99/hunter-resurgence
```

## Roadmap

The official servers close on 2026-11-11. The game needs them even for single-player hunts, so after that date it can be played again once step 4 is done. The order is fixed; there are no dates, because each step needs the one before.

| Step | What it brings | State |
|---|---|---|
| Now | **Profile backup**: saves your profile while the official servers are still up | Available ([above](#for-players-the-backup-tool)). Run it before 2026-11-11 |
| 1 | **Remote play**: prove that the game can play on a community server over the internet, through a small helper on your PC. One-time setup; the game's files stay unchanged | First, after the shutdown |
| 2 | **Community server**: the server code in this repository. Anyone can run a server with the data of their own game install; none of the game's files are in this repository | Planned |
| 3 | **Accounts and restore**: sign in with your email (a one-time code, no password), optionally link your Steam account; upload your backup zip to get your inventory, trophies, statistics, skills, missions and wallet back | Planned |
| 4 | **New launcher, play again**: sign in, choose a server, start a hunt, change the game settings; Steam's Play button opens it | Planned |
| 5 | **New website**: profiles, trophies, hunt history with kill pages, leaderboards; then the store (in-game currency only, nothing sold for real money), missions and friends | Planned |
| Later | Trophy lodge and bait-site pages inside the game, multiplayer, competitions | - |

A restore can only bring back what the backup saved ([what is not saved](docs/backup.md#what-is-not-saved)). The claim key in the zip, and the Steam account recorded by a backup made in **My account** mode, prove that the backup is yours. Keep the zip private.

**Help wanted now:** native speakers who can check the app's French, Spanish, Portuguese (Brazil), German or Russian texts (`tools/backup/Strings.*.resx` and `src/Ui/Strings.*.resx`). Open an issue or a pull request.

## Build

See [CONTRIBUTING.md](CONTRIBUTING.md). The launcher and the website are described in [docs/launcher.md](docs/launcher.md) and [docs/website.md](docs/website.md).
