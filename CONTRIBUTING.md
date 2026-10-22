# Contributing

This project is licensed under the GNU AGPL-3.0. By contributing you agree that your contribution is released under the same license.

## Rules

These apply to every file and every commit message:

- Only README.md names the game, in plain text (tagline and disclaimer). Everywhere else, say "the game".
- No real hostnames or domains of the publisher, no user ids, handles, tokens or email addresses of real people. Use placeholders (`<api-host>`, `<client-exe>`, ...).
- No game files and no game data (assets, captures, dumps, saved replies of real accounts).
- Nothing about how knowledge of the game or its servers was obtained: no reverse-engineering notes, addresses, tool names, capture or live-session logs, nothing about a person's account or runs.

Real values for a build (hosts, consumer key, exe name) live in a `settings.json` that is never committed (it is git-ignored). Releases read them from repository secrets.

## Build and test

Needs the .NET SDK pinned in `global.json` (Windows).

```
dotnet build tools/backup -c Release
dotnet run --project tools/backup -c Release -- --selftest
```

The selftest uses a fake HTTP handler and a fake process; it exits 0 when every check is ok.

To build the distributable exe, create a `settings.json` (start from `tools/backup/settings.example.json`) and run:

```
tools\backup\publish.cmd path\to\settings.json
```

## Releases

Pushing a tag `v*` runs `.github/workflows/release.yml`: build, selftest, publish, zip, SHA-256 in the release notes. It needs one repository secret per settings key, all required (a missing one fails the release): `API_HOST`, `APIV2_HOST`, `WEB_HOST`, `PHOTO_HOST`, `STATIC_HOST`, `AVATAR_HOST`, `OAUTH_CONSUMER_KEY`, `CLIENT_EXE`.

## Commits

Conventional Commits (`feat:`, `fix:`, `docs:`, `ci:`, `chore:` ...), imperative subject under 72 characters, one purpose per commit.
