# Deploy the website and the community server

## What this is

The [website](website.md) (`src/Web/`) and the community server ([server.md](server.md), `server/Server/`) in containers on one Ubuntu 24.04 VPS, behind Caddy and Cloudflare. The website is the **mock** website: every page shows fixed, fictional data. A banner at the bottom of every page (`Wip.Banner` in `src/Ui/Strings*.resx`) says so; change or remove it when real features arrive.

| File | Purpose |
|---|---|
| `src/Web/Dockerfile` | the image (build context: the repository root). It listens on port 8080 as a non-root user. Its SDK tag is the version of `global.json` and its `aspnet` tag the runtime that SDK ships: a new SDK means `global.json`, both tags and every `packages.lock.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate` with that SDK) in one change, or the locked restore fails with NU1004 |
| `deploy/compose.yml` | production stack: `caddy` (ports 80 and 443) in front of `web` and `server`, plus `server-backup` ([Backups](#backups)). `server` and `server-backup` are on `internal` only (`server-backup` on no network). The named volume `dataprotection` keeps the website's cookie-signing keys (`/home/app/.aspnet/DataProtection-Keys`) across deploys, so cookies stay valid; the Dockerfile gives that folder to the app user, and a new named volume takes its owner. `serverdata` holds the server's databases at `/data`, `serverbackups` the copies. The Caddy image is pinned to a minor tag (`caddy:2.11`); Dependabot's `docker-compose` entry for `/deploy` updates it weekly |
| `deploy/Caddyfile` | the site `{$SITE_DOMAIN}` with the Cloudflare origin certificate: game requests and `/auth/*` to `server`, the rest to `web` ([routing](#the-community-server)); then `import sites/*.caddy` |
| `deploy/.env.example` | the values of `.env` |
| `server/Server/Dockerfile` | the community server's image, like the website's (same SDK and runtime tags, bumped together; build context: the repository root): non-root, listens on plain HTTP on port 8080 (the proxy's side), `SERVER_SETTINGS=/srv/settings.json`, and creates `/data` and `/backups` owned by the app user, because an empty named volume takes the owner of the folder it is mounted over |
| `deploy/firewall.sh` | ports 80 and 443 for Cloudflare only |
| `deploy/compose.dev.yml`, `deploy/Caddyfile.dev` | local test on `https://localhost` |
| `.github/workflows/ci.yml` | the `build` job also runs `tools\backup\publish.cmd` with `tools/backup/settings.example.json` and checks that the release zip holds exactly `backup.exe` and `settings.json` (the release's publish step, without a tag); the same for `tools\play\publish.cmd` and the player kit's zip (`play.exe`, `settings.json`, three `.cmd` files) |
| `.github/workflows/ci.yml` | the `server-ubuntu` job runs the server's tests on Ubuntu ([server.md](server.md#tests)); the Windows run of them is part of the `build` job |
| `.github/workflows/ci.yml` | the `docker` job builds the image, starts it and checks that `/feed` shows the banner and names the fingerprinted `shell.<hash>.js` (the import map, see [website.md](website.md#build-and-run)), that `/settings/language` lists a time zone (`Europe/Paris`), which fails when `tzdata` is missing, that `/robots.txt` is served ([website.md](website.md#build-and-run)), and that `/feed` and a 404 carry the security headers ([website.md](website.md#security-headers)) |
| `.github/workflows/deploy.yml` | build, push and roll out after a green `ci` on `main` |
| `.github/workflows/dependabot-locks.yml` | adds the regenerated `packages.lock.json` files to a Dependabot SDK or NuGet pull request |
| `.github/actions/setup-sdk/action.yml` | installs exactly the SDK of `global.json` for `ci`, `release` and `dependabot-locks`: it reads `sdk.version`, turns `rollForward` off in the runner's copy of `global.json` (never committed), runs `setup-dotnet` with that version and prints `dotnet --version` |

**SDK and NuGet updates.** Dependabot opens one weekly pull request, the `dotnet-sdk` multi-ecosystem group in `.github/dependabot.yml`, when a new SDK patch or feature band is out. It moves `global.json` and the `dotnet/sdk` and `dotnet/aspnet` tags of `src/Web/Dockerfile` together; a new .NET major is ignored, because it is a migration. A NuGet pull request (the `nuget` entry of `.github/dependabot.yml`) changes the bumped project's `.csproj` and `packages.lock.json`, but not the lock files of the projects that reference it (`server/Server.Tests` references `server/Server`; `tests/Resurgence.Tests` references most of `src` and `tools`). `dependabot-locks.yml` runs on every Dependabot pull request that changes `global.json`, a Dockerfile above, a `.csproj` or a `packages.lock.json`; it skips pull requests by anyone else. It restores with exactly the SDK of `global.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate`; the action turns `rollForward` off for the rest of the job, or the newest 10.0 SDK on the runner would write the lock files), commits any changed lock files to the pull request's branch as "build: regenerate lock files", and starts `ci` for that commit by `workflow_dispatch`, since a push made with `GITHUB_TOKEN` starts no workflow. That run's `build`, `server-ubuntu`, `docker` and `docker-server` are on the commit (Actions, `ci`, event `workflow_dispatch`), not in the pull request's check list. The `pull_request` run of `ci` on the bot's commit ends with no jobs (the page shows "action required", the API lists it as failed). So the ruleset `main: reviewed changes` does not see its required check `build` on the pull request, and the pull request stays blocked with green checks (#124 and #117: the ruleset's records of those merges show only `required_status_checks` failing, "Required status check "build" is expected."). When the dispatched run has passed, the maintainer merges with `gh pr merge <n> --merge --admin --delete-branch`. A Dependabot pull request that got no lock commit (its lock files were already right) merges with `gh pr merge <n> --merge --delete-branch`. For an SDK update, check that the `aspnet` tag is the runtime that the new SDK ships: Dependabot bumps each tag to its own latest version. If the lock-file job fails, or `build` or `docker` fails with NU1004: start the job again with Actions, `dependabot-locks`, Run workflow, on the pull request's branch, or run the restore with that SDK yourself and push the lock files. After the bot's lock-file commit Dependabot no longer updates the pull request (no rebase, no newer version): comment `@dependabot recreate` to start over from `main`, and the job runs again on the new commit.

**Locked restore.** The `build` and `server-ubuntu` jobs of `ci.yml` and the job of `release.yml` set `RestoreLockedMode: "true"` (an environment variable, which MSBuild reads as the property), so every restore in the job, including the implicit ones of `dotnet build`, `dotnet test` and `publish.cmd`, uses the committed `packages.lock.json` files as they are and fails with NU1004 when one does not match its project. All three build with exactly the SDK of `global.json` (the action above), so the SDK of a release is the one that was reviewed. `global.json` keeps `rollForward: latestFeature` for local builds, which restore unlocked. A pull request that changes a `PackageReference` (or the SDK) without its lock files therefore fails `build` and `server-ubuntu`; regenerate them with `dotnet restore hunter-resurgence.slnx --force-evaluate` using exactly the SDK of `global.json` (a newer local SDK writes its own version of the packages the SDK adds itself, such as `Microsoft.NET.ILLink.Tasks`, so do not commit lock files it rewrote). On a Dependabot SDK or NuGet pull request `build` and `server-ubuntu` fail until `dependabot-locks.yml` has pushed the lock files and started `ci` again.

## VPS prerequisites

- Ubuntu 24.04, Docker Engine and the Compose plugin from [Docker's apt repository](https://docs.docker.com/engine/install/ubuntu/).
- A non-root user for deploys (`deploy` below) in the `docker` group, with the public half of the deploy key in `~/.ssh/authorized_keys`. Never root.
- The shared network, once: `docker network create edge`.
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

`WEB_IMAGE` and `SERVER_IMAGE` are in lower case. Every deploy rewrites the `WEB_TAG` and `SERVER_TAG` lines with the commit sha it rolled out, so the lines have to exist. Every deploy also replaces the `LINK_BACKUP=<url>` line with the repository variable `LINK_BACKUP` (no line when the variable is unset); compose passes it to `web`.

The website's other settings are environment variables of the `web` service: `FORWARDED_TRUSTED`, `LINK_BACKUP` and `SERVER_URL`. `SERVER_URL` is the community server's address on the `internal` network (for example `http://server:8080`). With it the website signs players in through the server (see [website.md](website.md#sign-in)); without it the website keeps its mock session. The server's `web_host` setting must be the website's domain, and Caddy has to send `/auth/*` to the server for the sign-in links to work.

`settings.json` (game name and the other links, see [launcher.md](launcher.md#settingsjson)) is optional. The backup tool link comes from the `LINK_BACKUP` variable, which wins over `link_backup` of the file. To use one, put it in `~/web/` and mount it in `compose.override.yml`, which Compose reads next to `compose.yml` and deploys never overwrite:

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
- **Pitfall:** Docker publishes container ports through its own iptables rules, before `ufw`'s, so `ufw` rules do **not** protect ports 80 and 443. The rules for them go in Docker's `DOCKER-USER` chain. `deploy/firewall.sh` reads Cloudflare's [IPv4](https://www.cloudflare.com/ips-v4) and [IPv6](https://www.cloudflare.com/ips-v6) ranges, rebuilds a `CLOUDFLARE` chain with them (anything else is dropped) and jumps to it from `DOCKER-USER` for ports 80 and 443 on the public interface. Running it again replaces the old ranges.

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

1. Copy `deploy/compose.yml` and `deploy/Caddyfile` to `~/web/`, create `.env`, `certs/` and `sites/` (above), and the server's folder ([The community server](#the-community-server)).
2. Make the images public once they exist (after the first deploy run): GitHub, the packages `<repo>-web` and `<repo>-server` of `<owner>`, Package settings, Change visibility, Public. The repository is public and the images hold no secrets, so the VPS pulls without a login. (If they must stay private: `docker login ghcr.io` on the VPS with a token that has `read:packages` only.)
3. `cd ~/web && docker compose up -d`, then `docker compose ps`: `web` and `server` healthy, `caddy` and `server-backup` running.
4. Open `https://<domain>/` through Cloudflare.

## Deploys

`.github/workflows/deploy.yml` runs when the `ci` workflow succeeds for a push to `main` (never for pull requests, so forks get no secrets):

1. builds the website and server images from that commit and pushes `ghcr.io/<owner>/<repo>-web:<sha>` and `-server:<sha>`, each also as `:latest`;
2. copies `compose.yml` and `Caddyfile` to `~/web/` over SSH, writes `WEB_TAG=<sha>`, `SERVER_TAG=<sha>` and `LINK_BACKUP` into `.env`, then `docker compose pull && docker compose up -d`, reloads Caddy and removes dangling images. Compose stops the old `server` before it starts the new one, so two never write the database at once.

Repository secrets, separate from the release secrets in [CONTRIBUTING.md](../CONTRIBUTING.md#releases):

| Secret | Value |
|---|---|
| `DEPLOY_HOST` | the VPS address |
| `DEPLOY_USER` | the deploy user |
| `DEPLOY_KEY` | the private half of that user's SSH key (no passphrase) |

Without them (a fork, or no VPS yet) the run ends with a notice and deploys nothing. The host key is taken with `ssh-keyscan` at every run (trust on first use); the stricter option is a fourth secret with the pinned `known_hosts` line.

Repository variable (Settings, Secrets and variables, Actions, Variables): `LINK_BACKUP`, the backup tool link, `https://github.com/<owner>/<repo>/releases/latest/download/backup-win-x64.zip`. Without it the deploy removes the line and the site uses `link_backup` of `settings.json`, if any, else shows no backup tool button. A value that is not a plain `https://` URL (no spaces, `'`, `$` or `#`) fails the deploy before the rollout, after the image is pushed. The deploy also ends `.env` with a newline before it appends the line.

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
| path `/auth/*` (the `@server_paths` matcher, the one place to change the server's website-facing paths) | `server:8080`, `X-Game-Host` removed |
| anything else | `web:8080`, `X-Game-Host` removed |

The server believes `X-Game-Host` only from `trusted_proxies`, so a client cannot choose a game host except through Caddy, and Caddy's address is the only one listed. The server builds its sign-in links as `https://` plus the request's `Host`, which Caddy passes on unchanged, and the website forwards the browser's `Host` in its server-side calls (`SERVER_URL=http://server:8080`, set in compose).

**What the operator puts on the VPS**, in `SERVER_DIR` (default `~/server/`, in `.env`; mounted read-only at `/srv`), never committed:

```
settings.json     from server/Server/settings.example.json, with the real values (below)
install/          the operator's own copy of the game's install folder
recordings/       the operator's recordings (server.md)
mirror/           the saved static pages (server.md)
variants.json     optional
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

- Everything is a mock; the banner text has to change when that does.
