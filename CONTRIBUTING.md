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

Pushing a tag `v*` runs `.github/workflows/release.yml`: build, test, publish, the release files (the backup tool's zip; the launcher's `HunterResurgence-win-Setup.exe`, its Velopack packages and `releases.win.json`), the SHA-256 of the zip and the Setup in the release notes and a build provenance attestation of each (skipped while the repository is private). It builds with exactly the SDK of `global.json` and restores in locked mode (see [CI and updates](#ci-and-updates)). The release is created as a draft, the release files are uploaded, then it is published, because a published release is immutable. It needs one repository secret per settings key, all required (a missing one fails the release): `WEB_HOST`, `PHOTO_HOST`, `STATIC_HOST`, `AVATAR_HOST`, `OAUTH_CONSUMER_KEY`, `CLIENT_EXE`, and optionally `SERVER_URL` (the community server's address, for the backup tool's "Send to the community server" button); for the launcher's zip, which is built only when `PLAY_SERVER_URL` (the launcher's `server_url`) is set (without it the release has the backup tool alone), and then needs `CRASH_HOST` and `WEB_HOST` (the launcher's `crash_host` and `game_web_host`) and `STEAM_APP_ID` (optional: without it there is no launcher zip, the others are unaffected; with it the other four are required), `LAUNCHER_CLIENT_EXE`, `GRAPHICS_FILE`, `OPTIONS_FILE`, `OPTIONS_KEY_PREFIX` besides `WEB_HOST`; and optionally `LINK_DISCORD`, `LINK_BACKUP`, `LINK_SOURCE`, `GAME_NAME`, `GAME_FULL` (the launcher's `link_discord`, `link_backup`, `link_source`, `game_name`, `game_full`; an unset one leaves the placeholder, i.e. not configured). Whitespace (spaces, tabs, line breaks) around a secret's value is removed. A required secret that holds only whitespace fails the release, and so does a `PLAY_SERVER_URL` that does; an optional `SERVER_URL` that holds only whitespace is left out.

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

`.github/workflows/ci.yml` runs on pushes to `main`, on pull requests and by hand. Five jobs: `build` (Windows: build and test the solution, then run `tools\backup\publish.cmd` with `tools/backup/settings.example.json` and check that the release zip holds exactly `backup.exe` and `settings.json` (then for the launcher: `src\Launcher\publish.cmd` after installing `vpk`, a publish folder with `resurgence-launcher.exe`, `settings.json` and `wwwroot` and a release folder with the Setup, the full package and `releases.win.json`), so a change that breaks the publish fails the pull request and not a release tag), `server-ubuntu` (Ubuntu: the server's tests, because operators run the server on Linux), `scripts` (Ubuntu: actionlint on the workflows, `docker compose config` on `deploy/compose.yml`, shellcheck on `deploy/firewall.sh`, and the firewall script run against stub `iptables` and `curl`, where no ranges from Cloudflare must change no rule), `docker-server` (Ubuntu: build the server's image, start it with an empty install, check that it answers and may write `/data`, and run the backup command) and `docker` (Ubuntu: build the website's image, start it and check `/feed`, `/settings/language`, `/robots.txt` and the security headers, that the app user can write its `.aspnet` key folder (the cookie-signing keys), then the pictures of `IMAGES_DIR`: labelled boxes without them, an `<img>` and `image/png` with two, SVG refused, `/images.json` listing them (`{}` without), and `/images-missing.json` listing a box `/feed` drew ([docs/images.md](docs/images.md))). `build`, `server-ubuntu` and the release install exactly the SDK of `global.json` (`.github/actions/setup-sdk`) and restore in locked mode: a pull request that changes a `PackageReference` without its `packages.lock.json` fails with NU1004. Regenerate the lock files with `dotnet restore hunter-resurgence.slnx --force-evaluate` using exactly the SDK of `global.json` (`dotnet --version` in the repository root must print that version; a newer SDK installed next to it is picked first because of `rollForward`), and commit them; lock files that a newer local SDK rewrote must not be committed. The `vpk` version in `ci.yml` and `release.yml` is the version of the `Velopack` package in `src/Launcher/Resurgence.Launcher.csproj`: change them together. Details: [docs/deploy.md](docs/deploy.md).

`.github/dependabot.yml` opens weekly pull requests in these groups: NuGet (`dotnet`: `Microsoft.*` and `System.*` together, other packages alone), `dotnet-sdk` (`global.json` and the `dotnet/sdk` and `dotnet/aspnet` tags of `src/Web/Dockerfile` and `server/Server/Dockerfile` as one pull request), GitHub Actions (the workflows and the composite actions in `.github/actions/`), and Docker Compose (the images of `deploy/`). `.github/workflows/dependabot-locks.yml` commits the regenerated `packages.lock.json` files to Dependabot's SDK and NuGet pull requests (a NuGet update also changes the lock files of the projects that reference the bumped one). The `ci` run on that lock commit waits for the maintainer's approval; once approved and green, the pull request merges without `--admin`. The details are in [docs/deploy.md](docs/deploy.md).

## Commits

Every pull request is squash-merged into one commit on `main` ([ADR 0002](docs/adr/0002-squash-merge-pull-requests.md)), so the rule is on the pull request: one purpose per pull request, and its title is a Conventional Commits subject (`feat:`, `fix:`, `docs:`, `ci:`, `chore:` ...), imperative, at most 65 characters (GitHub adds ` (#<n>)`). The squash commit is that title only; the branch's commits stay in the pull request. Dependabot's titles are kept as Dependabot writes them. The branch's commits follow Conventional Commits too, for review; review fixes need no fixup or rebase.
