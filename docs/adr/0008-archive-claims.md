# Archive claims

Status: accepted

Amends [ADR 0006](0006-no-restore-onto-an-account-that-has-played.md) for claims only.

The official servers are closed, so a player can no longer make a backup zip. The operator kept the public profiles of most players in an archive (`archive.db`). A signed-in player may **claim** the archived profile of their own name: its items, skills, achievements, trophies, lodges and the rest of what a public backup holds are written onto the player's account ([Archive claims](../server.md#archive-claims)).

1. **A claim never changes the account's user id.** The archived profile is a copy made by someone else, and nothing proves the player owns it, so it never takes the old game user id and writes no `restored_ids` row. An own-mode zip of the same player, which does prove more, can still be approved later. So the rule of ADR 0006 (an account that has played keeps its user id) is not in the way: a claim is allowed on an account that has played, once the player confirmed the overwrite. Hunts, kills and photos made here stay; the statistics are the archive's base plus the hunts made here ([ADR 0003](0003-statistics-are-a-base-plus-server-hunts.md)). For zips ADR 0006 stands unchanged.
2. **Automatic below hunterscore 10,000.** A profile worth little is restored at once, and every player of that kind is covered. From 10,000, or when another account already claimed the same archived player, the operator decides (`review approve c<id>`); the operator gets a Discord message for each waiting row. Nobody can be hurt by a profile of little worth, and the review queue stays small.
3. **Limits against scraping, not against players.** A lookup needs a Steam sign-in and counts against 5 per account per UTC day; one claim per account, ever. Unclaimed archived players never become accounts and never appear on the website.
4. **The operator is not limited.** `claim <account> <archived name or user id>` skips every limit and review and replaces the account's earlier claim row.

## Considered options

- **Restore under the archived user id, like a zip.** Rejected: it would give a copy the old identity without proof, and refuse every account that has played (ADR 0006).
- **Review every claim.** Rejected: most players are below 10,000 and the operator would hold a queue nobody can empty.
- **Restore every claim at once.** Rejected: a high-ranked profile is the one worth taking from its owner; the operator checks it.
- **Merge the archive into the account.** Rejected for the same reason as in ADR 0006: the item and kill ids are unique only per account, and two profiles would have to be merged.

## Consequences

- Two accounts can name the same archived player; the second waits for the operator. A rejected claim stays the account's one.
- The archive is read only for the server and is not in the repository; without the file the feature is off.
- The account keeps its wallet (the archive has none), with gm$ raised to at least 15,000 like after every restore.
