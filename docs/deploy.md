# Deploy the website

## What this is

The [website](website.md) (`src/Web/`) in a container on one Ubuntu 24.04 VPS, behind Caddy and Cloudflare. It is the **mock** website: every page shows fixed, fictional data. It is not the game server. A banner at the bottom of every page (`Wip.Banner` in `src/Ui/Strings*.resx`) says so; change or remove it when real features arrive.

| File | Purpose |
|---|---|
| `src/Web/Dockerfile` | the image (build context: the repository root). It listens on port 8080 as a non-root user. Its SDK tag is the version of `global.json` and its `aspnet` tag the runtime that SDK ships: a new SDK means `global.json`, both tags and every `packages.lock.json` (`dotnet restore hunter-resurgence.slnx --force-evaluate` with that SDK) in one change, or the locked restore fails with NU1004 |
| `deploy/compose.yml` | production stack: `caddy` (ports 80 and 443) in front of `web` |
| `deploy/Caddyfile` | the site `{$SITE_DOMAIN}` with the Cloudflare origin certificate, then `import sites/*.caddy` |
| `deploy/.env.example` | the three values of `.env` |
| `deploy/firewall.sh` | ports 80 and 443 for Cloudflare only |
| `deploy/compose.dev.yml`, `deploy/Caddyfile.dev` | local test on `https://localhost` |
| `.github/workflows/deploy.yml` | build, push and roll out after a green `ci` on `main` |

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
```

`WEB_IMAGE` is in lower case. Every deploy rewrites the `WEB_TAG` line with the commit sha it rolled out, so the line has to exist.

`settings.json` (game name and links, see [launcher.md](launcher.md#settingsjson)) is optional. To use one, put it in `~/web/` and mount it in `compose.override.yml`, which Compose reads next to `compose.yml` and deploys never overwrite:

```yaml
services:
  web:
    volumes: ["./settings.json:/app/settings.json:ro"]
```

## Cloudflare

- DNS: a proxied (orange cloud) `A` record for `<domain>` pointing at the VPS. For `www`: a proxied record with any placeholder target (for example `AAAA 100::`) and a Redirect Rule from `www.<domain>` to `https://<domain>`, keeping the path and the query string, 301. The VPS serves only `<domain>`.
- SSL/TLS mode **Full (strict)**; Always Use HTTPS on; minimum TLS version 1.2; WebSockets on (the default, Blazor needs them).
- An **Origin Certificate** for `<domain>` (add `*.<domain>` if other sites will share it), saved on the VPS as `certs/origin.pem` and `certs/origin.key` (`chmod 600`). With these files Caddy requests no certificate of its own.
- No HSTS header yet: add `Strict-Transport-Security` in the Caddyfile only after Full (strict) has worked for a day.

Caddy passes Cloudflare's `CF-Connecting-IP` to the app as `X-Forwarded-For`, and the app accepts forwarded headers from the compose network only (`FORWARDED_TRUSTED`). The header is trustworthy only because the firewall lets nothing but Cloudflare reach ports 80 and 443.

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

1. Copy `deploy/compose.yml` and `deploy/Caddyfile` to `~/web/`, create `.env`, `certs/` and `sites/` (above).
2. Make the image public once it exists (after the first deploy run): GitHub, the package `<repo>-web` of `<owner>`, Package settings, Change visibility, Public. The repository is public and the image holds no secrets, so the VPS pulls without a login. (If it must stay private: `docker login ghcr.io` on the VPS with a token that has `read:packages` only.)
3. `cd ~/web && docker compose up -d`, then `docker compose ps`: `web` healthy, `caddy` running.
4. Open `https://<domain>/` through Cloudflare.

## Deploys

`.github/workflows/deploy.yml` runs when the `ci` workflow succeeds for a push to `main` (never for pull requests, so forks get no secrets):

1. builds the image from that commit and pushes `ghcr.io/<owner>/<repo>-web:<sha>` and `:latest`;
2. copies `compose.yml` and `Caddyfile` to `~/web/` over SSH, writes `WEB_TAG=<sha>` into `.env`, then `docker compose pull && docker compose up -d`, reloads Caddy and removes dangling images.

Repository secrets, separate from the release secrets in [CONTRIBUTING.md](../CONTRIBUTING.md#releases):

| Secret | Value |
|---|---|
| `DEPLOY_HOST` | the VPS address |
| `DEPLOY_USER` | the deploy user |
| `DEPLOY_KEY` | the private half of that user's SSH key (no passphrase) |

Without them (a fork, or no VPS yet) the run ends with a notice and deploys nothing. The host key is taken with `ssh-keyscan` at every run (trust on first use); the stricter option is a fourth secret with the pinned `known_hosts` line.

## Rollback

The running version is the `WEB_TAG` line of `~/web/.env`. Every pushed sha stays in GHCR; find an older one in the list of `deploy` runs on GitHub, on the package page, or with `docker images` on the VPS. Then:

```
ssh deploy@<vps> "cd web && sed -i 's/^WEB_TAG=.*/WEB_TAG=<older sha>/' .env && docker compose up -d"
```

The next deploy sets the tag again.

## Adding the server service or another site

- **The game's community server, later:** a second service in `deploy/compose.yml`, on the `internal` network only, and a route in the Caddyfile (a `handle` block in the site, or its own host name). `FORWARDED_TRUSTED` trusts the whole `internal` network, so the server could set forwarded headers too; to narrow it, give `caddy` a fixed `ipv4_address` on `internal` and trust only that address.
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

The override builds the image as `web:dev` and serves `https://localhost` with Caddy's own local CA, which nothing trusts (`curl -k`, or accept the warning in the browser):

```
curl -ksf https://localhost/feed | grep "Work in progress"
curl -ks -X POST "https://localhost/_blazor/negotiate?negotiateVersion=1"    # WebSockets among the transports
curl -sS https://localhost/ -o /dev/null                                     # fails: certificate not trusted
```

In the browser's network tab a WebSocket to `/_blazor` (status 101) stays open while you move between pages. Stop with `docker compose -f deploy/compose.yml -f deploy/compose.dev.yml down`.

## Known limits

- The Data Protection keys (antiforgery tokens) are not persisted: a new container makes new ones. Open pages lose their circuit at a deploy anyway and reload with new tokens.
- Everything is a mock; the banner text has to change when that does.
