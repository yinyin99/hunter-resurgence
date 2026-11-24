# Contributing

This project is licensed under the GNU AGPL-3.0. By contributing you agree that your contribution is released under the same license.

## Rules

These apply to every file and every commit message:

- Only README.md and the protocol literals the client requires name the game. README.md does it in plain text; the literals (the names the client itself sends or loads that carry the game's name) are all in `server/Server/ProtocolLiterals.cs` and nowhere else. Everywhere else, say "the game".
- No real hostnames or domains of the publisher, no user ids, handles, tokens or email addresses of real people. Use placeholders (`<api-host>`, `<client-exe>`, ...).
- No game files and no game data (assets, captures, dumps, saved replies of real accounts). Never patch, ship or download game files: the client is redirected with the hosts file and a local certificate (for now; the method may change).
- Nothing about how knowledge of the game or its servers was obtained: no reverse-engineering notes, addresses, tool names, capture or live-session logs, nothing about a person's account or runs.

Real values for a build (hosts, consumer key, exe name) live in a `settings.json` that is never committed (it is git-ignored). Releases read them from repository secrets. Each release zip ships its `settings.json` in plain text, because the launcher needs its game keys and the names it points at the community server, and the backup tool needs it to reach the official servers; the rules above apply to the repository's files and commits, not to that file.

## Build and test

Needs the .NET SDK pinned in `global.json` (Windows).

```
dotnet build hunter-resurgence.slnx -c Release
dotnet test hunter-resurgence.slnx -c Release --no-build
dotnet run --project src/Web
```

The solution holds the server (`server/Server`, with its tests in `server/Server.Tests`, see [docs/server.md](docs/server.md)), the backup tool (`tools/backup`), the launcher (`src/Launcher`, with the player kit inside it: [docs/launcher.md](docs/launcher.md#the-player-kit)), the website (`src/Web`), the library they share (`src/Ui`), the service contracts and mocks (`src/Services`) and the restore library (`src/Restore`; the server's `import backup` stores what it reads). The tests are in `tests/Resurgence.Tests` (xUnit v3) and `dotnet test` exits 0 when every test passes: the backup tool's tests use a fake HTTP handler and a fake process, the launcher's use temporary files and a test server on 127.0.0.1, the player kit's (in the launcher's folder) use temporary files and test servers on 127.0.0.1 (one of them makes a key in your Windows key store and deletes it again; none edits the hosts file or the trust store), the restore tests build synthetic backup zips in memory and read one backup zip the backup tool writes against the fake handler; none changes anything on the PC or uses the network, and none reads anything else from it except `LauncherServicesTests` and `LanguageSwitcherTests`, which build the launcher's real services (the second with a temporary prefs file) and so read what a launcher start reads (see [docs/launcher.md](docs/launcher.md)), and `WebTests/WebAppTests`, which reads `src/Web/settings.json` when it exists. The component tests in `WebTests` (bUnit) render the `src/Ui` pages with the service mocks, no browser; `WebAppTests` (`WebApplicationFactory`) runs the real `src/Web` app in memory in the Development environment (see [docs/website.md](docs/website.md#tests)). `server/Server.Tests` also holds one journey test (a player from sign-in to the score board on the server's loopback Kestrel) and references `src/Services`, to check that the website's server services read the server's own replies. The last command starts the website on `http://localhost:5080`.

The launcher, the website and the backup tool read a `settings.json` (next to the exe, or in `src/Web/`), with real values that are never committed. Start from `src/Launcher/settings.example.json` (the website reads the same keys for the names and links) or `tools/backup/settings.example.json`. The keys are listed in [docs/backup.md](docs/backup.md) and [docs/launcher.md](docs/launcher.md#settingsjson). See also [docs/website.md](docs/website.md) and [docs/restore.md](docs/restore.md). The website's container image and its deploy are in [docs/deploy.md](docs/deploy.md).

To build the backup tool's distributable exe, create a `settings.json` (start from `tools/backup/settings.example.json`) and run:

```
tools\backup\publish.cmd path\to\settings.json [version]
```

The optional version (for example `1.2.3` or `1.2.3-rc.1`) is passed to `dotnet publish` as `-p:Version`; without it the build says `0.0.0-dev`. The release zip is `tools\backup\bin\Release\net10.0-windows\win-x64\backup-win-x64.zip`.

The launcher builds the same way with `src\Launcher\publish.cmd path\to\settings.json [version]` (start from `src/Launcher/settings.example.json`; it needs the Velopack tool `vpk`); see [docs/launcher.md](docs/launcher.md#build-and-run).

## Releases

Pushing a tag `v*` runs `.github/workflows/release.yml`: build, test, publish, the release files (the backup tool's zip; the launcher's `HunterResurgence-win-Setup.exe`, its Velopack packages and `releases.win.json`), the SHA-256 of the zip and the Setup in the release notes and a build provenance attestation of each (skipped while the repository is private). It builds with exactly the SDK of `global.json` and restores in locked mode (see [CI and updates](#ci-and-updates)). The release is created as a draft, the release files are uploaded, then it is published, because a published release is immutable. The backup tool's `settings.json` needs one repository secret per key, all required (a missing or blank one fails the release): `WEB_HOST`, `PHOTO_HOST`, `STATIC_HOST`, `AVATAR_HOST`, `OAUTH_CONSUMER_KEY` and `CLIENT_EXE`; `SERVER_URL` (the community server's address, for the backup tool's "Send to the community server" button) is optional. The launcher is in the release only when `PLAY_SERVER_URL` (the launcher's `server_url`) and `STEAM_APP_ID` are both set (without either the release has the backup tool alone); it then also requires `LAUNCHER_CLIENT_EXE`, `GRAPHICS_FILE`, `OPTIONS_FILE`, `OPTIONS_KEY_PREFIX` and `CRASH_HOST` (the launcher's `client_exe`, `graphics_file`, `options_file`, `options_key_prefix` and `crash_host`) and takes `WEB_HOST` as its `game_web_host`, and optionally `LINK_DISCORD`, `LINK_BACKUP`, `LINK_SOURCE`, `GAME_NAME` and `GAME_FULL` (the launcher's `link_discord`, `link_backup`, `link_source`, `game_name`, `game_full`; an unset one leaves the placeholder, i.e. not configured). Whitespace (spaces, tabs, line breaks) around a secret's value is removed. A required secret that holds only whitespace fails the release (`PLAY_SERVER_URL` and `STEAM_APP_ID`, which only switch the launcher on, are trimmed first: unset or blank builds no launcher; a `PLAY_SERVER_URL` that is not an `https://` address without spaces, or a `STEAM_APP_ID` that is not digits only, fails the release); an optional `SERVER_URL` that holds only whitespace is left out.

The release runs the tests of `tests/Resurgence.Tests` only when the tagged commit's `ci` run on `main` has not passed (yet): after a passed one they would be the same tests on the same commit.

Separately, `.github/workflows/deploy.yml` deploys the website and the community server to the VPS when a `server-X.Y.Z` tag is pushed (see [docs/deploy.md](docs/deploy.md#deploys)). It is its own version, independent of the launcher's `vX.Y.Z`: a merge to `main` deploys nothing, and a launcher release deploys nothing. Its secrets are `DEPLOY_HOST`, `DEPLOY_USER` and `DEPLOY_KEY`; without them it deploys nothing.

### Versioning

The git tag is the only version: `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-suffix`. The workflow takes the version from it (the exe, its window title and the backup manifest show it); a local build says `0.0.0-dev`.

- Patch: fixes only.
- Minor: anything new, including any change to the manifest format.
- `-rc.N` (for example `v0.2.0-rc.1`): test builds. A tag with a suffix is published as a prerelease.

Tag with an annotated tag whose message lists the changes; it becomes the "Changes" section of the release notes:

```
git tag -a v0.2.0 -m "What changed, one line per change"
git push origin v0.2.0
```

### Protections

- Published releases are immutable: their assets and tag are locked, and a version number can never be reused. A failed or wrong release means the next patch number.
- Release tags `vX.Y.Z` cannot be deleted or moved (tag ruleset, no bypass); disabling the ruleset is the only way out.
- Deploy tags `server-X.Y.Z` cannot be deleted or moved either (tag ruleset "tags: protect server deploys", no bypass): a wrong deploy is fixed with the next version, never by moving a tag.
- `-rc.N` prereleases are immutable too and stay public, so use them sparingly.
- Pull requests from forks need the maintainer's approval before CI runs.
- A pull request merges only when the required checks `build` and `server-ubuntu` passed (branch ruleset "main: reviewed changes"); a check skipped by the `changes` job counts as passed. A merged pull request's branch is deleted automatically ("Automatically delete head branches"); workflow artifacts and logs are kept 30 days.

## CI and updates

`.github/workflows/ci.yml` runs on pushes to `main`, on pull requests and by hand. Eight jobs (`checks` is described under [Commits](#commits)): `changes` (lists the pull request's files: when every one is under `docs/` or ends in `.md`, the other jobs are skipped and the pull request is green in under a minute; on a push, by hand, or when `changes` fails, they all run), `build` (Windows: build the solution and run the tests of `tests/Resurgence.Tests`, then run `tools\backup\publish.cmd` with `tools/backup/settings.example.json` and check that the release zip holds exactly `backup.exe` and `settings.json` (then for the launcher: `src\Launcher\publish.cmd` after installing `vpk`, a publish folder with `resurgence-launcher.exe`, `settings.json` and `wwwroot` and a release folder with the Setup, the full package and `releases.win.json`, then the Setup run silently and the installed exe and the link scheme checked, and the uninstall run and the link scheme checked to be gone), so a change that breaks the publish fails the pull request and not a release tag), `server-ubuntu` (Ubuntu: the server's tests, run only here, because operators run the server on Linux), `scripts` (Ubuntu: actionlint on the workflows, `docker compose config` on `deploy/compose.yml`, shellcheck on `deploy/firewall.sh` and `deploy/rollout.sh`, and the firewall script run against stub `iptables` and `curl`, where no ranges from Cloudflare must change no rule, and the real `Caddyfile` in front of two stub backends, to check which one each path reaches and that a request sent during a restart waits instead of getting a 502), `docker-natpt` (Ubuntu: build the punch-through server's image from `server/natpt` and check that the server starts and listens), `docker-server` (Ubuntu: build the server's image, start it with an empty install, check that it answers and may write `/data`, and run the backup command) and `docker` (Ubuntu: build the website's image, start it and check `/feed`, `/robots.txt` and the security headers, that the app user can write its `.aspnet` key folder (the cookie-signing keys), then the pictures of `IMAGES_DIR`: labelled boxes without them, an `<img>` and `image/png` with two, SVG refused, `/images.json` listing them (`{}` without), and `/images-missing.json` counting the two as present and listing the other slots of the manifest, the launcher's pages included ([docs/images.md](docs/images.md))). The three `docker*` jobs build through `.github/actions/build-image`, with a layer cache kept as the `buildcache` tag of each image's GHCR package: every run reads it, a push to `main` writes it, and `deploy.yml` reads it for the same commit. `build`, `server-ubuntu` and the release install exactly the SDK of `global.json` (`.github/actions/setup-sdk`, which also caches the NuGet packages, keyed by the `packages.lock.json` files) and restore in locked mode: a pull request that changes a `PackageReference` without its `packages.lock.json` fails with NU1004. Regenerate the lock files with `dotnet restore hunter-resurgence.slnx --force-evaluate` using exactly the SDK of `global.json` (`dotnet --version` in the repository root must print that version; a newer SDK installed next to it is picked first because of `rollForward`), and commit them; lock files that a newer local SDK rewrote must not be committed. The `vpk` version in `ci.yml` and `release.yml` is the version of the `Velopack` package in `src/Launcher/Resurgence.Launcher.csproj`: change them together. Details: [docs/deploy.md](docs/deploy.md).

`.github/workflows/codeql-caches.yml` runs every 6 hours and deletes the CodeQL caches (`codeql-overlay-base-database-*`, one per language per push to `main`) except the newest per language, so the repository's Actions caches stay far under GitHub's 10 GB limit.
Add new actions pinned to a commit SHA with the version as a comment (`uses: owner/repo@<sha> # vX.Y.Z`); Dependabot updates both.

`.github/dependabot.yml` opens weekly pull requests in these groups: NuGet (`dotnet`: `Microsoft.*` and `System.*` together, other packages alone), `dotnet-sdk` (`global.json` and the `dotnet/sdk` and `dotnet/aspnet` tags of `src/Web/Dockerfile` and `server/Server/Dockerfile` as one pull request), GitHub Actions (the workflows and the composite actions in `.github/actions/`), and Docker Compose (the images of `deploy/`). `.github/workflows/dependabot-locks.yml` commits the regenerated `packages.lock.json` files to Dependabot's SDK and NuGet pull requests (a NuGet update also changes the lock files of the projects that reference the bumped one). The `ci` run on that lock commit waits for the maintainer's approval; once approved and green, the pull request merges without `--admin`. The details are in [docs/deploy.md](docs/deploy.md).

## Commits

Every pull request is squash-merged into one commit on `main` ([ADR 0002](docs/adr/0002-squash-merge-pull-requests.md)), so the rule is on the pull request: one purpose per pull request, and its title is a Conventional Commits subject (`feat:`, `fix:`, `docs:`, `ci:`, `chore:` ...), imperative, at most 65 characters (GitHub adds ` (#<n>)`). The squash commit is that title only; the branch's commits stay in the pull request. Dependabot's titles are kept as Dependabot writes them. The branch's commits follow Conventional Commits too, for review; review fixes need no fixup or rebase.

The scripts in `tools/checks/` (Node 20+; `parse` also needs Python with PyYAML) are what the `checks` job of `ci` runs on a pull request's changed files and title: `commit-title.mjs`, `format.mjs`, `translations.mjs`, `big-files.mjs` and `parse.mjs`. From the repository root, `node tools/checks/translations.mjs [files...]` checks those files, or every tracked file with none; `node tools/checks/commit-title.mjs [--pr] "<title>"` checks a title (`--pr`: 65 characters instead of 72). Each prints one line per problem and exits 1. The git hooks in `tools/hooks/` run them before a commit and a push: `commit-msg` (the title), `pre-commit` (the staged files) and `pre-push` (the private-names scan of the maintainer's clone when it has one, `.git/hooks/publish-check.py`, then the tests of the projects the pushed commits touch: `server/Server.Tests` for `server/`, `src/Restore` and `src/Services`, and on Windows `tests/Resurgence.Tests` for `src/`, `tools/backup` and `tests/`; `HOOK_TESTS=0 git push` skips the tests). They need `node`, `dotnet` and, for the scan, `python`. Turn them on once per clone by pointing git's hooks path setting (`git help config`) at `tools/hooks`. `format.mjs` checks whole `.cs` files against the style of `.editorconfig` and blocks the pull request like the others; `dotnet format whitespace . --folder --include <files>` fixes them.
