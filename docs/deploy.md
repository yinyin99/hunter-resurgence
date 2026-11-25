# Deploy the website and the community server

## What this is

The [website](website.md) (`src/Web/`) and the community server ([server.md](server.md), `server/Server/`) in containers on one Ubuntu 24.04 VPS, behind Caddy and Cloudflare. Without `SERVER_URL` the website is the **mock** website: its pages show fixed, fictional data. With it (as in `compose.yml`) the sign-in, the store and buying, the profiles, the hunterscore and minigame boards, the wallet, the missions, the friends, the messages and the restore page are the community server's, the news are the project's Discord announcements, and the pages without a source are hidden ([website.md](website.md#sign-in), [Known limits](#known-limits)). A banner at the bottom of every page (`Wip.Banner` in `src/Ui/Strings*.resx`) says that some features of the original are still missing; remove it when that is no longer so.

| File | Purpose |
|---|---|
| `src/Web/Dockerfile` | the image (build context: the repository root). It listens on port 8080 as a non-root user. Its SDK tag is the version of `global.json` and its `aspnet` tag the runtime that SDK ships: a new SDK means `global.json`, both tags and every `packages.lock.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate` with that SDK) in one change, or the locked restore fails with NU1004 |
| `deploy/compose.yml` | production stack: `caddy` (ports 80 and 443) in front of `web` and `server`, plus `server-backup` ([Backups](#backups)) and `natpt` (UDP 10000 straight on the host, [Punch-through server](#punch-through-server)). `web` and `server` are on `internal` only, `server-backup` on no network. The named volume `dataprotection` keeps the website's cookie-signing keys (`/home/app/.aspnet/DataProtection-Keys`) across deploys, so cookies stay valid; the Dockerfile gives that folder to the app user, and a new named volume takes its owner. `serverdata` holds the server's databases at `/data`, `serverbackups` the copies. The Caddy image is pinned to a minor tag (`caddy:2.11`); Dependabot's `docker-compose` entry for `/deploy` updates it weekly |
| `deploy/Caddyfile` | the site `{$SITE_DOMAIN}` with the Cloudflare origin certificate: game requests and the server's own paths (`/auth/*`, `/account/*`, `/hunters`, `/hunters/*`, `/catalog/*`, `/photos/*`, `/stats/*`, `/news`, `/news/*`, the minigame boards under `/leaderboards/*`) to `server`, the rest to `web` ([routing](#the-community-server)); then `import sites/*.caddy` |
| `deploy/.env.example` | the values of `.env` |
| `server/Server/Dockerfile` | the community server's image, like the website's (same SDK and runtime tags, bumped together; build context: the repository root): non-root, listens on plain HTTP on port 8080 (the proxy's side), `SERVER_SETTINGS=/srv/settings.json`, and creates `/data` and `/backups` owned by the app user, because an empty named volume takes the owner of the folder it is mounted over |
| `server/natpt/` | the punch-through server's image: `ptserver.cpp` (RakNet's NAT plugin, about 30 lines) and a Dockerfile that clones RakNet at commit `44ec8ec`, changes one line of its message numbers and builds only what the server needs ([Punch-through server](#punch-through-server)). Build context: `server/natpt` |
| `deploy/firewall.sh` | ports 80 and 443 for Cloudflare only |
| `deploy/rollout.sh` | the deploy's roll-out on the VPS: new tags, health check, the old tags back if it fails ([Deploys](#deploys)) |
| `deploy/compose.dev.yml`, `deploy/Caddyfile.dev` | local test on `https://localhost` |
| `.github/workflows/ci.yml` | the `build` job also runs `tools\backup\publish.cmd` with `tools/backup/settings.example.json` and checks that the release zip holds exactly `backup.exe` and `settings.json` (the release's publish step, without a tag); for `src\Launcher\publish.cmd` it installs `vpk`, checks that the publish folder holds the exe, `settings.json` and `wwwroot` and that the release folder holds the Setup, the full package and `releases.win.json`, then runs the Setup silently and checks the installed exe and the link scheme, and uninstalls it and checks that the link scheme is gone |
| `.github/workflows/ci.yml` | the `server-ubuntu` job runs the server's tests on Ubuntu ([server.md](server.md#tests)), the only run of them; the `changes` job skips every other job for a pull request that changes only `docs/` and `.md` files |
| `.github/workflows/ci.yml` | the `docker-natpt` job builds `server/natpt` and checks that the server starts and logs that it listens on UDP 10000 |
| `.github/workflows/ci.yml` | the `docker-server` job builds the server's image, starts it with an empty install and checks that it answers, that the app user can write `/data` and that `server backup` runs |
| `.github/workflows/ci.yml` | the `docker` job builds the image, starts it and checks that `/feed` shows the banner and names the fingerprinted `shell.<hash>.js` (the import map, see [website.md](website.md#build-and-run)), that `/robots.txt` is served ([website.md](website.md#build-and-run)), and that `/feed` and a 404 carry the security headers ([website.md](website.md#security-headers)) |
| `.github/workflows/ci.yml` | the `scripts` job runs actionlint on the workflows, `docker compose config` on `deploy/compose.yml` with `deploy/.env.example`, shellcheck on `deploy/firewall.sh` and `deploy/rollout.sh`, the roll-out script against stub `docker` and `curl` (healthy: the new tags stay; a restarting container or a 502: the old tags are back), and the firewall script against stub `iptables` and `curl` (no ranges: it fails and adds no rule; two ranges: the `RETURN`, `DROP` and `DOCKER-USER` rules); then it puts the real `Caddyfile` in front of two stub backends and checks which one each path reaches ([routing](#the-community-server)) and that a request sent while the backends are gone is answered once they are back ([Deploys](#deploys)). The `docker` job also checks that the app user can write `/home/app/.aspnet/DataProtection-Keys` |
| `.github/workflows/deploy.yml` | build, push and roll out on a `server-X.Y.Z` tag of a commit of `main` whose `ci` passed |
| `.github/workflows/dependabot-locks.yml` | adds the regenerated `packages.lock.json` files to a Dependabot SDK or NuGet pull request |
| `.github/workflows/discord-release-threads.yml`, `.github/scripts/discord-release-threads.mjs` | after a release is published, resolves the Discord forum threads linked in the release's pull requests and the issues they close ([Discord threads at release](#discord-threads-at-release)) |
| `.github/workflows/codeql-caches.yml` | every 6 hours (and by hand), deletes the Actions caches `codeql-overlay-base-database-*` that CodeQL's default setup saves for every push to `main` (about 1 GB a day), except the newest per language, the only one CodeQL reads |
| `.github/actions/setup-sdk/action.yml` | installs exactly the SDK of `global.json` for `ci`, `release` and `dependabot-locks`: it reads `sdk.version`, turns `rollForward` off in the runner's copy of `global.json` (never committed), runs `setup-dotnet` with that version and the NuGet package cache (keyed by every `packages.lock.json`) and prints `dotnet --version` |
| `.github/actions/build-image/action.yml` | `docker buildx build` for the `docker*` jobs of `ci`, with the layer cache in the `buildcache` tag of the image's GHCR package (`ghcr.io/<owner>/<repo>-web:buildcache`, `-server`, `-natpt`): read by every run, written (all stages, `mode=max`) by a push to `main` only; a cache that cannot be read or written never fails the build |

**SDK and NuGet updates.** Dependabot opens one weekly pull request, the `dotnet-sdk` multi-ecosystem group in `.github/dependabot.yml`, when a new SDK patch or feature band is out. It is meant to move `global.json` and the `dotnet/sdk` and `dotnet/aspnet` tags of both Dockerfiles together, but it may bump only the Dockerfile tags (#386: `global.json` stayed on 10.0.403, so the lock files were not regenerated and `docker` failed with NU1004): then push a commit that sets `global.json` to the Dockerfile's SDK and start `dependabot-locks` on the branch. A new .NET major is ignored, because it is a migration. A NuGet pull request (the `nuget` entry of `.github/dependabot.yml`) changes the bumped project's `.csproj` and `packages.lock.json`, but not the lock files of the projects that reference it (`server/Server.Tests` references `server/Server`; `tests/Resurgence.Tests` references most of `src` and `tools`). `dependabot-locks.yml` runs on every Dependabot pull request that changes `global.json`, a Dockerfile above, a `.csproj` or a `packages.lock.json`; it skips pull requests by anyone else. It restores with exactly the SDK of `global.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate`; the action turns `rollForward` off for the rest of the job, or the newest 10.0 SDK on the runner would write the lock files) and commits any changed lock files to the pull request's branch as "build: regenerate lock files". The `pull_request` runs on that commit by `github-actions[bot]` are held for approval ("action required"), so the ruleset `main: reviewed changes` sees no required check `build` and the pull request stays blocked (#124 and #117: the ruleset's records of those merges show only `required_status_checks` failing, "Required status check "build" is expected."). The maintainer approves the held `ci` run (Actions, the run, Approve and run; or `gh api -X POST repos/{owner}/{repo}/actions/runs/<id>/approve`; `gh run list --commit <sha> --workflow ci.yml` gives the id). Its checks then count for the pull request, and once they pass it merges with `gh pr merge <n> --squash --delete-branch`, without `--admin` (#386). The held `dependabot-locks` run needs no approval: it would skip the bot's commit. For an SDK update, check that the `aspnet` tag is the runtime that the new SDK ships: Dependabot bumps each tag to its own latest version. If the lock-file job fails, or `build` or `docker` fails with NU1004: start the job again with Actions, `dependabot-locks`, Run workflow, on the pull request's branch, or run the restore with that SDK yourself and push the lock files. After the bot's lock-file commit Dependabot no longer updates the pull request (no rebase, no newer version): comment `@dependabot recreate` to start over from `main`, and the job runs again on the new commit.

**Locked restore.** The `build` and `server-ubuntu` jobs of `ci.yml` and the job of `release.yml` set `RestoreLockedMode: "true"` (an environment variable, which MSBuild reads as the property), so every restore in the job, including the implicit ones of `dotnet build`, `dotnet test` and `publish.cmd`, uses the committed `packages.lock.json` files as they are and fails with NU1004 when one does not match its project. All three build with exactly the SDK of `global.json` (the action above), so the SDK of a release is the one that was reviewed. `global.json` keeps `rollForward: latestFeature` for local builds, which restore unlocked. A pull request that changes a `PackageReference` (or the SDK) without its lock files therefore fails `build` and `server-ubuntu`; regenerate them with `dotnet restore hunter-resurgence.slnx --force-evaluate` using exactly the SDK of `global.json` (a newer local SDK writes its own version of the packages the SDK adds itself, such as `Microsoft.NET.ILLink.Tasks`, so do not commit lock files it rewrote). On a Dependabot SDK or NuGet pull request `build` and `server-ubuntu` fail until `dependabot-locks.yml` has pushed the lock files and a maintainer has approved the held `ci` run on that commit.

## VPS prerequisites

- Ubuntu 24.04, Docker Engine and the Compose plugin from [Docker's apt repository](https://docs.docker.com/engine/install/ubuntu/).
- A non-root user for deploys (`deploy` below) in the `docker` group, with the public half of the deploy key in `~/.ssh/authorized_keys`. Never root.
- That user in `systemd-journal`, once, as root, so it can read the [Logs](#logs) without sudo: `sudo usermod -aG systemd-journal deploy` (applies from its next login).
- The shared network, once: `docker network create edge`.
- The journal's retention, once, as root ([Logs](#logs)):

```
sudo mkdir -p /etc/systemd/journald.conf.d
printf '[Journal]\nStorage=persistent\nMaxRetentionSec=30day\nSystemMaxUse=5G\n' | sudo tee /etc/systemd/journald.conf.d/retention.conf
sudo systemctl restart systemd-journald
```

- `~/web/` of that user holding:

```
compose.yml               copied by every deploy
Caddyfile                 copied by every deploy
.env                      from deploy/.env.example, real values
certs/origin.pem          Cloudflare origin certificate
certs/origin.key          its key (chmod 600)
sites/                    other projects' site files, may be empty
compose.override.yml      optional, host-only additions (below)
```

## The .env

```
SITE_DOMAIN=<domain>
WEB_IMAGE=ghcr.io/<owner>/<repo>-web
WEB_TAG=latest
SERVER_IMAGE=ghcr.io/<owner>/<repo>-server
SERVER_TAG=latest
NATPT_IMAGE=ghcr.io/<owner>/<repo>-natpt
NATPT_TAG=latest
SERVER_DIR=/home/deploy/server
```

`WEB_IMAGE`, `SERVER_IMAGE` and `NATPT_IMAGE` are in lower case. Every deploy rewrites the `WEB_TAG`, `SERVER_TAG` and `NATPT_TAG` lines with the commit sha it rolled out, so the lines have to exist. Every deploy also replaces the `LINK_BACKUP=<url>`, `LINK_DISCORD=<url>`, `LINK_SOURCE=<url>`, `GAME_NAME="<name>"` and `GAME_FULL="<name>"` lines with the repository secrets of the same names, the ones the release uses too (no line for an unset secret; `LINK_SOURCE` defaults to the repository the deploy runs from); compose passes them to `web`, and `LINK_DISCORD` to `server` too.

The website's other settings are environment variables of the `web` service: `FORWARDED_TRUSTED`, `GAME_FULL`, `GAME_NAME`, `IMAGES_DIR`, `LINK_BACKUP`, `LINK_DISCORD`, `LINK_SOURCE` and `SERVER_URL`. `IMAGES_DIR` is `/images`, where compose mounts `SERVER_DIR/images` read-only: the slot pictures ([images.md](images.md)). `SERVER_URL` is the community server's address on the `internal` network (for example `http://server:8080`). With it the website signs players in through the server (see [website.md](website.md#sign-in)); without it the website keeps its mock session. The server's `web_host` setting must be the website's domain, and Caddy has to send the server's paths (`/auth/*` first, the others in [the routing table](#the-community-server)) to the server for the sign-in links and the launcher to work.

`settings.json` (the other links, see [launcher.md](launcher.md#settingsjson)) is optional. The game name and the backup tool link come from the `GAME_NAME`, `GAME_FULL`, `LINK_BACKUP` and `LINK_DISCORD` variables, which win over `game_name`, `game_full`, `link_backup` and `link_discord` of the file. To use one, put it in `~/web/` and mount it in `compose.override.yml`, which Compose reads next to `compose.yml` and deploys never overwrite:

```yaml
services:
  web:
    volumes: ["./settings.json:/app/settings.json:ro"]
```

## Cloudflare

- DNS: a proxied (orange cloud) `A` record for `<domain>` pointing at the VPS. For `www`: a proxied record with any placeholder target (for example `AAAA 100::`) and a Redirect Rule from `www.<domain>` to `https://<domain>`, keeping the path and the query string, 301. The VPS serves only `<domain>`.
- SSL/TLS mode **Full (strict)**; Always Use HTTPS on; minimum TLS version 1.2; WebSockets on (the default, Blazor needs them).
- An **Origin Certificate** for `<domain>` (add `*.<domain>` if other sites will share it), saved on the VPS as `certs/origin.pem` and `certs/origin.key` (`chmod 600`). With these files Caddy requests no certificate of its own.
- The app sends HSTS (`max-age=31536000`, this host only) and the other security headers itself ([website.md](website.md#security-headers)); do not add them in the Caddyfile or Cloudflare too.

Caddy passes Cloudflare's `CF-Connecting-IP` to the apps as `X-Forwarded-For`. The website accepts forwarded headers from Caddy's address only (`FORWARDED_TRUSTED`): `caddy` has a fixed address on `internal` (`172.30.10.2`, in a pinned subnet `172.30.10.0/24`, both in `deploy/compose.yml`), and `FORWARDED_TRUSTED` and the server's `trusted_proxies` repeat it: change them together. The header is trustworthy only because the firewall lets nothing but Cloudflare reach ports 80 and 443.

## Firewall

- `ufw`: default deny incoming; allow SSH from your own address or range only (`ufw allow from <your-range> to any port 22 proto tcp`).
- **Pitfall:** Docker publishes container ports through its own iptables rules, before `ufw`'s, so `ufw` rules do **not** protect ports 80 and 443. The rules for them go in Docker's `DOCKER-USER` chain. `deploy/firewall.sh` reads Cloudflare's [IPv4](https://www.cloudflare.com/ips-v4) and [IPv6](https://www.cloudflare.com/ips-v6) ranges, rebuilds a `CLOUDFLARE` chain with them (anything else is dropped) and jumps to it from `DOCKER-USER` for ports 80 and 443 on the public interface. Running it again replaces the old ranges. If Cloudflare sends no ranges, it stops with an error and changes nothing (the `scripts` job checks this against stub `iptables` and `curl`).

```
sudo install -m 755 firewall.sh /usr/local/sbin/cloudflare-only
sudo cloudflare-only eth0          # the public interface: ip -br addr
```

iptables rules are lost on reboot, and Cloudflare changes its ranges now and then: run the script after Docker starts and once a week, with `/etc/systemd/system/cloudflare-only.service`:

```ini
[Unit]
After=docker.service network-online.target
Wants=network-online.target
[Service]
Type=oneshot
ExecStart=/usr/local/sbin/cloudflare-only eth0
[Install]
WantedBy=multi-user.target
```

and `/etc/systemd/system/cloudflare-only.timer`:

```ini
[Timer]
OnCalendar=weekly
Persistent=true
[Install]
WantedBy=timers.target
```

then `sudo systemctl daemon-reload && sudo systemctl enable --now cloudflare-only.service cloudflare-only.timer`.

Test from a host that is not Cloudflare: `curl -m 5 --resolve <domain>:443:<vps-ip> https://<domain>/` must time out, while `https://<domain>/` through Cloudflare works.

## First start

1. Copy `deploy/compose.yml` and `deploy/Caddyfile` to `~/web/`, create `.env`, `certs/` and `sites/` (above), and the server's folder with its `images/` subfolder, as `deploy` ([The community server](#the-community-server)); a folder that compose has to create belongs to root and `deploy` cannot copy pictures into it.
2. Make the images public once they exist (after the first deploy run): GitHub, the packages `<repo>-web`, `<repo>-server` and `<repo>-natpt` of `<owner>`, Package settings, Change visibility, Public. The repository is public and the images hold no secrets, so the VPS pulls without a login. (If they must stay private: `docker login ghcr.io` on the VPS with a token that has `read:packages` only.)
3. `cd ~/web && docker compose up -d`, then `docker compose ps`: `web` and `server` healthy, `caddy`, `natpt` and `server-backup` running.
4. Open `https://<domain>/` through Cloudflare.

## Deploys

`.github/workflows/deploy.yml` runs when a tag `server-MAJOR.MINOR.PATCH` is pushed. The website and the community server have this version of their own, separate from the launcher's `vX.Y.Z` (`release.yml`): a merge to `main` deploys nothing, a launcher release deploys nothing, and a server tag releases no launcher. Patch: fixes only; minor: anything new; major: a change that needs the operator (a new setting, a file on the host). The first is `server-1.0.0`.

```
git checkout main && git pull
git tag -a server-1.1.0 -m "What changed, one line per change"
git push origin server-1.1.0
```

`git log server-1.0.0..main --oneline` lists what the next deploy takes. The run first checks, and fails without rolling anything out, that the tag has that form, that its commit is on `main` (a commit of an unmerged branch is refused), and that the `ci` run of that commit's push to `main` succeeded; a tag pushed while that `ci` still runs waits for it, up to 30 minutes. Deploys run one at a time, in the order the tags were pushed: push one tag at a time. Forks get no secrets. Then the deploy:

1. builds the website, server and punch-through images from that commit and pushes `ghcr.io/<owner>/<repo>-web:<sha>`, `-server:<sha>` and `-natpt:<sha>`, each also as `:latest`; the layers come from the `buildcache` tag that the `ci` run of the same commit on `main` wrote, so the log shows them `CACHED` and nothing is compiled again;
2. copies `compose.yml`, `Caddyfile` and `rollout.sh` to `~/web/` over SSH, writes `LINK_BACKUP`, `LINK_DISCORD`, `LINK_SOURCE`, `GAME_NAME` and `GAME_FULL` into `.env`, then runs `rollout.sh <sha>`: it notes the `WEB_TAG`, `SERVER_TAG` and `NATPT_TAG` that run now, writes `<sha>` into the three, `docker compose pull && docker compose up -d`, reloads Caddy, checks the health (below) and removes dangling images. Compose stops the old `server` before it starts the new one, so two never write the database at once.

The health check polls for up to 60 s (every 5 s; a healthy deploy adds a few seconds): every container of the stack is running (not restarting or exited, and not `unhealthy` by its own health check), and Caddy, called from the VPS with the site's `SITE_DOMAIN` as Host, answers `/` (the website) and `/stats/game` (the server) with anything but 502, 503 or 504. If that fails, or `pull`, `up -d` or the Caddy reload does, the script prints the last 30 log lines of the failed containers, writes the tags it noted back into `.env`, runs `docker compose up -d` again and fails the `deploy` run: the previous images run again and the run is red (the log names the containers). It puts back images only, not the `Caddyfile` or `compose.yml`, and not a database that the new server already migrated (see [Rollback](#rollback)). `rollout.sh` is tested with stub `docker` and `curl` in the `scripts` job of `ci`; the real path runs only on a `server-*` tag.

While `server` or `web` is recreated (about 12 s), Caddy holds a request and dials again every 500 ms for up to 30 s (`lb_try_duration` in the Caddyfile's `upstream` snippet), so a player who is hunting or browsing sees a slow reply, not a 502. Only a failed dial is retried for a request other than `GET`: a heartbeat, an inventory move or a purchase that reached the old container is never sent twice. A restart longer than 30 s still gives 502. The `scripts` job of `ci` checks this: it removes the stub backends, sends a `POST` and a `GET`, and starts the backends again 5 s later.

Repository secrets, separate from the release secrets in [CONTRIBUTING.md](../CONTRIBUTING.md#releases):

| Secret | Value |
|---|---|
| `DEPLOY_HOST` | the VPS address |
| `DEPLOY_USER` | the deploy user |
| `DEPLOY_KEY` | the private half of that user's SSH key (no passphrase) |

Without them (a fork, or no VPS yet) the run ends with a notice and deploys nothing. The host key is taken with `ssh-keyscan` at every run (trust on first use); the stricter option is a fourth secret with the pinned `known_hosts` line.

Repository secrets (Settings, Secrets and variables, Actions, Secrets), the same ones the launcher release reads ([launcher.md](launcher.md#settingsjson)):

| Secret | Value | Unset |
|---|---|---|
| `LINK_BACKUP` | the backup tool link, `https://github.com/<owner>/<repo>/releases/latest/download/backup-win-x64.zip` | `link_backup` of `settings.json`, if any, else no backup tool button |
| `LINK_DISCORD` | the operator's Discord invite (`https://`): the website's feed widget, footer icon and support tab line, and the "Join the Discord" line on the launcher's start page when signed in (the server's `environment`; the website's sign-in pages read the website's own); the launcher release reads the same secret | `link_discord` of the website's and the server's `settings.json`, else none |
| `LINK_SOURCE` | the project's repository, linked by the feed's posts whose link is `link:source`; the download page's launcher button and player guide link are built from it | the repository the deploy runs from (`https://github.com/<owner>/<repo>`) |
| `GAME_NAME` | the game's short name, for `{game}` in the texts | `game_name` of `settings.json`, else "the game" |
| `GAME_FULL` | the name with its edition, for `{game_full}` (the meta tags) | `game_full` of `settings.json`, else the short name |

An unset secret removes its line; spaces and line breaks around a value are trimmed. The launcher release trims `PLAY_SERVER_URL` and `STEAM_APP_ID` the same way: unset or blank builds no launcher, a `PLAY_SERVER_URL` that is not an `https://` address (no spaces) or a `STEAM_APP_ID` that is not digits only fails the release. A `LINK_BACKUP`, `LINK_DISCORD` or `LINK_SOURCE` that is not a plain `https://` URL (no spaces, `'`, `$` or `#`), or a name with anything but letters, digits, spaces and `. : -`, fails the deploy before the rollout, after the image is pushed. The deploy also ends `.env` with a newline before it appends the lines.

## Rollback

Tag an older commit of `main` with the next version and push it: `git tag -a server-1.1.1 <older sha> -m "Roll back to server-1.0.0"`. The deploy rolls that commit out like any other (its image is rebuilt from the commit). A tag is never moved or reused.

A deploy whose containers are not healthy rolls itself back ([Deploys](#deploys)). For a rollback that cannot wait for a run, the running versions are the `WEB_TAG`, `SERVER_TAG` and `NATPT_TAG` lines of `~/web/.env`, and every pushed sha stays in GHCR; find an older one in the list of `deploy` runs on GitHub, on the package page, or with `docker images` on the VPS. Then:

```
ssh deploy@<vps> "cd web && sed -i -e 's/^WEB_TAG=.*/WEB_TAG=<older sha>/' -e 's/^SERVER_TAG=.*/SERVER_TAG=<older sha>/' -e 's/^NATPT_TAG=.*/NATPT_TAG=<older sha>/' .env && docker compose up -d"
```

The next deploy sets the tag again.

Only the newest 20 deploy images of each package stay in GHCR (`ghcr-retention`, weekly, plus `latest` and `buildcache`): a rollback past that is rebuilt by tagging the old commit as above.

A deploy that changes the database schema migrates it at the server's first start, and the older server refuses a newer schema: before such a deploy, copy the database (`docker compose exec -T server-backup dotnet server.dll backup /backups 7`, [Backups](#backups)), since rolling back then means restoring that copy.

## The community server

**Routing** (`deploy/Caddyfile`, one domain `<domain>`, in this order):

| Request | Goes to |
|---|---|
| carries the header `X-Game-Host` (every request of a player's local forwarder, for every game host name, the signup host included) | `server:8080`, header kept |
| path `/auth/*`, `/account/*`, `/hunters`, `/hunters/*`, `/catalog/*`, `/photos/*`, `/stats/*`, `/news` or `/news/*` (the `@server_paths` matcher, the one place to change the server's website-facing paths): sign-in, the player's own data (cookie and `X-Requested-With` or a same-origin `Origin` on every write), the public profiles and board, the store's catalogue, the gallery's pictures, the online counts, the news feed and its pictures. The website's server reaches them over `SERVER_URL`, the launcher and the backup tool on the public domain | `server:8080`, `X-Game-Host` removed |
| path `/leaderboards/*` whose `Accept` does not name `text/html` (the `@server_boards` matcher): the minigame boards and the species boards by season, which the launcher reads. A browser's page load of the same path (`/leaderboards/lanebandit` is also a page of the website) carries `Accept: text/html` and stays the website's, as does `/leaderboards` itself | `server:8080`, `X-Game-Host` removed |
| anything else | `web:8080`, `X-Game-Host` removed |

The server believes `X-Game-Host` only from `trusted_proxies`, so a client cannot choose a game host except through Caddy, and Caddy's address is the only one listed. The server builds its sign-in links as `https://` plus the request's `Host`, which Caddy passes on unchanged, and the website forwards the browser's `Host` in its server-side calls (`SERVER_URL=http://server:8080`, set in compose).

**What the operator puts on the VPS**, in `SERVER_DIR` (default `~/server/`, in `.env`; mounted read-only at `/srv`), never committed:

```
settings.json     from server/Server/settings.example.json, with the real values (below)
install/          the operator's own copy of the game's install folder
recordings/       the operator's recordings (server.md); optional `mission_rules.json` there, the rules of single mission objectives (server.md, "Overridden rules"), read at the server's start
mirror/           the saved static pages (server.md)
variants.json     optional
images/           the slot pictures (images.md), mounted read-only in `web`; the game's avatars `avatar-<id>.png` too (server.md#avatars), and the stock copies `avatars/256/<id>.png`
avatar-uploads/   optional: the saved thumbnails of the players' uploaded avatars, `242/<uid>.jpg` (server.md, `avatar_uploads_dir`); `/srv` already holds it, no mount to add
```

`images/` may be empty: every picture is then its labelled box, and the game's avatars are a miss (a broken picture in the game). The game's avatars are the files `avatar-<id>.png` of the avatar list (the same ones the website draws): the server reads them from `images/` (and, for an id with no such file, the stock copy `images/avatars/256/<id>.png`; the `images_dir` setting, default `images`, next to `settings.json`) at each request, with no restart; `web` reads the folder at start, so restart it after a change. Create it as `deploy` before the first deploy that mounts it, else compose creates it as root and the copy below fails. The pictures are copied from the operator's PC:

```
ssh deploy@<vps> "mkdir -p '<server dir>/images' && find '<server dir>/images' -mindepth 1 -maxdepth 1 -type f -delete"
tar -C <pictures folder> -cf - . | ssh deploy@<vps> "tar -C '<server dir>/images' -xf -"
ssh deploy@<vps> "cd ~/web && docker compose restart web"
```

`settings.json` for this stack, relative paths being relative to the file:

- `web_host`: the website's public domain, `<domain>` (`SITE_DOMAIN`). The server and the website share it: the website forwards the browser's `Host`, and the server's cookies and Steam return links are made for it.
- the other host keys: `game_web_host` (required) and the rest, the game's real host names (the ones the operator's recordings show); a key left out is a placeholder ([server.md](server.md#run-it)).
- `install_dir`: `install`; `data_dir`: `/data` (the volume); `recordings_dir`, `mirror_dir`, `images_dir`, `variants_file` as in the folder above.
- `listeners`: `[{ "address": "0.0.0.0", "port": 8080 }]`: plain HTTP, no `cert`, no `admin` (the admin API is for a person at the machine and is never behind the proxy).
  The admin commands that matter on the VPS are therefore command-line ones, run in the running container, e.g. `cd ~/web && docker compose exec -T server dotnet server.dll admin rescore --account <id> --kill <kill id>` (a dry run; `--apply` writes: take a [backup](#backups) first; [server.md](server.md#admin-api)).
  Not every `--apply` runs in the live container: `admin missions-rollback --apply` ([server.md](server.md#missions)) and `admin mp-harvests --apply` ([server.md](server.md#multiplayer)) must run with the server stopped, after a [backup](#backups): `cd ~/web && docker compose stop server && docker compose run --rm --no-deps server admin missions-rollback --apply; docker compose start server` (the image's entry point is `dotnet server.dll`, so `run` takes the command after it; the server is started again even if the rollback fails). Its dry run, `docker compose exec -T server dotnet server.dll admin missions-rollback`, writes nothing and is fine on the live server. The same for `admin mp-harvests`: first its dry run, `docker compose exec -T server dotnet server.dll admin mp-harvests` (it lists each harvest that would move and each mission objective it would reopen; read it), then, after a backup, `cd ~/web && docker compose stop server && docker compose run --rm --no-deps server admin mp-harvests --apply; docker compose start server`.
- `trusted_proxies`: `["172.30.10.2"]`, Caddy's address; `host_header`: `X-Game-Host`.

`docker compose up -d` after any change of `settings.json` (the server reads it at start). The server image is public like the website's and contains no game files.

## Punch-through server

The `natpt` service of `deploy/compose.yml` is the multiplayer feature's punch-through server: a RakNet `NatPunchthroughServer` on **UDP 10000**, published straight on the host (not behind Caddy: it is UDP). Players' game starts register with it, and it introduces a joiner to a host so both can open their routers without a forwarded port. It has no password and no state. `deploy.yml` builds and pushes its image (`<repo>-natpt`) with the others; `ci` builds it too (`docker-natpt`), so a broken RakNet build fails the pull request.

- **Upgrading a stack without it:** add `NATPT_IMAGE=ghcr.io/<owner>/<repo>-natpt` (lower case) and `NATPT_TAG=latest` to `~/web/.env` before the next `server-X.Y.Z` tag, or `docker compose pull` fails and nothing is rolled out. After the first deploy run, make the package `<repo>-natpt` public like the others (step 2 of [First start](#first-start)); every deploy rewrites the tag like the others.
- **Firewall:** `firewall.sh` and its `DOCKER-USER` rules cover TCP ports 80 and 443 only and leave this port alone. Docker publishes the port before `ufw`'s rules, so `ufw allow 10000/udp` documents the intent but is not what opens it; the provider's firewall (UDP 10000 in), if the VPS has one, has to allow it.
- **The name the game uses:** the server's connect reply gives players `nat_host` of `settings.json`, default `web_host` ([server.md](server.md#run-it)). This stack's `web_host` is behind Cloudflare's proxy, which does not carry UDP, so set `nat_host` here: add a DNS-only (grey cloud) record for the VPS, e.g. `nat.<domain>`, and use it. That record shows the VPS address to anyone who looks it up. The name must resolve; one that does not hangs the game's start.
- **Check:** `docker compose logs natpt` shows `NatPunchthroughServer on UDP 10000`, and the host's game start shows no 30 s wait. The `docker-natpt` job checks that the image builds and the server starts.

## Backups

`server-backup` is a second service of the server's own image: once a day (every 24 hours from its start) it runs `server backup /backups 7`, which makes a consistent copy of every database of `/data` with SQLite's `VACUUM INTO` (not a file copy, [ADR 0001](adr/0001-sqlite-for-profiles.md)) as `<name>-<yyyymmdd>.db` in the volume `serverbackups`, and keeps the newest 7 of each. It is safe while the server runs. A sidecar instead of a host cron line because it needs nothing on the host (no `sqlite3`, no cron entry, no path to the volume), is deployed and rolled back with the server, and uses the server's `SQLitePCL` build, so the copy is made by the same SQLite that wrote the file; the price is that a daily copy on the same disk is no protection against losing the VPS: copy `serverbackups` off the machine now and then (`docker compose cp server-backup:/backups ./offsite`, then `scp`).

Only the files at the top of `/data` are copied. The claims archive is large and made on the operator's PC, so keep it in a folder below: `/data/archive/archive.db`, with `"archive_db": "/data/archive/archive.db"` in the server's `settings.json` ([server.md](server.md#archive-claims)); the operator's PC keeps the copy that counts.

**Restore drill**, to run once after the first deploy and then every few months: check that the newest copy opens and holds data, on a scratch volume, with nothing of production touched.

```
cd ~/web && set -a && . ./.env && set +a
docker volume create drill
docker run --rm --user 0 -v drill:/data -v web_serverbackups:/backups:ro -v "$SERVER_DIR:/srv:ro" --entrypoint sh $SERVER_IMAGE:$SERVER_TAG -c 'cp /backups/state-<yyyymmdd>.db /data/state.db && dotnet server.dll backup /tmp/check 1'
docker volume rm drill
```

The last command of the container opens the restored database and copies all of it; it prints `backup: …` when the file is sound and fails when it is not.

**Restore for real** (data lost or corrupt): stop both services that open the files, put the copy in place, remove the old `-wal` and `-shm` files, start again.

```
cd ~/web && docker compose stop server server-backup
docker compose run --rm --no-deps --entrypoint sh server-backup -c 'cp /backups/state-<yyyymmdd>.db /data/state.db && rm -f /data/state.db-wal /data/state.db-shm'
docker compose up -d
```

Repeat the `cp` for every other database of `/data` that was lost (`ls /backups` shows the names). The players' progress is what the copy held: the hours since it was made are gone.

## Missions audit

Once a day a cron line of the `deploy` user posts the server's `admin missions-audit --discord` and `admin missions-stuck --discord` ([server.md](server.md#missions)) to a Discord webhook: the mission completions of the last 24 hours that look suspicious with the count of each flag, then the objectives players attempt and nobody completes. The webhook's URL is the `MISSIONS_AUDIT_WEBHOOK=<url>` line of `~/web/.env`, which the operator adds by hand (deploys leave the line alone; without it nothing is posted). The script, `~/web/missions-audit.sh`, needs `jq` and `curl` on the host:

```
#!/bin/sh
cd ~/web && url=$(sed -n 's/^MISSIONS_AUDIT_WEBHOOK=//p' .env) && [ -n "$url" ] || exit 0
post() {  # $1: the admin command and its options, $2: the first words of its report
  docker compose exec -T server dotnet server.dll admin $1 --discord | sed -n "/^\*\*$2/,\$p" |
    jq -Rsc 'rtrimstr("\n") | split("\n---8<---\n")[] | {content: ., allowed_mentions: {parse: []}}' |
    while read -r body; do curl -fsS -o /dev/null -H 'Content-Type: application/json' -d "$body" "$url"; sleep 1; done
}
post missions-audit 'missions audit'
post 'missions-stuck --min-candidates 50' 'missions stuck'
```

Each message is one post (`allowed_mentions` empty, so a handle such as `@everyone` pings nobody; one second apart, under the webhook's rate limit); `sed` drops what the server prints before the report (a `skip` line for a recording it cannot read). The crontab line (`crontab -e` as `deploy`), with the output kept for a failed post:

```
30 6 * * * sh ~/web/missions-audit.sh >> ~/web/missions-audit.log 2>&1
```

`sh ~/web/missions-audit.sh` by hand posts it at once. Both reports only read the database, so they run in the live container. The stuck report's own default is 5 candidates, which listed over 130 objectives at first; the script asks for 50 (`--min-candidates 50`), so the daily post lists only the objectives with many attempts and no completion, or one condition that refuses nearly all of them. Lower the number in the script, or run the command by hand with the default, to see more.

## Logs

Every service logs to the host journal (`logging: *logging` in `compose.yml`, Docker's `journald` driver), so a deploy that recreates a container keeps its logs; the default `json-file` driver deletes them with the container. Read them on the VPS, as root or a member of `systemd-journal`:

```
journalctl CONTAINER_NAME=web-server-1 --since -2h        # the server; web-web-1, web-caddy-1, web-server-backup-1 likewise
journalctl CONTAINER_NAME=web-server-1 -g 'upload of account'   # every upload outcome (server.md, "Upload a backup zip")
docker compose logs server                                # the current container only
```

The lines hold client addresses and handles, so the journal keeps them at most 30 days and 5 GB in all (the drop-in of [VPS prerequisites](#vps-prerequisites)); `journalctl --disk-usage` shows the size, `journalctl | head -1` the oldest line. An entry takes about 1 KB on disk and the server writes one per request (about 15,000 an hour in November 2026, most of them a crawler on `/hunters`), so 5 GB holds about two weeks; the size cap ends it before the 30 days. The website logs only warnings and errors of the framework's request, routing, static-file and HTTP-client categories (`src/Web/Program.cs`): at Info level they wrote about six lines per request, ten times the server's, and filled 2 GB in 10 hours. Caddy writes no access log.

## Adding another site

- **Another Compose project on the same VPS:** its public service joins the external network `edge` with a unique alias, and it gets a site file in `~/web/sites/`, for example `sites/other.caddy`:

```
<other-domain> {
	tls /certs/<other-domain>.pem /certs/<other-domain>.key
	reverse_proxy other-web:3000
}
```

Its certificate files go in the same `certs/` folder, named per site. Then `docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile`. An empty `sites/` folder is fine.

## Local test

Needs Docker with the Compose plugin; ports 80 and 443 (TCP) and 10000 (UDP) free.

```
docker network create edge
docker compose -f deploy/compose.yml -f deploy/compose.dev.yml up --build -d --wait
```

The override builds the images as `web:dev`, `server:dev` and `natpt:dev` and serves `https://localhost` with Caddy's own local CA, which nothing trusts (`curl -k`, or accept the warning in the browser). The stack starts `server` too, so `SERVER_DIR` (an environment variable or `deploy/.env`, `SITE_DOMAIN=localhost` likewise) must name a folder with a `settings.json` like the one above, a `trusted_proxies` entry of `172.30.10.2` included; `Caddyfile.dev` routes everything to `web`:

```
curl -ksf https://localhost/feed | grep "Work in progress"
curl -ks -X POST "https://localhost/_blazor/negotiate?negotiateVersion=1"    # WebSockets among the transports
curl -sS https://localhost/ -o /dev/null                                     # fails: certificate not trusted
```

In the browser's network tab a WebSocket to `/_blazor` (status 101) stays open while you move between pages. Stop with `docker compose -f deploy/compose.yml -f deploy/compose.dev.yml down`.

Do not make your browser trust Caddy's local CA: the app's HSTS header would then force `localhost` to https for a year, and `dotnet run` on `http://localhost:5080` would stop loading in that browser.

## Discord threads at release

`.github/workflows/discord-release-threads.yml` runs after the `release` workflow succeeds (the release is published by then) and tells the Discord posts that the fix or idea has shipped, through Discord's REST API (the bot does not have to be online). It follows the release run (`workflow_run`) because a release published with the workflow's own `GITHUB_TOKEN` does not fire `release: published`. Closing or reopening an issue touches nothing on Discord: a fix is not available to players until it is released. The Discord server has two forum channels, where each post is a thread: `bug-reports` (tags `Open`, `In progress`, `Resolved`, `Won't fix`) and `ideas` (tags `Idea`, `Planned`, `Done`, `Not planned`).

**Maintainer step:** put the Discord thread link (`https://discord.com/channels/<guild id>/<thread id>`, "Copy link" on the post) in the **pull request's body**, or in the body of the issue the pull request closes (`Closes #<n>`). A body can hold several links. The thread is resolved when the release that ships the pull request is published. `Won't fix` and `Not planned` are set by hand in Discord.

The script (`.github/scripts/discord-release-threads.mjs`, Node 20+, no packages) takes the commits from the previous stable release to the new tag (GitHub's compare), each commit's pull requests merged into the default branch, and the issues each of those closes. It reads the Discord links in the bodies of those pull requests and issues, but only when the pull request or issue was opened by the owner, a member or a collaborator of the repository: anyone can edit their own body, also after a merge, so a link in a stranger's pull request or issue (a Dependabot body quotes third parties' text) cannot make the bot post in other people's threads. A pull request from a stranger can still resolve a thread through the maintainer's issue it closes.

The links are deduplicated. For each one the script checks that the server is the secret's one and that the channel is a thread of a forum named exactly `bug-reports` or `ideas`; any other link (another server, `#announcements`, a normal channel) is logged and skipped. Then, unless the thread already has the tag (a re-run posts nothing twice):

- it posts one short English message, "✅ Fixed in vX.Y.Z: <release link>. The launcher updates itself." (`ideas`: "✅ Done in vX.Y.Z: ..."), and mentions nobody;
- it sets `Resolved` (`bug-reports`) or `Done` (`ideas`), found by name in the forum's tags, in place of the thread's other status tags of that forum (`In progress`, `Planned`, ...); the thread's other tags stay;
- it archives the thread, not locked: a reply from the reporter unarchives it.

A prerelease (`vX.Y.Z-rc.N`) resolves nothing, and the next stable release's range includes its commits. The run fails only on a real API failure (a missing thread, a missing tag, no permission), not on a missing or skipped link. The tag comes from the run and is checked against `vMAJOR.MINOR.PATCH`; nothing of a pull request or issue reaches a shell line. The workflow has `contents`, `pull-requests` and `issues` read permissions, and the bot needs, on both forums, the permissions to view them, send messages in posts and manage threads (tags and archiving). Repository secrets:

| Secret | Value |
|---|---|
| `DISCORD_BOT_TOKEN` | the bot's token |
| `DISCORD_GUILD_ID` | the Discord server's id |

Without them (a fork) the run ends with a notice. To run it again for a release (or to catch up after the secrets were added), start the workflow by hand (Actions, discord-release-threads, Run workflow) with the tag, and optionally `from`, the tag where the range starts. The release is the launcher's `vX.Y.Z`: a change of the server or the website reaches players only with the next `server-X.Y.Z` tag ([Deploys](#deploys)), so the reply is sent when a release ships, which can be before that deploy.

## Known limits

What the live site (the website with `SERVER_URL`) does not do yet, and why. [website.md](website.md#sign-in) lists what is the server's; the banner (`Wip.Banner`) says that some features of the original are still missing.

- **Profiles** are the server's for every player of the server: the header (handle, membership, hunterscore, rank, avatar) and the website's eight tabs (ranks, achievements, skills, statistics, gallery, trophies, dogs, friends), each read from `GET /hunters/<handle>/<area>`; no tab is hidden. What the tabs leave out: the achievements tab has its overview only (no category pages, no challenges); the statistics tab has no "last hunt" pill (the first row of its history is that hunt) and no season list, and its collectables, like a hunt's collectables, achievements and shed antlers, are empty; the score sheet of a hunt restored from a backup opens only when the zip held its page. A handle the server does not know is "Page not found".
- **Friends** are the server's: the status bar's list and the request buttons work for a signed-in player. The server keeps no time for a request, so the requests come by handle, not oldest first; the lists have no "Send message" button, as messages cannot be sent.
- **Messages** are the player's own, read only: the server's notices and the conversations a backup restored ([server.md](server.md#messages)).
- **Missions** are the player's own ([server.md](server.md#missions)), the daily ones included. The server has no item to resume a broken daily streak, so the page offers none, and a restored streak starts again at day 1 (a backup holds no length).
- **Hidden for a signed-in player** (no source on the server): the minigame boards show no "your rank" (the hunterscore board does).
- **Not offered** (the address goes to the feed, the hunterscore board or `/help`):
  - the competitions and the community league, and the antler boards: the server has no competitions or antler measurements ([#222](https://github.com/yinyin99/hunter-resurgence/issues/222)); the season boards (species score and range, hall of fame) are the server's ([server.md](server.md#leaderboards-and-seasons));
  - help's guides, video tutorials and community sites tabs: their content is the mock's; help keeps its support tab and its daily missions tab ([#225](https://github.com/yinyin99/hunter-resurgence/issues/225));
  - bank, invite friends, welcome and new message: the website reads no such data from the server yet, and the bank would take real money. The settings page has its avatar tab only. The wallet shows the player's own history only (restored and made on the server), without a bank tab.
- **The store** is the catalogue of the server's recording, frozen on the day it was made: a new price or item means a new recording. A sale shows only when the recording holds one. Without a recording that matches the settings' host names, the server falls back to the install's items, with no categories or bundles ([server.md](server.md#the-store-catalogue)).
  - The pictures are labelled boxes until they are mapped ([#224](https://github.com/yinyin99/hunter-resurgence/issues/224)).
  - The price a page shows can be up to 5 minutes old (the website's copy); the server charges today's.
- **The news** is the server's ([server.md](server.md#news)): the mock website shows the project's three first posts in English; the live site shows what `admin news` published, the first three posts included (the server's migration inserts them with their translations). The three first posts keep the slide pictures they had (`competition-hero-170039`, `competition-hero-170031`, `competition-hero-170026`); a post without a picture shows `news-default`.
- **The Lane Bandit and Bust Through boards** list each account's best: the higher of the score restored from a backup and the best the server's hunts reported (heartbeat event 17). Each page view reads every account.
- **A red `ci` on `main` blocks a deploy tag of that commit** (the deploy needs the green `ci` of the tagged commit's push). A flaky test there holds the deploy back until `ci` is run again or the test is fixed.
- **Merged is not live**: what is on `main` reaches players only with the next `server-X.Y.Z` tag ([Deploys](#deploys)).
