# Profile pages only for accounts on this server

Status: accepted

Spec: [#244](https://github.com/yinyin99/hunter-resurgence/issues/244)

A **profile page** (in the game and on the website) exists for an account of the community server and for nothing else. Every public read finds its account with `State.PublicAccount`: an account of this server, not banned, and never the recorded player's account ([docs/server.md](../server.md#public-reads)). A backup that nobody restored onto their own account is never shown, whoever holds the zip. A handle the server does not know is "Page not found" (HTTP 404) on the live site: it never shows a mock hunter. The mock pages keep their fictional hunters only in the mock website and the launcher, which run without `SERVER_URL` ([docs/website.md](../website.md)).

## Considered options

- **Show any backup that was uploaded or imported.** Rejected: a backup says nothing about who owns it. The backup tool's Public profile mode copies any player's profile by handle, and the Steam id in a zip is a hint that anyone holding the zip can edit ([Upload a backup zip](../server.md#upload-a-backup-zip)). Showing a zip would publish a player's data on the word of whoever uploaded it.
- **Keep the mock hunter for an unknown handle.** Rejected: a fictional hunter looks like a real player, and the page of a handle that does not exist must say so.
- **Show the recorded player's profile.** Rejected: the recorded player is the operator's recording, not a player here; no player owns it.

## Consequences

- A player has a profile page after they have an account on the server: signing in creates it, and a restore fills it with the profile of the backup. Until then the page shows what the account has, not the backup.
- A banned account's page is "Page not found", like an unknown handle.
- A local server runs for one player and replays its recordings; it has no other players' pages.
- Deleting an account ("Delete my data") removes its page with its rows.
- Changing who may be shown, for example a private mode, takes a new ADR that supersedes this one.
