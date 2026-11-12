# Deploy the website and the community server

## What this is

The [website](website.md) (`src/Web/`) and the community server ([server.md](server.md), `server/Server/`) in containers on one Ubuntu 24.04 VPS, behind Caddy and Cloudflare. Without `SERVER_URL` the website is the **mock** website: its pages show fixed, fictional data. With it (as in `compose.yml`) the sign-in, the store and buying, the news, the profiles, the hunterscore and minigame boards, the wallet, the missions, the friends, the messages and the restore page are the community server's, and the pages without a source are hidden ([website.md](website.md#sign-in), [Known limits](#known-limits)). A banner at the bottom of every page (`Wip.Banner` in `src/Ui/Strings*.resx`) says that some pages still show sample content; change or remove it when that is no longer so.

| File | Purpose |
|---|---|
| `src/Web/Dockerfile` | the image (build context: the repository root). It listens on port 8080 as a non-root user. Its SDK tag is the version of `global.json` and its `aspnet` tag the runtime that SDK ships: a new SDK means `global.json`, both tags and every `packages.lock.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate` with that SDK) in one change, or the locked restore fails with NU1004 |
| `deploy/compose.yml` | production stack: `caddy` (ports 80 and 443) in front of `web` and `server`, plus `server-backup` ([Backups](#backups)). `server` and `server-backup` are on `internal` only (`server-backup` on no network). The named volume `dataprotection` keeps the website's cookie-signing keys (`/home/app/.aspnet/DataProtection-Keys`) across deploys, so cookies stay valid; the Dockerfile gives that folder to the app user, and a new named volume takes its owner. `serverdata` holds the server's databases at `/data`, `serverbackups` the copies. The Caddy image is pinned to a minor tag (`caddy:2.11`); Dependabot's `docker-compose` entry for `/deploy` updates it weekly |
| `deploy/Caddyfile` | the site `{$SITE_DOMAIN}` with the Cloudflare origin certificate: game requests and the server's own paths (`/auth/*`, `/account/*`, `/hunters`, `/hunters/*`, `/catalog/*`, `/photos/*`, the minigame boards under `/leaderboards/*`) to `server`, the rest to `web` ([routing](#the-community-server)); then `import sites/*.caddy` |
| `deploy/.env.example` | the values of `.env` |
| `server/Server/Dockerfile` | the community server's image, like the website's (same SDK and runtime tags, bumped together; build context: the repository root): non-root, listens on plain HTTP on port 8080 (the proxy's side), `SERVER_SETTINGS=/srv/settings.json`, and creates `/data` and `/backups` owned by the app user, because an empty named volume takes the owner of the folder it is mounted over |
| `deploy/firewall.sh` | ports 80 and 443 for Cloudflare only |
| `deploy/compose.dev.yml`, `deploy/Caddyfile.dev` | local test on `https://localhost` |
| `.github/workflows/ci.yml` | the `build` job also runs `tools\backup\publish.cmd` with `tools/backup/settings.example.json` and checks that the release zip holds exactly `backup.exe` and `settings.json` (the release's publish step, without a tag); for `src\Launcher\publish.cmd` it installs `vpk`, checks that the publish folder holds the exe, `settings.json` and `wwwroot` and that the release folder holds the Setup, the full package and `releases.win.json`, then runs the Setup silently and checks the installed exe and the link scheme, and uninstalls it and checks that the link scheme is gone |
| `.github/workflows/ci.yml` | the `server-ubuntu` job runs the server's tests on Ubuntu ([server.md](server.md#tests)); the Windows run of them is part of the `build` job |
| `.github/workflows/ci.yml` | the `docker` job builds the image, starts it and checks that `/feed` shows the banner and names the fingerprinted `shell.<hash>.js` (the import map, see [website.md](website.md#build-and-run)), that `/settings/language` lists a time zone (`Europe/Paris`), which fails when `tzdata` is missing, that `/robots.txt` is served ([website.md](website.md#build-and-run)), and that `/feed` and a 404 carry the security headers ([website.md](website.md#security-headers)) |
| `.github/workflows/ci.yml` | the `scripts` job runs actionlint on the workflows, `docker compose config` on `deploy/compose.yml` with `deploy/.env.example`, shellcheck on `deploy/firewall.sh`, and the firewall script against stub `iptables` and `curl` (no ranges: it fails and adds no rule; two ranges: the `RETURN`, `DROP` and `DOCKER-USER` rules); the `docker` job also checks that the app user can write `/home/app/.aspnet/DataProtection-Keys` |
| `.github/workflows/backup-live.yml` | every day and by hand, never for a pull request, not a required check: runs `LiveTests` ([backup.md](backup.md#tests)) against the official servers with a `settings.json` written from the release's secrets (`WEB_HOST`, `PHOTO_HOST`, `STATIC_HOST`, `AVATAR_HOST`, `OAUTH_CONSUMER_KEY`, `CLIENT_EXE`); opens or updates an issue labelled `backup-live` when it fails; a server outage skips the test |
| `.github/workflows/deploy.yml` | build, push and roll out after a green `ci` on `main` |
| `.github/workflows/dependabot-locks.yml` | adds the regenerated `packages.lock.json` files to a Dependabot SDK or NuGet pull request |
| `.github/actions/setup-sdk/action.yml` | installs exactly the SDK of `global.json` for `ci`, `release` and `dependabot-locks`: it reads `sdk.version`, turns `rollForward` off in the runner's copy of `global.json` (never committed), runs `setup-dotnet` with that version and prints `dotnet --version` |

**SDK and NuGet updates.** Dependabot opens one weekly pull request, the `dotnet-sdk` multi-ecosystem group in `.github/dependabot.yml`, when a new SDK patch or feature band is out. It moves `global.json` and the `dotnet/sdk` and `dotnet/aspnet` tags of `src/Web/Dockerfile` together; a new .NET major is ignored, because it is a migration. A NuGet pull request (the `nuget` entry of `.github/dependabot.yml`) changes the bumped project's `.csproj` and `packages.lock.json`, but not the lock files of the projects that reference it (`server/Server.Tests` references `server/Server`; `tests/Resurgence.Tests` references most of `src` and `tools`). `dependabot-locks.yml` runs on every Dependabot pull request that changes `global.json`, a Dockerfile above, a `.csproj` or a `packages.lock.json`; it skips pull requests by anyone else. It restores with exactly the SDK of `global.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate`; the action turns `rollForward` off for the rest of the job, or the newest 10.0 SDK on the runner would write the lock files), commits any changed lock files to the pull request's branch as "build: regenerate lock files", and starts `ci` for that commit by `workflow_dispatch`, since a push made with `GITHUB_TOKEN` starts no workflow. That run's `build`, `server-ubuntu`, `docker` and `docker-server` are on the commit (Actions, `ci`, event `workflow_dispatch`), not in the pull request's check list. The `pull_request` run of `ci` on the bot's commit is held for approval ("action required") until the pull request closes; the API then lists it as failed with no jobs. So the ruleset `main: reviewed changes` does not see its required check `build` on the pull request, and the pull request stays blocked with green checks (#124 and #117: the ruleset's records of those merges show only `required_status_checks` failing, "Required status check "build" is expected."). When the dispatched run has passed, the maintainer merges with `gh pr merge <n> --squash --admin --delete-branch`. A Dependabot pull request that got no lock commit (its lock files were already right) merges with `gh pr merge <n> --squash --delete-branch`. For an SDK update, check that the `aspnet` tag is the runtime that the new SDK ships: Dependabot bumps each tag to its own latest version. If the lock-file job fails, or `build` or `docker` fails with NU1004: start the job again with Actions, `dependabot-locks`, Run workflow, on the pull request's branch, or run the restore with that SDK yourself and push the lock files. After the bot's lock-file commit Dependabot no longer updates the pull request (no rebase, no newer version): comment `@dependabot recreate` to start over from `main`, and the job runs again on the new commit.

**Locked restore.** The `build` and `server-ubuntu` jobs of `ci.yml` and the job of `release.yml` set `RestoreLockedMode: "true"` (an environment variable, which MSBuild reads as the property), so every restore in the job, including the implicit ones of `dotnet build`, `dotnet test` and `publish.cmd`, uses the committed `packages.lock.json` files as they are and fails with NU1004 when one does not match its project. All three build with exactly the SDK of `global.json` (the action above), so the SDK of a release is the one that was reviewed. `global.json` keeps `rollForward: latestFeature` for local builds, which restore unlocked. A pull request that changes a `PackageReference` (or the SDK) without its lock files therefore fails `build` and `server-ubuntu`; regenerate them with `dotnet restore hunter-resurgence.slnx --force-evaluate` using exactly the SDK of `global.json` (a newer local SDK writes its own version of the packages the SDK adds itself, such as `Microsoft.NET.ILLink.Tasks`, so do not commit lock files it rewrote). On a Dependabot SDK or NuGet pull request `build` and `server-ubuntu` fail until `dependabot-locks.yml` has pushed the lock files and started `ci` again.

## VPS prerequisites

- Ubuntu 24.04, Docker Engine and the Compose plugin from [Docker's apt repository](https://docs.docker.com/engine/install/ubuntu/).
- A non-root user for deploys (`deploy` below) in the `docker` group, with the public half of the deploy key in `~/.ssh/authorized_keys`. Never root.
- The shared network, once: `docker network create edge`.
- The journal's retention, once, as root ([Logs](#logs)):

```
sudo mkdir -p /etc/systemd/journald.conf.d
printf '[Journal]\nStorage=persistent\nMaxRetentionSec=30day\nSystemMaxUse=2G\n' | sudo tee /etc/systemd/journald.conf.d/retention.conf
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
SERVER_DIR=/home/deploy/server
```

`WEB_IMAGE` and `SERVER_IMAGE` are in lower case. Every deploy rewrites the `WEB_TAG` and `SERVER_TAG` lines with the commit sha it rolled out, so the lines have to exist. Every deploy also replaces the `LINK_BACKUP=<url>`, `LINK_SOURCE=<url>`, `GAME_NAME="<name>"` and `GAME_FULL="<name>"` lines with the repository variables of the same names (no line for an unset variable; `LINK_SOURCE` defaults to the repository the deploy runs from); compose passes them to `web`.

The website's other settings are environment variables of the `web` service: `FORWARDED_TRUSTED`, `GAME_FULL`, `GAME_NAME`, `IMAGES_DIR`, `LINK_BACKUP`, `LINK_SOURCE` and `SERVER_URL`. `IMAGES_DIR` is `/images`, where compose mounts `SERVER_DIR/images` read-only: the slot pictures ([images.md](images.md)). `SERVER_URL` is the community server's address on the `internal` network (for example `http://server:8080`). With it the website signs players in through the server (see [website.md](website.md#sign-in)); without it the website keeps its mock session. The server's `web_host` setting must be the website's domain, and Caddy has to send the server's paths (`/auth/*` first, the others in [the routing table](#the-community-server)) to the server for the sign-in links and the launcher to work.

`settings.json` (the other links, see [launcher.md](launcher.md#settingsjson)) is optional. The game name and the backup tool link come from the `GAME_NAME`, `GAME_FULL` and `LINK_BACKUP` variables, which win over `game_name`, `game_full` and `link_backup` of the file. To use one, put it in `~/web/` and mount it in `compose.override.yml`, which Compose reads next to `compose.yml` and deploys never overwrite:

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
2. Make the images public once they exist (after the first deploy run): GitHub, the packages `<repo>-web` and `<repo>-server` of `<owner>`, Package settings, Change visibility, Public. The repository is public and the images hold no secrets, so the VPS pulls without a login. (If they must stay private: `docker login ghcr.io` on the VPS with a token that has `read:packages` only.)
3. `cd ~/web && docker compose up -d`, then `docker compose ps`: `web` and `server` healthy, `caddy` and `server-backup` running.
4. Open `https://<domain>/` through Cloudflare.

## Deploys

`.github/workflows/deploy.yml` runs when the `ci` workflow succeeds for a push to `main` (never for pull requests, so forks get no secrets). Deploys run one at a time. When a deploy starts, if its commit is no longer the head of `main`, it ends with a notice and rolls nothing out: the newer commit's deploy does. Two pushes close together would otherwise deploy in the order their `ci` runs end, and the older commit could go out last. A re-run of an older deploy is skipped the same way: roll back with a revert on `main`, or on the host ([Rollback](#rollback)). Otherwise the deploy:

1. builds the website and server images from that commit and pushes `ghcr.io/<owner>/<repo>-web:<sha>` and `-server:<sha>`, each also as `:latest`;
2. copies `compose.yml` and `Caddyfile` to `~/web/` over SSH, writes `WEB_TAG=<sha>`, `SERVER_TAG=<sha>`, `LINK_BACKUP`, `LINK_SOURCE`, `GAME_NAME` and `GAME_FULL` into `.env`, then `docker compose pull && docker compose up -d`, reloads Caddy and removes dangling images. Compose stops the old `server` before it starts the new one, so two never write the database at once.

Repository secrets, separate from the release secrets in [CONTRIBUTING.md](../CONTRIBUTING.md#releases):

| Secret | Value |
|---|---|
| `DEPLOY_HOST` | the VPS address |
| `DEPLOY_USER` | the deploy user |
| `DEPLOY_KEY` | the private half of that user's SSH key (no passphrase) |

Without them (a fork, or no VPS yet) the run ends with a notice and deploys nothing. The host key is taken with `ssh-keyscan` at every run (trust on first use); the stricter option is a fourth secret with the pinned `known_hosts` line.

Repository variables (Settings, Secrets and variables, Actions, Variables):

| Variable | Value | Unset |
|---|---|---|
| `LINK_BACKUP` | the backup tool link, `https://github.com/<owner>/<repo>/releases/latest/download/backup-win-x64.zip` | `link_backup` of `settings.json`, if any, else no backup tool button |
| `LINK_SOURCE` | the project's repository, linked by the feed's development post | the repository the deploy runs from (`https://github.com/<owner>/<repo>`) |
| `GAME_NAME` | the game's short name, for `{game}` in the texts | `game_name` of `settings.json`, else "the game" |
| `GAME_FULL` | the name with its edition, for `{game_full}` (the meta tags) | `game_full` of `settings.json`, else the short name |

An unset variable removes its line. A `LINK_BACKUP` or `LINK_SOURCE` that is not a plain `https://` URL (no spaces, `'`, `$` or `#`), or a name with anything but letters, digits, spaces and `. : -`, fails the deploy before the rollout, after the image is pushed. The deploy also ends `.env` with a newline before it appends the lines.

## Rollback

The running versions are the `WEB_TAG` and `SERVER_TAG` lines of `~/web/.env`. Every pushed sha stays in GHCR; find an older one in the list of `deploy` runs on GitHub, on the package page, or with `docker images` on the VPS. Then:

```
ssh deploy@<vps> "cd web && sed -i -e 's/^WEB_TAG=.*/WEB_TAG=<older sha>/' -e 's/^SERVER_TAG=.*/SERVER_TAG=<older sha>/' .env && docker compose up -d"
```

The next deploy sets the tag again.

## The community server

**Routing** (`deploy/Caddyfile`, one domain `<domain>`, in this order):

| Request | Goes to |
|---|---|
| carries the header `X-Game-Host` (every request of a player's local forwarder, for every game host name, the signup host included) | `server:8080`, header kept |
| path `/auth/*`, `/account/*`, `/hunters`, `/hunters/*`, `/catalog/*` or `/photos/*` (the `@server_paths` matcher, the one place to change the server's website-facing paths): sign-in, the player's own data (cookie and `X-Requested-With` or a same-origin `Origin` on every write), the public profiles and board, the store's catalogue, the gallery's pictures. The website's server reaches them over `SERVER_URL`, the launcher and the backup tool on the public domain | `server:8080`, `X-Game-Host` removed |
| path `/leaderboards/*` whose `Accept` does not name `text/html` (the `@server_boards` matcher): the minigame boards, which the launcher reads. A browser's page load of the same path (`/leaderboards/lanebandit` is also a page of the website) carries `Accept: text/html` and stays the website's, as does `/leaderboards` itself | `server:8080`, `X-Game-Host` removed |
| anything else | `web:8080`, `X-Game-Host` removed |

The server believes `X-Game-Host` only from `trusted_proxies`, so a client cannot choose a game host except through Caddy, and Caddy's address is the only one listed. The server builds its sign-in links as `https://` plus the request's `Host`, which Caddy passes on unchanged, and the website forwards the browser's `Host` in its server-side calls (`SERVER_URL=http://server:8080`, set in compose).

**What the operator puts on the VPS**, in `SERVER_DIR` (default `~/server/`, in `.env`; mounted read-only at `/srv`), never committed:

```
settings.json     from server/Server/settings.example.json, with the real values (below)
install/          the operator's own copy of the game's install folder
recordings/       the operator's recordings (server.md)
mirror/           the saved static pages (server.md)
variants.json     optional
images/           the slot pictures (images.md), mounted read-only in `web`
```

`images/` may be empty: every picture is then its labelled box. Create it as `deploy` before the first deploy that mounts it, else compose creates it as root and the copy below fails. The pictures are copied from the operator's PC; `web` reads the folder at start, so restart it after a change:

```
ssh deploy@<vps> "mkdir -p '<server dir>/images' && rm -f '<server dir>/images'/*"
tar -C <pictures folder> -cf - . | ssh deploy@<vps> "tar -C '<server dir>/images' -xf -"
ssh deploy@<vps> "cd ~/web && docker compose restart web"
```

`settings.json` for this stack, relative paths being relative to the file:

- `web_host`: the website's public domain, `<domain>` (`SITE_DOMAIN`). The server and the website share it: the website forwards the browser's `Host`, and the server's cookies and Steam return links are made for it.
- the other 12 host keys: the game's real host names (the ones the operator's recordings show).
- `install_dir`: `install`; `data_dir`: `/data` (the volume); `recordings_dir`, `mirror_dir`, `variants_file` as in the folder above.
- `listeners`: `[{ "address": "0.0.0.0", "port": 8080 }]`: plain HTTP, no `cert`, no `admin` (the admin API is for a person at the machine and is never behind the proxy).
- `trusted_proxies`: `["172.30.10.2"]`, Caddy's address; `host_header`: `X-Game-Host`.

`docker compose up -d` after any change of `settings.json` (the server reads it at start). The server image is public like the website's and contains no game files.

## Backups

`server-backup` is a second service of the server's own image: once a day (every 24 hours from its start) it runs `server backup /backups 7`, which makes a consistent copy of every database of `/data` with SQLite's `VACUUM INTO` (not a file copy, [ADR 0001](adr/0001-sqlite-for-profiles.md)) as `<name>-<yyyymmdd>.db` in the volume `serverbackups`, and keeps the newest 7 of each. It is safe while the server runs. A sidecar instead of a host cron line because it needs nothing on the host (no `sqlite3`, no cron entry, no path to the volume), is deployed and rolled back with the server, and uses the server's `SQLitePCL` build, so the copy is made by the same SQLite that wrote the file; the price is that a daily copy on the same disk is no protection against losing the VPS: copy `serverbackups` off the machine now and then (`docker compose cp server-backup:/backups ./offsite`, then `scp`).

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

## Logs

Every service logs to the host journal (`logging: *logging` in `compose.yml`, Docker's `journald` driver), so a deploy that recreates a container keeps its logs; the default `json-file` driver deletes them with the container. Read them on the VPS, as root or a member of `systemd-journal`:

```
journalctl CONTAINER_NAME=web-server-1 --since -2h        # the server; web-web-1, web-caddy-1, web-server-backup-1 likewise
journalctl CONTAINER_NAME=web-server-1 -g 'upload of account'   # every upload outcome (server.md, "Upload a backup zip")
docker compose logs server                                # the current container only
```

The lines hold client addresses and handles, so the journal keeps them at most 30 days and 2 GB in all (the drop-in of [VPS prerequisites](#vps-prerequisites)); `journalctl --disk-usage` shows the size. Caddy writes no access log.

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

Needs Docker with the Compose plugin; ports 80 and 443 free.

```
docker network create edge
docker compose -f deploy/compose.yml -f deploy/compose.dev.yml up --build -d --wait
```

The override builds the images as `web:dev` and `server:dev` and serves `https://localhost` with Caddy's own local CA, which nothing trusts (`curl -k`, or accept the warning in the browser). The stack starts `server` too, so `SERVER_DIR` (an environment variable or `deploy/.env`, `SITE_DOMAIN=localhost` likewise) must name a folder with a `settings.json` like the one above, a `trusted_proxies` entry of `172.30.10.2` included; `Caddyfile.dev` routes everything to `web`:

```
curl -ksf https://localhost/feed | grep "Work in progress"
curl -ks -X POST "https://localhost/_blazor/negotiate?negotiateVersion=1"    # WebSockets among the transports
curl -sS https://localhost/ -o /dev/null                                     # fails: certificate not trusted
```

In the browser's network tab a WebSocket to `/_blazor` (status 101) stays open while you move between pages. Stop with `docker compose -f deploy/compose.yml -f deploy/compose.dev.yml down`.

Do not make your browser trust Caddy's local CA: the app's HSTS header would then force `localhost` to https for a year, and `dotnet run` on `http://localhost:5080` would stop loading in that browser.

## Known limits

What the live site (the website with `SERVER_URL`) does not do yet, and why. [website.md](website.md#sign-in) lists what is the server's; the banner (`Wip.Banner`) says that some pages still show sample content.

- **Profiles** are the server's for every player of the server: the header (handle, membership, hunterscore, rank, avatar) and the website's eight tabs (ranks, achievements, skills, statistics, gallery, trophies, dogs, friends), each read from `GET /hunters/<handle>/<area>`; no tab is hidden. What the tabs leave out: the achievements tab has its overview only (no category pages, no challenges); the statistics tab has no "last hunt" pill (the first row of its history is that hunt) and no season list, and its collectables, like a hunt's collectables, achievements and shed antlers, are empty; the score sheet of a hunt restored from a backup opens only when the zip held its page. A handle the server does not know is "Page not found".
- **Friends** are the server's: the status bar's list and the request buttons work for a signed-in player. The server keeps no time for a request, so the requests come by handle, not oldest first; the lists have no "Send message" button, as messages cannot be sent.
- **Messages** are the player's own, read only: the server's notices and the conversations a backup restored ([server.md](server.md#messages)).
- **Missions** are the player's own ([server.md](server.md#missions)), the daily ones included. The server has no item to resume a broken daily streak, so the page offers none, and a restored streak starts again at day 1 (a backup holds no length); the daily missions page shows no pictures of the givers.
- **Hidden for a signed-in player** (no source on the server): the store has no "you own" lines, and the minigame boards show no "your rank".
- **Not offered** (the address goes to the feed, the hunterscore board or `/help`):
  - the competitions and the community league, the season boards (species score and range, hall of fame) and the antler boards: the server has no seasons, competitions or antler measurements ([#222](https://github.com/yinyin99/hunter-resurgence/issues/222));
  - help's guides, video tutorials and community sites tabs: their content is the mock's; help keeps its support tab and its daily missions tab ([#225](https://github.com/yinyin99/hunter-resurgence/issues/225));
  - bank, settings, game management, invite friends, welcome and new message: the website reads no such data from the server yet, and the bank would take real money. The wallet shows the player's own history only (restored and made on the server), without a bank tab.
- **The store** is the catalogue of the server's recording, frozen on the day it was made: a new price or item means a new recording. A sale shows only when the recording holds one. Without a recording that matches the settings' host names, the server falls back to the install's items, with no categories or bundles ([server.md](server.md#the-store-catalogue)).
  - The pictures are labelled boxes until they are mapped ([#224](https://github.com/yinyin99/hunter-resurgence/issues/224)).
  - The price a page shows can be up to 5 minutes old (the website's copy); the server charges today's.
- **The news** is the project's two posts (the backup tool and the project in development), the same as on the mock website. The posts' slides show two of the operator's competition pictures (`competition-hero-170031`, `competition-hero-170026`).
- **The Lane Bandit and Bust Through boards** list each account's best: the higher of the score restored from a backup and the best the server's hunts reported (heartbeat event 17). Each page view reads every account.
- **A red `ci` on `main` skips the deploy** (the deploy runs only after a green one), and an older green commit still queued skips too, since it is no longer `main`'s head ([Deploys](#deploys)). A flaky test there holds the live site back until `ci` is run again or the test is fixed ([#219](https://github.com/yinyin99/hunter-resurgence/issues/219) is flaky locally).
