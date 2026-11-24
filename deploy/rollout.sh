#!/bin/sh
# Rolls the stack in the current folder (~/web: compose.yml, .env) out to the images of commit $1 and checks it for up to 60 s: every container running, the website
# and the server answering through Caddy. Otherwise puts the WEB_TAG, SERVER_TAG and NATPT_TAG that ran before back, starts those and exits 1 (docs/deploy.md, "Deploys").
set -u
sha=${1:-}
case $sha in ''|*[!0-9a-f]*) echo "usage: rollout.sh <commit sha>" >&2; exit 2;; esac
domain=$(sed -n 's/^SITE_DOMAIN=//p' .env)
[ -n "$domain" ] || { echo "SITE_DOMAIN is not set in .env" >&2; exit 2; }
prev=$(grep -E '^(WEB|SERVER|NATPT)_TAG=' .env)

tags() { while IFS== read -r k v; do sed -i "s/^$k=.*/$k=$v/" .env; done; } # KEY=value lines on stdin
# the services that are not running (restarting, exited) or whose own health check says so
bad() { docker compose ps -a --format '{{.Service}} {{.State}} {{.Health}}' | awk '$2 != "running" || $3 == "starting" || $3 == "unhealthy" {print $1}'; }
# Caddy answers on this host with the site's Host header and its origin certificate (not trusted here): anything but a failed dial or Caddy's 502/503/504 counts
answers() {
	code=$(curl -ks -o /dev/null -w '%{http_code}' --max-time 5 --resolve "$domain:443:127.0.0.1" "https://$domain$1") || return 1
	case $code in 000|502|503|504) return 1;; esac
}
healthy() { [ -z "$(bad)" ] && answers / && answers /stats/game; } # the website, then the server's own path
wait_healthy() { for _ in $(seq 12); do healthy && return 0; sleep 5; done; return 1; }

printf '%s\n' "WEB_TAG=$sha" "SERVER_TAG=$sha" "NATPT_TAG=$sha" | tags
if docker compose pull && timeout 180 docker compose up -d && docker compose exec -T caddy caddy reload --config /etc/caddy/Caddyfile && wait_healthy; then
	docker image prune -f
	exit 0
fi
failed=$(bad | tr '\n' ' ')
echo "::error::the roll-out of $sha is not healthy (${failed:-no container failed, the site does not answer}): back to the previous images"
# the logs first: the second up -d recreates the failed containers
# shellcheck disable=SC2086 # a list of service names
docker compose logs --no-color --tail 30 ${failed:-web server caddy}
printf '%s\n' "$prev" | tags
docker compose up -d
exit 1
