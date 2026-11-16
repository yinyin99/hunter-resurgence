# Player guide: from your old profile to playing on the community server

Five steps, in this order. Steps 1 to 4 bring your old profile to the community server; step 5 plays on it, and [multiplayer (beta)](#multiplayer-beta) comes after it. New players and players who don't want their old profile go straight to step 5. You need Windows, Steam and your own copy of the game, installed. The tools are in the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest); the website is [hunter-resurgence.com](https://hunter-resurgence.com).

**Uploading your backup replaces your profile on the community server** (items, wallet, skills, statistics, achievements, trophies and lodges). Hunts you played there before stay. A backup can be restored once.

## 1. Back up your profile

**The backup tool no longer works**: it needed the official servers, which closed on 2026-11-11. If you made a backup zip before then, go to step 2. Without one, go to step 2 too and claim your old profile by name in step 3. The steps below are what the tool did, for reference.

1. If the launcher's setup is in place, choose Settings, Remove the setup first: while it is installed the game, and the backup tool, cannot reach the official servers.
2. Download `backup-win-x64.zip`, unpack it and keep `backup.exe` and `settings.json` in the same folder. Windows may say "Windows protected your PC": choose **More info**, then **Run anyway**.
3. Start the game and start a hunt. The backup tool reads your account through the running game, and the game is signed in only once a hunt has started.
4. Start `backup.exe`, choose **My account (from the running game)**, keep the ticks the tool proposes and press **Start**. Kill pages and photos and Photos are ticked by default: after a restore they give the gallery, the kill photos and the score sheets, and they cannot be fetched once the official servers close, so keep them (they add many requests and take a while). The groups that are off by default (messages, competition history) are not needed for a restore, and ticking Messages marks your unread messages as read for good. Leave the game running until it is done.
5. The result is a backup zip in `Documents\game-backup`. Keep it private: it holds your profile and a claim key, a hint that the backup is yours.

Only a backup made in My account mode can be restored with your wallet. Each run is a copy of your profile at that moment: anything you play afterwards is not in it.

## 2. Sign in on the website

Open [hunter-resurgence.com](https://hunter-resurgence.com), choose **Already a hunter? Sign in** and sign in with Steam. The first time, you choose a name; the restore in step 4 replaces it with your profile's name. The same page has **I played before: restore what was kept of my old profile**: it is the claim of step 3 (leave the name empty to take your old name when it is free). Use the Steam account you play the game with: it is your account on the community server, on the website and in the game.

## 3. Claim your old profile or send your backup

**No backup zip?** Open **Restore your backup** in the **Settings** menu. Choose **I have no backup**, type your old hunter name and press **Find** (5 searches a day). A found profile shows its hunterscore and what a claim brings back: items, trophies, lodges, skills, statistics and achievements, but not your wallet. Players with over 500 hunterscore are the most likely to be found. Tick **Use my old name** to take that name back (shown only when it is free), then press **Claim**. One claim per account. The page then says "restored", or that an operator will check the claim (a very high hunterscore, or a profile someone else claimed too); for those, go to step 4.

**Have a backup zip?** Send it once, on the website: open **Restore your backup** in the **Settings** menu, choose the backup zip, press **Upload**. It says "Received: an operator will check it".

A backup zip is at most 1 GiB. "Not a usable backup" means the file is not a backup zip the server can read; "User id in use" means another account already holds that profile.

## 4. Wait for the operator

Nothing is restored automatically: the operator (the person who runs the server) checks every backup by hand, so it can take a while. Open **Restore your backup** on the website to see where it stands. Each backup waiting for review is listed with the time it was kept and, under it, the operator's reason if the restore was refused (in English). When the operator has decided you get a message in the game's inbox (the server's notice thread) and the page says what happened: restored, with your user id and name (and whether your name stayed because the one in the backup is not allowed or belongs to another player), or rejected, with the operator's reason. Your account then has the name of your profile (and its user id, unless you had already hunted on the server), with its inventory, wallet, skills, statistics, achievements and trophies.

## 5. Play with the launcher

The launcher signs you in, sets up your PC for the community server (the player kit) and starts the game. It never changes the game's files.

1. Download `HunterResurgence-win-Setup.exe` from the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest) (or from the website's Download page) and run it: it installs for your user only (no admin prompt) and adds a shortcut. Windows may warn that it is not code-signed: choose **More info**, then **Run anyway**.
2. The launcher is installed in `%LocalAppData%\HunterResurgence` (`C:\Users\<you>\AppData\Local\HunterResurgence`) and starts. It opens **Set up for the community server**: the window lists what it changes on your PC (three things, and never the game's files). Choose **Set up**. Windows asks twice: install the certificate (**Yes**), and let the launcher change the hosts file as administrator (**Yes**). A notice says it is done. You do this once. **Not now** leaves the launcher usable, but the game cannot reach the community server until you set up (Settings, **Set up for the community server**).
3. Press **Sign in with Steam** in the launcher and sign in in the browser that opens (the same Steam account as in Steam). A new account chooses its name there; then press **Yes, sign in**. The browser says "Signed in, you can close this tab". Choose a reserve and a start, and press **Start Game**. The first time, the launcher first asks for the game's graphics settings (**Initial setup**): press **Start Game** in it.
4. The first time the game starts, Windows Firewall asks about the game: choose **Allow**. Without it the game cannot hunt.
5. The next time the launcher starts (at once, if you chose **Not now** in step 2), it asks to **Start the game from Steam's Play button**. The official servers are closed. The game's own launcher, which Steam opens when you press Play, can no longer reach a server by itself: it needs this launcher, which carries your connection to the community server. So that Play keeps working, this launcher can tell Steam to open it instead. It changes one setting of this game in Steam (its launch options), nothing else: not your other games, not Steam itself, not the game's files. You can see it in Steam: right-click the game, Properties, Launch options. Steam must be closed while the setting is written, because Steam saves its own copy when it exits. Uninstalling this launcher puts the setting back. Choose **Close Steam and set it** (or **Copy the line instead** and paste it into Launch options yourself). The same dialog is in the launcher's Settings, **Use Steam's Play button**.

**To uninstall the launcher**: Windows Settings, Apps, Installed apps, Uninstall (or `%LocalAppData%\HunterResurgence\Update.exe --uninstall`). It puts Steam's launch option back.

**Keep the launcher open** while you play: it answers the game. Next time: press **Play** in Steam (or the website's **Hunt now**, or the Start menu shortcut). The launcher keeps you signed in for 30 days; sign in again when it asks.

**A new account** starts with a free kit: a 12 GA shotgun and a .243 rifle, ammunition for both, binoculars, clothes, 1,000 em$ and 15,000 gm$. At every hunt start the server tops the free ammunition up to three boxes each (what you bought beyond that stays); on a server with unlimited ammunition (the default) it also fills every other ammunition you own, and ammunition is not for sale. A restore keeps what the backup brings and adds any of these items it lacks.

**Remove the setup**: in the launcher choose Settings, **Remove the setup**, and **Yes** to Windows' questions (the certificate, and permission to edit the hosts file). The game then talks to its own servers again, which are closed, so it cannot play until you set up again.

More: [launcher](launcher.md#the-player-kit).

## Multiplayer (beta)

Multiplayer is a beta: it works, but expect rough edges. The game connects the players straight to each other; the community server only lists the games and introduces them.

- **Join**: in the launcher, Hunt now, **Find a multiplayer game** (or the dashboard's Quick Join). The list shows each game's name, host, reserve and players, and a lock for a password. Choose **Join Game**, type the password if asked, and click a starting location on the map. A refusal says why: wrong password, game full, friends only or your Hunter Score outside the host's limits, or the host is not ready yet.
- **Host**: in the launcher, Hunt now, **Multiplayer game** (the dashboard's **Start a multiplayer game**, or **HOST YOUR OWN GAME** when the list has no games): choose the reserve, the start and the time, then the game's name, a description, friends only or anyone, a password if you want one, and 2 to 8 players (optionally a Hunter Score range). The game ends when you leave it.
- **Connection**: the other players connect to your PC. The server's punch-through server helps a home router to let them in, with no setting on your side. If nothing answers, the game waits about 30 seconds and then tries a direct connection (UPnP, or a forwarded UDP port). A strict router can still block it. Games with a host close to you run better. Allow the game in Windows Firewall, as in step 5.

More: [launcher](launcher.md#multiplayer).

## Problems

- **"Could not listen on port 80 or 443"** when the launcher starts: another program uses that port (a web server, a second launcher). Close it and start the launcher again.
- **The game cannot connect**: the launcher must be open, and the game allowed in Windows Firewall.
- **The game cannot reach the community server**: the launcher offers the setup again at its start when the hosts block is missing. Some antivirus programs block edits of the hosts file.
- **You hunted before the restore**: nothing to do. The restore replaces your profile and keeps the hunts you played on the server; your account keeps its user id.

More detail on each tool: [backup tool](backup.md), [launcher](launcher.md), [website](website.md#restore-and-your-data).
