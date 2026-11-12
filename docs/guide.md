# Player guide: from backup to playing on the community server

Five steps, in this order. Steps 1 to 4 bring your profile to the community server; step 5 plays on it. You need Windows, Steam and your own copy of the game, installed. The tools are in the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest); the website is [hunter-resurgence.com](https://hunter-resurgence.com).

**The order matters.** A backup can only be restored onto an account that has not played yet. Hunt on the community server before your backup is restored and it can no longer be restored onto that account. Do steps 2 to 4 before your first hunt there.

## 1. Back up your profile

This needs the official servers, so do it while they are up (until 2026-11-11). A restore can only bring back what a backup saved. If you already have a backup zip, go to step 2.

1. If the launcher's setup is in place, choose Settings, Remove the setup first: while it is installed the game, and the backup tool, cannot reach the official servers.
2. Download `backup-win-x64.zip`, unpack it and keep `backup.exe` and `settings.json` in the same folder. Windows may say "Windows protected your PC": choose **More info**, then **Run anyway**.
3. Start the game and start a hunt. The backup tool reads your account through the running game, and the game is signed in only once a hunt has started.
4. Start `backup.exe`, choose **My account (from the running game)**, keep the ticks the tool proposes and press **Start**. Kill pages and photos and Photos are ticked by default: after a restore they give the gallery, the kill photos and the score sheets, and they cannot be fetched once the official servers close, so keep them (they add many requests and take a while). The groups that are off by default (messages, competition history) are not needed for a restore, and ticking Messages marks your unread messages as read for good. Leave the game running until it is done.
5. The result is a backup zip in `Documents\game-backup`. Keep it private: it holds your profile and a claim key, a hint that the backup is yours.

Only a backup made in My account mode can be restored with your wallet. Each run is a copy of your profile at that moment: anything you play afterwards is not in it.

## 2. Sign in on the website

Open [hunter-resurgence.com](https://hunter-resurgence.com), choose **Already a hunter? Sign in** and sign in with Steam. The first time, you choose a name; the restore in step 4 replaces it with your profile's name. Use the Steam account you play the game with: it is your account on the community server, on the website and in the game.

## 3. Send your backup, before your first hunt

Either way works; do one of them once:

- **On the website**: open **Restore your backup** in the **Settings** menu, choose the backup zip, press **Upload**. It says "Received: an operator will check it".
- **From the backup tool**: after a backup, press **Send to the community server** in the result bar (shown only when your copy of the tool knows the server), then **Send**. Your browser opens: sign in with Steam and confirm that the code is the one the tool shows (the page asks "Sign in the launcher?" even then: press **Yes, sign in**).

A backup zip is at most 1 GiB. "Not a usable backup" means the file is not a backup zip the server can read; "User id in use" ("Refused: profile in use" in the backup tool) means another account already holds that profile.

## 4. Wait for the operator

Nothing is restored automatically: the operator (the person who runs the server) checks every backup by hand, so it can take a while. Open **Restore your backup** on the website to see where it stands. Each backup waiting for review is listed with the time it was kept and, under it, the operator's reason if the restore was refused (in English). When the operator has decided you get a message in the game's inbox (the server's notice thread) and the page says what happened: restored, with your user id and name (and whether your name stayed because the one in the backup is not allowed or belongs to another player), or rejected, with the operator's reason. Your account then has the name and user id of your profile, with its inventory, wallet, skills, statistics, achievements and trophies.

Do not hunt on the community server before that.

## 5. Play with the launcher

The launcher signs you in, sets up your PC for the community server (the player kit) and starts the game. It never changes the game's files.

1. Download `HunterResurgence-win-Setup.exe` from the [latest release](https://github.com/yinyin99/hunter-resurgence/releases/latest) and run it: it installs for your user only (no admin prompt) and adds a shortcut. Windows may warn that it is not code-signed: choose **More info**, then **Run anyway**.
2. In Steam, right-click the game, choose **Properties**, and paste `"%LocalAppData%\HunterResurgence\current\resurgence-launcher.exe" %command%` into **Launch options**, with `%LocalAppData%` written out (`C:\Users\<you>\AppData\Local`).
3. Press **Play** in Steam. The first time, the launcher lists what it changes and asks you to set up. Choose **Set up**. Windows asks twice: install the certificate (**Yes**), and let the launcher change the hosts file as administrator (**Yes**).
4. Sign in with the code link the launcher shows (the same Steam account as in step 2), choose a reserve and a start, and press **Start**.
5. The first time the game starts, Windows Firewall asks about the game: choose **Allow**. Without it the game cannot hunt.

**Keep the launcher open** while you play: it answers the game. Next time: press **Play** in Steam and sign in.

**Back to the official servers**: in the launcher choose Settings, **Remove the setup**, and **Yes** to Windows' questions (the certificate, and permission to edit the hosts file). The game then talks to its own servers again (while they exist). You need this before running the backup tool again.

More: [launcher](launcher.md#the-player-kit).

## Problems

- **The backup tool says "redirected"** under Check connection: the launcher's setup is in place. Remove it in the launcher's Settings, back up, then set up again (Settings, Set up for the community server).
- **The backup tool cannot read your account**: the game must be running, in a hunt, on the same PC.
- **"Could not listen on port 80 or 443"** when the launcher starts: another program uses that port (a web server, a second launcher). Close it and start the launcher again.
- **The game cannot connect**: the launcher must be open, and the game allowed in Windows Firewall.
- **The game still reaches the official servers**: the launcher offers the setup again at its start. Some antivirus programs block edits of the hosts file.
- **You hunted before the restore**: the backup can no longer be restored onto that account, and the restore page shows that reason under the waiting backup. Open **Restore your backup** on the website, tick **I understand, delete everything** and press **Delete my data**, then sign in again (a new account) and send the backup again. This deletes the account's game data, its kept backups and anything it bought.

More detail on each tool: [backup tool](backup.md), [launcher](launcher.md), [website](website.md#restore-and-your-data).
