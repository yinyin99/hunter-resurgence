# The mission model

Status: accepted

Spec: [#244](https://github.com/yinyin99/hunter-resurgence/issues/244)

The community server runs missions as four rules:

1. **Definitions come from the operator's recorded catalogue**, the same recording the store uses ([The store catalogue](../server.md#the-store-catalogue)); the server has no missions of its own. A server without that recording has no missions to offer.
2. **Progress comes from the hunt's events**, as every other number of a hunt does ([ADR 0003](0003-statistics-are-a-base-plus-server-hunts.md)): the server decides what an event completes and never takes a progress or a completion from the client.
3. **The game's own updates and rewards**: the server pays a mission's reward and announces it in the reply shapes the game already shows (the new balance, the completion notice), so the game needs no change.
4. **Restored states carry over**: a mission the backup holds as completed stays completed, and an active one keeps its saved progress.

Daily missions rotate by date, the same for everyone, with streaks; challenges are tracked like missions. Missions, their progress and the wallet they pay are **owner-only** ([ADR 0005](0005-public-and-owner-only-profile-areas.md)).

## Considered options

- **Write the mission list into the server.** Rejected: the game and the website show the recorded titles and texts, so a list of our own would not match them and would be one more thing to keep in step. The cost is that a new mission means a new recording, as for the store.
- **Take completion from the client.** Rejected: the same trust rule as the statistics; a reward would be a number the player's PC could send.
- **Daily missions chosen per player.** Rejected: the spec fixes one rotation by date for everyone, so the calendar and the streaks mean the same to all players.

## Consequences

- A new mission, or a changed reward, needs a new recording of the catalogue, as a new store price does.
- The backup's mission states are kept whole in `extras` ([docs/restore.md](../restore.md)); the server reads them for the carry-over.
- The details (which events meet which objective, the reward fields, the calendar) are in [docs/server.md](../server.md) beside the code that decides them; this ADR holds the model only.
- Revisit with a new ADR that supersedes this one if the server must define missions of its own.
