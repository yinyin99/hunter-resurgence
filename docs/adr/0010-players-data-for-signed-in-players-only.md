# Players' data for signed-in players only

Status: accepted

Spec: [#750](https://github.com/yinyin99/hunter-resurgence/issues/750)

Amends [ADR 0005](0005-public-and-owner-only-profile-areas.md): its public areas are public to **signed-in players**, not to everyone.

A visitor who is not signed in sees what the game is and how to get it: the news, download, help, the terms and privacy pages, sign-in and sign-up. Every other page (profiles with all their areas, hunts and score sheets, galleries, leaderboards, the store, the missions, the competitions) is for signed-in players, and the server enforces it: its website routes answer 401 `signed_out` without a session, and the pages show the sign-in prompt ([Signed-in players only](../website.md#signed-in-players-only)). The game and the launcher are not affected: their calls carry the player's token or cookie.

No one, signed in or not, can list all the players of the server:

- The leaderboards show the top 100, and a player also sees his own place with the 50 above and the 50 below him.
- A player sees his own friends only; the friend search needs 3 characters and gives a few handles, a limited number of times an hour.
- A read that takes a user id answers for the caller, his friends and the players he can already see; walking user ids finds nothing.
- Reads of profiles, hunts, searches and pictures are rate limited per account, and an account that keeps hitting the limit raises an alert.
- A signed-in player sees every gallery and every picture at full size, and a kill's photo on every hunt page; only the owner gets the gallery zip and the Share button.
- Search engines get only the public pages: the sitemap lists them, every other page is `noindex`.

## Considered options

- **Keep ADR 0005's public profiles.** Rejected: anyone could collect the whole player list and every player's data, page by page, without an account.
- **Hide the pages only.** Rejected: the server's routes would still answer anyone; the server is the boundary.
- **Profiles by invitation or a per-area setting.** Rejected for now: it needs a settings page and a default for every area; signed-in only is the smallest change that stops collection.

## Consequences

- A new website route or page that shows a player's data is signed-in only (`LauncherGuard.NeedsSession` for the page, a session check on the route), and a list of players is bounded by the rules above.
- A screen shown to a player can still be saved by its viewer: this stops bulk collection and full-size downloads, not that.
