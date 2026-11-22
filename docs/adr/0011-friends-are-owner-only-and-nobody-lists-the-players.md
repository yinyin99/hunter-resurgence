# Friends are owner-only, and nobody can list the players

Status: accepted

Spec: [#750](https://github.com/yinyin99/hunter-resurgence/issues/750)

Amends [ADR 0005](0005-public-and-owner-only-profile-areas.md). **Friends** move from the public areas to the owner-only ones: a player sees only his own friends and requests (`User/friendslist` with his token for his own user id, `GET /hunters/<handle>/friends` for his own handle, the Friends tab on his own profile). The **online state** shows only to the player himself and his friends.

The other public areas stay one read with two outputs, but a game read by `user_id` answers only for players the caller may see: himself, his friends, and whoever a board shows him (the top 100 and his own place with the 50 above and below, `State.CanSee`). For anyone else it is the answer for "no such profile", so walking user ids lists nobody. A read by handle (`Public_user/getByHostname`, `/hunters/<handle>`) finds any player here, for a player with a token or a session. Every read of another player counts in a per-account read limit (120 profile or hunt reads a minute, and 600 pictures a minute on their own counter, so a gallery never uses up the profile reads; [Read limits](../server.md#read-limits)), and the friend search finds at most 10 handles of at least 3 characters, 30 times an hour. An account that hits a limit raises an alert in the review channel.

## Considered options

- **Keep every area public to any player.** Rejected: sequential user ids and an open friends list let anyone collect every account of the server.
- **Profiles by user id for friends only.** Rejected: the in-game boards link players by id, and a player must open the profiles a board shows him.
- **Hide open hunts and live statistics from non-friends too.** Not done here: they are activity data of a profile the caller may already see; the explicit online flag is what the spec hides.

## Consequences

- A new read by `user_id` uses `State.SeenAccount` (visibility and read limit) and has a test that a player outside the caller's view gets the "no such profile" answer.
- `Friend/add` by id names a player in the website's list of requests sent, so it is allowed only for players of the caller's friends lists, his multiplayer session or his view.
