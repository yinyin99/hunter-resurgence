# The mission model

Status: accepted, amended by [ADR 0012](0012-the-operator-may-state-an-objectives-rule.md): the conditions of an objective may be stated by the operator's `mission_rules.json`; titles, texts and rewards still come only from the recorded catalogue.

Spec: [#244](https://github.com/yinyin99/hunter-resurgence/issues/244)

The community server runs missions as four rules:

1. **Definitions come from the operator's recorded catalogue**: the game's catalogue reply as the operator recorded it, which holds the missions and, unlike the store's, has no copy in the game install. The server has no missions of its own, and a server without that recording has none to offer.
2. **Progress comes from the hunt's events**, as every other number of a hunt does ([ADR 0003](0003-statistics-are-a-base-plus-server-hunts.md)): the server decides what an event completes and never takes a progress or a completion from the client.
3. **The game's own updates and rewards**: the server pays a mission's reward and announces it in the reply shapes the game already shows (the new balance, the completion notice), so the game needs no change.
4. **Restored states carry over**: a mission the backup holds as completed stays completed, and an active one keeps its saved progress.

Daily missions rotate by date, with streaks: a day's mission is picked by the date and the difficulty of the player's streak day, so players at different difficulties get different missions, and a frozen calendar keeps the mission it showed ([docs/server.md](../server.md#daily-missions); this sentence said "the same for everyone" before the difficulties came). Challenges are tracked like missions. Missions, their progress and the wallet they pay are **owner-only** ([ADR 0005](0005-public-and-owner-only-profile-areas.md)).

## Considered options

- **Write the mission list into the server.** Rejected: the game and the website show the recorded titles and texts, so a list of our own would not match them and would be one more thing to keep in step. The cost is that a new mission means a new recording, as for the store.
- **Take completion from the client.** Rejected: the same trust rule as the statistics; a reward would be a number the player's PC could send.
- **Daily missions chosen per player.** Rejected: the spec fixes one rotation by date, so the calendar and the streaks mean the same to all players; only the streak's difficulty differs between them.

## Consequences

- A new mission, or a changed reward, needs a new recording of the catalogue.
- The backup's mission states are kept whole in `extras` ([docs/restore.md](../restore.md)). Reading them for the carry-over, and the details (which events meet which objective, the reward fields, the calendar), come with the missions issue ([#272](https://github.com/yinyin99/hunter-resurgence/issues/272)) and its docs/server.md section; this ADR holds the model only.
- Revisit with a new ADR that supersedes this one if the server must define missions of its own.
