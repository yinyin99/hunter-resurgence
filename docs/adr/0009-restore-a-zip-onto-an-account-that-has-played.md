# Restore a zip onto an account that has played

Status: accepted

Supersedes [ADR 0006](0006-no-restore-onto-an-account-that-has-played.md).

An operator's approve restores an uploaded backup zip onto the account that uploaded it, **whether or not the account has played**. Uploading the zip is the player's consent to replace their profile on this server. The restore is the same either way: the profile rows (items, wallet, skills, statistics, achievements, trophies, lodges and the rest) are replaced by the zip's, and the hunts, kills and photos made here stay ([ADR 0003](0003-statistics-are-a-base-plus-server-hunts.md)). The rules that stay: a backup is restored once per game user id, and never with a user id another account holds ([Upload a backup zip](../server.md#upload-a-backup-zip)).

The one difference is the user id:

- An account that **has not played** takes the zip's user id, as before.
- An account that **has played** keeps its own. Nothing it played moves, and no id the zip brings can clash with the ids this server handed out under it. The `restored_ids` row is still written for the zip's user id, so that backup cannot be restored again. The name follows the same rule as for any restore: the zip's when it is a plain name nobody else has, unless the operator approves with `--keep-handle`.

The website's restore page no longer tells players to upload before their first hunt, and no longer says that a played account is refused; it says that uploading replaces the profile on this server while the hunts played here stay.

## Considered options

- **Keep ADR 0006: refuse a played account.** Rejected: the official servers are closed, so a player who hunted here first can never make a new backup, and the old rule left them with deleting their data as the only way in. The rule protected the player from a loss they were told about and chose by uploading.
- **Restore a played account under the zip's user id.** Rejected for the reason ADR 0006 gave: the account's hunts, kills and photos belong to its user id, so changing it splits or loses them, and the item and kill ids the zip brings are unique only per account.
- **Merge the zip into what the account played.** Rejected: two profiles would have to be merged, and the simple rule (the zip replaces the profile rows) is the one the player can understand.

## Consequences

- A player can upload their backup at any time. Items, wallet, skills and the other profile rows from before the restore are replaced; the hunts made here count on top of the restored statistics.
- An account that has played has a user id from this server's own range even after its restore. The user id of the backup is recorded only as restored, and a hunter's backup user id and the account's id can differ.
- "Has played" now decides only whether the account takes the zip's user id (the rows `expeditions` and `events`, as in ADR 0006), never whether the restore is allowed.
- [ADR 0008](0008-archive-claims.md): a claim never changes the account's user id either, so a claim and a zip restore on a played account now behave alike apart from the `restored_ids` row.
