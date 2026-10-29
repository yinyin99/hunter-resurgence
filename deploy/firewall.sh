#!/bin/sh
# Lets only Cloudflare reach ports 80 and 443 of the containers (docs/deploy.md, "Firewall"). Run as root, again whenever
# Cloudflare's ranges change (a systemd timer, see the docs). Docker's published ports bypass ufw: these rules go in
# Docker's DOCKER-USER chain instead. Usage: firewall.sh [public interface, default eth0]
set -eu
iface=${1:-eth0}
for v in 4 6; do
	ipt=iptables; [ "$v" = 6 ] && ipt=ip6tables
	if ! $ipt -n -L DOCKER-USER > /dev/null 2>&1; then echo "IPv$v: no DOCKER-USER chain (Docker does not publish over IPv$v), skipped"; continue; fi
	ranges=$(curl -fsS "https://www.cloudflare.com/ips-v$v")
	# rebuild the list of allowed ranges, then jump to it once from DOCKER-USER
	$ipt -N CLOUDFLARE 2> /dev/null || $ipt -F CLOUDFLARE
	for r in $ranges; do $ipt -A CLOUDFLARE -s "$r" -j RETURN; done
	$ipt -A CLOUDFLARE -j DROP
	rule="-i $iface -p tcp -m multiport --dports 80,443 -j CLOUDFLARE"
	# shellcheck disable=SC2086 # $rule is a list of arguments
	$ipt -C DOCKER-USER $rule 2> /dev/null || $ipt -I DOCKER-USER $rule
	echo "IPv$v: $(echo "$ranges" | wc -w) Cloudflare ranges allowed on $iface ports 80 and 443"
done
