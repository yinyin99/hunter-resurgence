# No restore onto an account that has played

Status: superseded by [ADR 0009](0009-restore-a-zip-onto-an-account-that-has-played.md)

Spec: [#244](https://github.com/yinyin99/hunter-resurgence/issues/244)

A restore on the community server replaces an account's profile rows and gives the account the backup's user id and, when it is a plain name of at most 32 characters that nobody else has, its handle. It is **refused** for an account that has played (a hunt was opened or an event came in; only its starting rows otherwise) when it would have to change the account's user id: the account keeps what it played, and the zip stays kept until the operator rejects it ([Upload a backup zip](../server.md#upload-a-backup-zip)). A backup is also restored once per game user id, and never with a user id another account holds.

The player is told why. A refused approve stores its reason as the zip's note and leaves a message in the account's inbox, and the website's restore page shows it ("The account has already played, so its user id cannot change to the one in the backup."). The way out is the player's own: "Delete my data", sign in again (a new account has not played), upload again. Review stays on the server's command line (`review list | approve | reject`); this decision adds no admin page.

## Considered options

- **Restore onto the account and keep what it played.** Rejected: the account's hunts, kills, trophies and photos belong to its user id, so changing the id under them splits or loses them, and the two profiles would have to be merged (the item ids and kill ids a backup brings are unique only per account).
- **Restore over the account and drop what it played.** Rejected: the player's progress on the server would vanish by an operator's click, with no choice made by the player. "Delete my data" is the same loss, chosen by the player.
- **An admin page for review.** Out of scope of the spec; the command line stays.

## Consequences

- A player who wants their backup on the server restores it before they play, or deletes the account and starts again; a backup uploaded after play waits, refused, until the player deletes the data or the operator rejects it.
- [ADR 0003](0003-statistics-are-a-base-plus-server-hunts.md) needs no merge rule: a restore replaces the profile rows whole, and the server hunts stay where there are any.
- "Has played" is the rows themselves (`expeditions` and `events`), so a refusal can be checked and tested without a flag to keep in step.
- A restore that does not change the account's user id (the account already has the backup's, as one made by the operator's `import backup` does) is not refused for having played. `import backup` writes no `restored_ids` row, so the once-per-user-id rule counts only the restores an operator approved.
