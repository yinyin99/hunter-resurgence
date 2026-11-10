# Statistics are a base plus server hunts

Status: accepted

Spec: [#244](https://github.com/yinyin99/hunter-resurgence/issues/244)

On the community server a profile's statistics are two parts: the **base statistics** (the statistics a restore stored, which play never changes; empty for an account that restored nothing) and the statistics of every closed **server hunt**. `Statistics/total`, the connect reply's `user_stats`, the website's statistics, the ranks, the achievements' counters and the hunterscore all read the same sum of the two ([GLOSSARY.md](../../GLOSSARY.md), [docs/server.md](../server.md#the-statistics)), so a kill counts once. A hunt's statistics are stored with it when it closes; a later restore replaces the base and the server hunts stay. The hunts a backup saved (**restored hunts**) are shown in the hunt history only: the base already holds them, so they are never counted again.

The server is the authority: a server hunt's statistics come from the hunt's own heartbeat events, never from numbers the client reports, and the server awards the achievements the sum reaches when the hunterscore is recomputed (a hunt's close, a restore, a server start; [Achievements](../server.md#achievements)).

## Considered options

- **Add each hunt into the restored statistics.** One set of numbers and no sum on read. Rejected: after the first hunt nothing says which part is the backup's, so a later restore either drops the player's progress or counts a hunt twice, and a hunt's statistics cannot be recomputed or shown on their own.
- **Take the statistics the client reports.** Rejected: the server would trust a number the player's PC can edit; the hunterscore board and the profile pages are shown to everyone.
- **Count the restored hunts again, on top of the base.** Rejected: the base already holds them, so every restored kill would count twice.

## Consequences

- A read sums the base and the closed hunts' stored statistics; a hunt that is still open is not in the sum until it closes (the client's disconnect, the next launch after a quit, or a timeout without a heartbeat). The player sees the new value at the next connect.
- Every surface uses one sum, so the game and the website cannot disagree, and the hunterscore needs no counter of its own.
- A new restore changes the base only; the account's server hunts, and the kills and trophies they made, stay.
- Revisit with a new ADR that supersedes this one if a closed hunt's statistics must be edited, or if the sum becomes slow to read.
