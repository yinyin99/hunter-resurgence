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
dotnet test hunter-resurgence.slnx -c Release --no-build
dotnet run --project src/Web
```

The solution holds the backup tool (`tools/backup`), the launcher (`src/Launcher`), the website (`src/Web`), the library they share (`src/Ui`), the service contracts and mocks (`src/Services`) and the restore library (`src/Restore`, with a command-line stub in `src/Restore/Cli`). The tests are in `tests/Resurgence.Tests` (xUnit v3) and `dotnet test` exits 0 when every test passes: the backup tool's tests use a fake HTTP handler and a fake process, the launcher's use temporary files only, the restore tests build synthetic zips in memory; none reads or changes anything else on the PC or uses the network. The last command starts the website on `http://localhost:5080`.

The launcher, the website and the backup tool read a `settings.json` (next to the exe, or in `src/Web/`), with real values that are never committed. Start from `src/Launcher/settings.example.json` (the website reads the same keys for the names and links) or `tools/backup/settings.example.json`. The keys are listed in [docs/backup.md](docs/backup.md) and [docs/launcher.md](docs/launcher.md#settingsjson). `client_exe` is a bare exe name in the backup tool (it looks for the running process) and a path relative to the game folder in the launcher. See also [docs/website.md](docs/website.md) and [docs/restore.md](docs/restore.md). The website's container image and its deploy are in [docs/deploy.md](docs/deploy.md).

To build the backup tool's distributable exe, create a `settings.json` (start from `tools/backup/settings.example.json`) and run:

```
tools\backup\publish.cmd path\to\settings.json [version]
```

The optional version (for example `1.2.3` or `1.2.3-rc.1`) is passed to `dotnet publish` as `-p:Version`; without it the build says `0.0.0-dev`. The zip is `tools\backup\bin\Release\net10.0-windows\win-x64\backup-win-x64.zip`.

## Releases

Pushing a tag `v*` runs `.github/workflows/release.yml`: build, test, publish, zip, SHA-256 in the release notes and a build provenance attestation of the zip (skipped while the repository is private). The release is created as a draft, the zip is uploaded, then it is published, because a published release is immutable. It needs one repository secret per settings key, all required (a missing one fails the release): `API_HOST`, `APIV2_HOST`, `WEB_HOST`, `PHOTO_HOST`, `STATIC_HOST`, `AVATAR_HOST`, `OAUTH_CONSUMER_KEY`, `CLIENT_EXE`.

Separately, `.github/workflows/deploy.yml` runs (through `workflow_run`) after a successful `ci` run for a push to `main`, never directly on the push, and deploys the website to the VPS (see [docs/deploy.md](docs/deploy.md)). Its secrets are `DEPLOY_HOST`, `DEPLOY_USER` and `DEPLOY_KEY`; without them it deploys nothing.

### Versioning

The git tag is the only version: `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-suffix`. The workflow takes the version from it (the exe, its window title and the backup manifest show it); a local build says `0.0.0-dev`.

- Patch: fixes only.
- Minor: anything new, including any change to the manifest format.
- `-rc.N` (for example `v0.2.0-rc.1`): test builds. A tag with a suffix is published as a prerelease.
- `1.0.0`: when the community server accepts these backups.

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

## CI and updates

`.github/workflows/ci.yml` runs on pushes to `main`, on pull requests and by hand. Two jobs: `build` (Windows: build and test the solution) and `docker` (Ubuntu: build the website's image, start it and check `/feed` and `/settings/language`).

`.github/dependabot.yml` opens weekly pull requests in these groups: NuGet (`dotnet`: `Microsoft.*` and `System.*` together, other packages alone), `dotnet-sdk` (`global.json` and the `dotnet/sdk` and `dotnet/aspnet` tags of `src/Web/Dockerfile` as one pull request), GitHub Actions, and Docker Compose (the images of `deploy/`). `.github/workflows/dependabot-locks.yml` adds the regenerated `packages.lock.json` files to an SDK pull request. The SDK details are in [docs/deploy.md](docs/deploy.md).

## Commits

Conventional Commits (`feat:`, `fix:`, `docs:`, `ci:`, `chore:` ...), imperative subject under 72 characters, one purpose per commit.
