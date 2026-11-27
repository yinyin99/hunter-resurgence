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

**No backup zip?** Open **Restore your backup** in the **Settings** menu. Choose **I have no backup**, type your old hunter name and press **Find** (5 searches a day). A found profile shows its HunterScore and what a claim brings back: items, trophies, lodges, skills, statistics and achievements, but not your wallet. Players with over 500 HunterScore are the most likely to be found. Tick **Use my old name** to take that name back (shown only when it is free), then press **Claim**. One claim per account. The page then says "restored", or that an operator will check the claim (a very high HunterScore, or a profile someone else claimed too); for those, go to step 4.

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

**The game check**: under the menu on the launcher's Hunt now pages (the dashboard, single player, multiplayer, Find a multiplayer game and tutorials), one line says "Game check: OK" or the first problem it found, with a button that fixes it: choose another game folder, open Steam (to update the game, or to sign in), or open the game's page in Steam (to verify its files). **See all** lists every check. A problem in red (a game that is not found, out of date, being updated, incomplete or already running, or a setup no longer in place) stops **Start Game** with the same text; a Steam that is not signed in is red too, and **Start Game** then starts Steam and waits up to a minute for you to sign in before it refuses. A problem in amber (a path with special characters or in OneDrive or on a network drive, a game file Windows blocked, low disk space, a file in the game's folder that is not part of the game, such as a graphics wrapper or a mod, or a game file that was changed) only warns.

**A new account** starts with a free kit: a 12 GA shotgun and a .243 rifle, ammunition for both, binoculars, clothes, 1,000 em$ and 15,000 gm$. At every hunt start the server tops the free ammunition up to three boxes each (what you bought beyond that stays); on a server with unlimited ammunition (the default) it also fills every other ammunition you own, and ammunition is not for sale. A restore keeps what the backup brings and adds any of these items it lacks.

**Remove the setup**: in the launcher choose Settings, **Remove the setup**, and **Yes** to Windows' questions (the certificate, and permission to edit the hosts file). The game then talks to its own servers again, which are closed, so it cannot play until you set up again.

More: [launcher](launcher.md#the-player-kit).

**A seasonal event fixes the time.** While an event runs on a reserve and "Play seasonal events" is on, the time of day of a hunt there is the event's (night), so the launcher shows "Fixed by the event" with that time instead of the time list. Other reserves are unchanged. To choose the time yourself, turn off "Play seasonal events" (the launcher's Settings menu, or the box on the launch map). **In a multiplayer game the host's setting applies to everyone:** if the host plays the event every player who joins gets it, if not none does, whatever their own setting; your own setting applies to your single-player hunts and to the games you host. The games list and the join window show whether a game runs the event.

**A seasonal event changes what is ethical.** Each kill is judged by the animal you shot. On the event's reserves, when the event applies to your hunt (your own setting for a single-player hunt or a game you host, the host's in a game you join), the animals are possessed (their event coat shows it), small game included: only the event's silver ammunition is permitted on them, and one hit with any other ammunition makes the kill unethical (disqualified: no score, no trophy shot, no place on the leaderboards, no taxidermy, no gm$), even when silver finishes it. A normal animal (with the setting off, on another reserve, or a coyote or blacktail deer, which are never possessed) takes its usual ammunition, and a hit with the event's silver on it makes the kill unethical. The event's werewolves and wendigos take silver only. When an animal's coat does not show which it is, the server goes by whether your hunt started with the event. This rule also applies to kills made before it; gm$ already paid stay yours, and a trophy already taxidermied or mounted stays.

## Optional: link your Discord account

If the community's Discord server offers it, link your Discord account to your hunter once and get **5000 gm$ and 250 em$**, one time only. You can start from the website or the launcher: until you have linked, a **Link Discord** card on the news page, a **Link Discord** button at the top of every page and a **Link Discord** entry in the Settings menu all take you there (in the launcher they open your browser). The website asks you to sign in with Steam if you are not signed in, then Discord asks you to allow the connection. When you come back to the website you see "Linked" with your HunterScore, your role and how many points are left for Veteran; the entry points are gone from then on. Your profile box on Discord shows your name with your current HunterScore under it (for example "Mikado", then "12345 HunterScore"). You get your roles on the Discord server automatically, there is nothing to claim: "Verified hunter" right after you link, and "Veteran" from a HunterScore of 20000, without linking again (it updates within a few minutes of your HunterScore changing). If you join the Discord server after linking, the roles come within a few minutes. A Discord account links to one hunter, and a hunter to one Discord account. The reward is paid once per hunter and per Discord account, however often you link again. **Delete my data** on the website's restore page removes your Discord link and those two roles; the website's Your data page says what is kept.

## Multiplayer (beta)

Multiplayer is a beta: it works, but expect rough edges. The game connects the players straight to each other; the community server only lists the games and introduces them.

- **Join**: in the launcher, Hunt now, **Find a multiplayer game** (or the dashboard's Quick Join). The list shows each game's name, host, reserve and players, and a lock for a password. Choose **Join Game**, type the password if asked, and click a starting location on the map. A refusal says why: wrong password, game full, friends only or your HunterScore outside the host's limits, or the host is not ready yet.
- **Host**: in the launcher, Hunt now, **Multiplayer game** (the dashboard's **Start a multiplayer game**, or **HOST YOUR OWN GAME** when the list has no games): choose the reserve, the start and the time, then the game's name, a description, friends only or anyone, a password if you want one, and 2 to 8 players (optionally a HunterScore range). The game ends when you leave it.
- **Connection**: the other players connect to your PC. The server's punch-through server helps a home router to let them in, with no setting on your side. If nothing answers, the game waits about 30 seconds and then tries a direct connection (UPnP, or a forwarded UDP port). A strict router can still block it. Games with a host close to you run better. Allow the game in Windows Firewall, as in step 5.

More: [launcher](launcher.md#multiplayer).

## Problems

- **"Could not listen on port 80 or 443"** when the launcher starts: another program uses that port (a web server, a second launcher). Close it and start the launcher again.
- **The game cannot connect**: the launcher must be open, and the game allowed in Windows Firewall.
- **The game cannot reach the community server**: the launcher offers the setup again at its start when the hosts block is missing. Some antivirus programs block edits of the hosts file.
- **You hunted before the restore**: nothing to do. The restore replaces your profile and keeps the hunts you played on the server; your account keeps its user id.
- **Key bindings**: change one in the game's options page (press the field, then the new key) and choose Save. The game keeps your bindings in its settings file in `%APPDATA%` (`C:\Users\<you>\AppData\Roaming`), the file whose name ends in `Settings_local.bin` for the community server.
- **The game's language and units**: in the launcher, Settings, Game settings, choose **Game language** (the game's menus and pages, from the next start of the game) and **Units** (metric or imperial, saved on your account). The game language follows the launcher's language when the game has it, and switching the launcher's language changes it again, even after a pick in Game settings.
- **The game fails to start or closes at once**: in the launcher, Settings, Report a problem builds a zip of the game's logs and settings and a few facts about your PC (the logs are cleaned of passwords and tokens; the game's crash dump, when Windows made one, is added as it is and may hold what the game had in memory). Look at the list, then save it to Downloads to share it yourself or send it to the community server, which gives you a reference to quote. The launcher can also send it by itself after a failed start, if you agree when it asks (you can change that in the same window).

More detail on each tool: [backup tool](backup.md), [launcher](launcher.md), [website](website.md#restore-and-your-data).
