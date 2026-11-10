# Public and owner-only profile areas

Status: accepted

Spec: [#244](https://github.com/yinyin99/hunter-resurgence/issues/244)

Every area of a profile page is **public** except six that are **owner-only**. Anyone sees the header (hunterscore, rank, membership, avatar), ranks, achievements, skills, statistics, trophies, lodges, hunts and score sheets, dogs, gallery, personal bests and friends. Only the player sees their inventory, loadouts, map setups, wallet and its history, messages and missions ([GLOSSARY.md](../../GLOSSARY.md)).

Each public area has one read in the server, built once and served two ways: as the game's own reply shape for any user id of an account on this server, and as `GET /hunters/<handle>/<area>` JSON for the website's server, which is public and needs no cookie, like `GET /hunters/<handle>` ([Public reads](../server.md#public-reads)). Both find the account the way [ADR 0004](0004-profile-pages-only-for-accounts-on-this-server.md) says. An owner-only area keeps its token check: it answers only when the token's own user id is the one asked for (`State.IsOwn`).

## Considered options

- **Header only, everything else owner-only.** The least shown. Rejected: the spec's goal is that a player sees their progress, and any other player's, on the profile page in the game and on the website; a page with no content shows nothing.
- **A setting per area that each player chooses.** Rejected for now: it needs a settings page and a default for every area, and the spec fixes the split. A setting takes a new ADR.
- **Separate code for the game and for the website.** Rejected: two reads of one area would drift, and the game and the website would show different numbers for the same player. One read per area has two outputs.
- **Public only to signed-in players.** Rejected: the website's server shows a profile to a visitor who has no game token or session.

## Consequences

- A public area is a new read with both outputs and a test that an unknown, banned or recorded player's user id gets the empty reply; an owner-only area is a new read with a token check. Which of the two it is gets decided here first.
- A player's own trophy visibility and lodge edits still apply inside a public area ([Lodges and trophy visibility](../server.md#lodges-and-trophy-visibility)): the hidden trophies are left out of the website's list.
- The website's server reads the public areas over `SERVER_URL`; a browser never calls them, so the reverse proxy does not route `/hunters`.
