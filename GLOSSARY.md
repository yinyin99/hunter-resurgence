# Hunter Resurgence

A community replacement for the game's online services after the official servers close: profile backups, a server, a launcher and a website.

## Servers

**Official servers**:
The game publisher's servers, which close on 2026-11-11.
_Avoid_: live servers, old servers

**Local server**:
A server a player runs on their own PC or local network to play offline.
_Avoid_: offline server

**Community server**:
The shared server run by this project's maintainer; anyone can run the same code.
_Avoid_: restore server

## Backup

**Backup tool**:
The Windows app that copies a profile from the official servers to the player's PC.
_Avoid_: the app, backup app

**Profile**:
The data of one game account: statistics, inventory, trophies, hunt history and more.
_Avoid_: account data

**Backup**:
One copy of a profile at one moment, kept as a backup folder and a backup zip.

**Backup folder**:
The folder a backup writes, named after the account id; a later backup of the same profile adds the date and time.

**Backup zip**:
The zip of a backup folder; the file a player keeps private and later restores.
_Avoid_: zip (alone), archive

**Release zip**:
The download that holds the backup tool and its `settings.json`; not a backup zip.
_Avoid_: the zip

**My account**:
The mode that backs up the player's own profile through the running game.
_Avoid_: own, me

**Public profile**:
The mode that backs up any profile by handle or id, without the game.
_Avoid_: public account

**Group**:
A part of a profile the player ticks to save.
_Avoid_: section, category

**Claim key**:
A per-PC secret saved in every backup zip; a hint that the backup belongs to the player.

**Format**:
The version number of a backup zip's layout, separate from the backup tool's version.

## Restore

**Restore**:
Bringing a profile back on a server from a backup zip.

**Refused**:
Said of a whole backup zip that a restore does not read.

**Rejected**:
Said of a single entry a restore skips while reading the rest.

## Launcher

**Launcher**:
This project's Windows app that signs in, picks a server and starts the game.
_Avoid_: new launcher (outside the roadmap)

**Old launcher**:
The game's original launcher.

**Game folder**:
The folder where the game is installed.
_Avoid_: install folder

**Game settings**:
The game's graphics and options files, which the launcher can edit.

**Settings copy**:
The copy of a game settings file the launcher makes before its first write.
_Avoid_: backup

**Website**:
This project's website: profiles, trophies, leaderboards and account pages.
