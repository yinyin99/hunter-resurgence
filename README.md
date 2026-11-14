# Hunter Resurgence: play theHunter Classic after the shutdown

**Hunter Resurgence is a free, open-source community server that lets you keep playing theHunter Classic, on all 12 reserves and with your old profile, after its official servers closed on November 11, 2026.**

**[Play now](docs/guide.md)** · **[Discord](https://discord.gg/bRHPJ474Ce)** · **[Website](https://hunter-resurgence.com)** · **[Download](https://github.com/yinyin99/hunter-resurgence/releases/latest)**

[![ci](https://github.com/yinyin99/hunter-resurgence/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/yinyin99/hunter-resurgence/actions/workflows/ci.yml) [![release](https://img.shields.io/github/v/release/yinyin99/hunter-resurgence)](https://github.com/yinyin99/hunter-resurgence/releases/latest) [![license](https://img.shields.io/github/license/yinyin99/hunter-resurgence)](LICENSE)

Also searched as theHunter (2009), the Hunter Classic, theHunter private server.

## Status

**2026-11-14: online, early.** You can hunt on the community server today, [for free](#is-it-free). You need Windows 10 or 11, Steam and your own copy of the game: this project contains no game files.

## Open source, so it can't shut down again

- All the code (server, launcher, website) is in this repository, under the [GNU AGPL-3.0](LICENSE).
- If this server ever stops, anyone can run their own from this code. Offline play on your own PC is planned.
- Every copy and fork must stay open source.

![The launcher](docs/img/launcher.png)

*Placeholder pictures and sample data: the repository holds no game pictures.*

## How to play

1. Download `HunterResurgence-win-Setup.exe` from the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest) and run it. Windows may say "Windows protected your PC": choose **More info**, then **Run anyway** (the exe is not code-signed).
2. Press **Play** in Steam. The first time, the launcher lists what it changes and asks you to set up; answer **Yes** to Windows' two questions.
3. Sign in with Steam, pick a reserve, a start and the time of day, and press **Start Game**. The first time, choose **Allow** when Windows Firewall asks about the game. Keep the launcher open while you play.

The game's files are never changed; Settings, **Remove the setup** undoes everything. Step by step, with fixes for common problems: [the player guide](docs/guide.md).

## What works today

| Works now | Not yet |
|---|---|
| Sign in with Steam: one account for the launcher, website and game | Offline / LAN play on your own PC |
| Hunt on all 12 reserves; pick the start on a map and the time of day | Multiplayer |
| Every species licensed for every player, for now | Competitions and season boards |
| Kills, harvest, score sheets, gold and XP | Bait-site pages in the game |
| Tutorial for new players | Full in-game map tiles |
| Store: buy with em$ and gm$, in the game and on the website; it shows what you already own | |
| Loadouts, saved and chosen at launch | |
| Placed items, pick-ups and stack transfers | |
| Skills, statistics, achievements, ranks, milestones and personal bests | |
| Hunterscore and minigame boards | |
| Lodges you can edit, trophies, gallery, dogs and their level rewards | |
| Avatars you can choose | |
| Friends and friend requests, messages (read only), wallet history | |
| Public player profiles with hunt history and score sheets, in the game and on the website | |
| Halloween 2026 event on its 5 reserves, on or off per player on the hunt start screen (its missions and trophies are not in yet) | |
| Get your old profile back ([below](#get-your-old-profile-back)); delete your data | |
| Launcher that installs and updates itself; Steam's Play button opens it | |
| Website and launcher in 19 languages | |

Missions, daily missions and challenges are in the server but haven't been tested in play.

## Get your old profile back

Sign in on [the website](https://hunter-resurgence.com/restore) with the Steam account you play with, then:

- **No backup?** Look up your old in-game name. If your profile is found, claim it: it brings back your items, skills, achievements, trophies, lodges, loadouts, missions and statistics, but not your wallet. Players with over 500 hunterscore are the most likely to be found. One claim per account. Very high scores (10,000 or more) are checked by the operator first.
- **Have a backup zip?** Upload it. The operator (the person who runs the server) checks it by hand; a backup made in "My account" mode also brings back your wallet.

Either way works even if you already hunted on the community server: hunts, kills and photos made here stay. Details: [the player guide](docs/guide.md#3-claim-your-old-profile-or-send-your-backup).

## Roadmap

Planned, with no dates:

- Halloween 2026 missions and trophies
- Competitions and seasons
- The remaining in-game pages (bait sites, map tiles)
- Offline / LAN play
- Multiplayer

## Get involved

The best way to help:

1. **[Join the Discord](https://discord.gg/bRHPJ474Ce).**
2. **Play** on the community server.
3. **Give feedback on Discord**: what works, what breaks, what is missing.
4. **Share it**: tell other hunters, on Reddit, Steam forums, your group.

Native speakers can also check the translations (backup tool: [how](docs/backup.md#languages); launcher and website: [how](docs/launcher.md#languages)) with an issue or a pull request. Code: [CONTRIBUTING.md](CONTRIBUTING.md).

## FAQ

### Is theHunter Classic still playable?

Yes. The official servers closed on November 11, 2026, and the game needs a server even for a single-player hunt. Hunter Resurgence is that server: install the launcher and [play](#how-to-play).

### Is it free?

Yes. Playing on the community server is free: no paid tier, no ads, no sales of items or currency and no perks. The only currency is the in-game one.

### Do I need to buy licenses?

No. Every species is licensed for every player for now. Licenses may come back later, bought with in-game money only.

### Do I need to own the game?

Yes, on Steam. This repository contains no game files or game data.

### Is it safe? Does it change my game?

The game's files are never changed. The launcher edits the Windows hosts file and trusts a local certificate good only for the game's server names, so the game reaches the community server; Settings, **Remove the setup** undoes both. The source is here for anyone to read. What it changes: [docs/launcher.md](docs/launcher.md#the-player-kit).

### Can I get my old profile back?

Often, yes: by your old in-game name or from a backup zip. See [Get your old profile back](#get-your-old-profile-back).

### Can I hunt with friends?

Not yet. Multiplayer is on the [roadmap](#roadmap); friends, messages and profiles already work.

### What if this server shuts down too?

The code is open source: anyone can run it. See [Open source](#open-source-so-it-cant-shut-down-again).

### Is this official?

No. It is an unofficial fan project, see the [disclaimer](#disclaimer-and-license).

## Backup tool (archived)

> [!WARNING]
> **The backup tool no longer works.** It saved a profile from the official servers, which closed on 2026-11-11. Backup zips it made before then can still be [restored](#get-your-old-profile-back).

It was a small Windows app that saved a player's profile (statistics, inventory, trophies, hunt history and more) as a zip on their PC. It stays in the releases (`backup-win-x64.zip`) and its source stays here. What it saved, and how to check a download: [docs/backup.md](docs/backup.md).

## For contributors

Build, test, the repository's rules and releases: [CONTRIBUTING.md](CONTRIBUTING.md). The project's words: [GLOSSARY.md](GLOSSARY.md). AI coding agents start at [AGENTS.md](AGENTS.md).

One document per part:

- [docs/guide.md](docs/guide.md): the player guide, from your old profile to playing on the community server.
- [docs/backup.md](docs/backup.md): the backup tool.
- [docs/launcher.md](docs/launcher.md): the launcher and its player kit.
- [docs/website.md](docs/website.md): the website and the shared UI library.
- [docs/restore.md](docs/restore.md): the restore library.
- [docs/server.md](docs/server.md): the server (local and community).
- [docs/deploy.md](docs/deploy.md): the website's container image and deploy.
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): the third-party fonts, icons, flags and libraries bundled in the website, launcher, backup tool and server, and their licences.

## Disclaimer and license

Unofficial fan project. Not affiliated with, endorsed by or connected to the developer or publisher of theHunter Classic. theHunter Classic is a trademark of its respective owner. This repository contains no game files or game data; you need your own copy of the game.

Copyright (C) 2026 the Hunter Resurgence project, [yinyin99](https://github.com/yinyin99). Licensed under the [GNU AGPL-3.0](LICENSE): copies and modified versions keep this notice.

Additional term under section 7(b) of the AGPL-3.0: copies and modified versions of the website and the launcher must keep the attribution "Hunter Resurgence © yinyin99", linked to https://github.com/yinyin99/hunter-resurgence, in the footer where this project shows it (or, if the footer is removed, as visibly in the program's legal notices).
