# Contributing

This project is licensed under the GNU AGPL-3.0. By contributing you agree that your contribution is released under the same license.

## Rules

These apply to every file and every commit message:

- Only README.md names the game, in plain text (tagline and disclaimer). Everywhere else, say "the game".
- No real hostnames or domains of the publisher, no user ids, handles, tokens or email addresses of real people. Use placeholders (`<api-host>`, `<client-exe>`, ...).
- No game files and no game data (assets, captures, dumps, saved replies of real accounts). Never patch, ship or download game files: the client is redirected with the hosts file and a local certificate (for now; the method may change).
- Nothing about how knowledge of the game or its servers was obtained: no reverse-engineering notes, addresses, tool names, capture or live-session logs, nothing about a person's account or runs.

Real values for a build (hosts, consumer key, exe name) live in a `settings.json` that is never committed (it is git-ignored). Releases read them from repository secrets. The release zip ships that `settings.json` in plain text, because the backup tool needs it to reach the official servers; the rules above apply to the repository's files and commits, not to that file.

## Build and test

Needs the .NET SDK pinned in `global.json` (Windows).

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project tools/backup -c Release -- --selftest
dotnet run --project src/Launcher -c Release -- --selftest
dotnet run --project src/Web
```

The solution holds the backup tool (`tools/backup`), the launcher (`src/Launcher`), the website (`src/Web`) and the library they share (`src/Ui`). Each selftest exits 0 when every check is ok: the backup tool's uses a fake HTTP handler and a fake process, the launcher's uses temporary files only. The last command starts the website on `http://localhost:5080`.

The launcher, the website and the backup tool read a `settings.json` (next to the exe, or in `src/Web/`), with real values that are never committed. Start from `src/Launcher/settings.example.json` (the website reads the same keys for the names and links) or `tools/backup/settings.example.json`. See [docs/launcher.md](docs/launcher.md), [docs/website.md](docs/website.md) and [docs/backup.md](docs/backup.md).

To build the backup tool's distributable exe, create a `settings.json` (start from `tools/backup/settings.example.json`) and run:

```
tools\backup\publish.cmd path\to\settings.json
```

## Releases

Pushing a tag `v*` runs `.github/workflows/release.yml`: build, selftest, publish, zip, SHA-256 in the release notes. It needs one repository secret per settings key, all required (a missing one fails the release): `API_HOST`, `APIV2_HOST`, `WEB_HOST`, `PHOTO_HOST`, `STATIC_HOST`, `AVATAR_HOST`, `OAUTH_CONSUMER_KEY`, `CLIENT_EXE`.

### Versioning

The git tag is the only version: `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-suffix`. The workflow takes the version from it (the exe, its window title and the backup manifest show it); a local build says `0.0.0-dev`.

- Patch: fixes only.
- Minor: anything new, including any change to the manifest format.
- `-rc.N` (for example `v0.2.0-rc.1`): test builds. A tag with a suffix is published as a prerelease.
- `1.0.0`: when the restore server accepts these backups.

Tag with an annotated tag whose message lists the changes; it becomes the "Changes" section of the release notes:

```
git tag -a v0.2.0 -m "What changed, one line per change"
git push origin v0.2.0
```

### Protections

- Published releases are immutable: their assets and tag are locked, and a version number can never be reused. A failed or wrong release means the next patch number.
- Release tags `vX.Y.Z` cannot be deleted or moved (tag ruleset, no bypass); disabling the ruleset is the only way out.
- `-rc.N` prereleases are immutable too and stay public, so use them sparingly.
- Pull requests from forks need the maintainer's approval before CI runs.

## Commits

Conventional Commits (`feat:`, `fix:`, `docs:`, `ci:`, `chore:` ...), imperative subject under 72 characters, one purpose per commit.
