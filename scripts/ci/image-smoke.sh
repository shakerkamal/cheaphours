#!/usr/bin/env bash
# Smoke checks for the CheapHours image, run by CI on the image it is about to scan and publish, and locally:
#   scripts/ci/image-smoke.sh cheaphours:local
# Checks: the api role starts without its database (/health/live 200 within 30 s, /health/ready 503); an unknown
# role exits 2 naming the valid roles; the user is non-root; the Berlin time zone is present; no local secrets or
# development settings are inside.
set -euo pipefail

image="${1:?usage: image-smoke.sh <image>}"
name="cheaphours-smoke-$$"
failures=0

fail() { echo "FAIL: $*" >&2; failures=$((failures + 1)); }
pass() { echo "ok:   $*"; }

cleanup() {
  docker rm -f "$name" "$name-fs" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# 1. The api role answers liveness without MongoDB, and readiness says it is not ready.
docker run -d --name "$name" -p 127.0.0.1::8080 \
  -e "ConnectionStrings__Mongo=mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500&connectTimeoutMS=500" \
  "$image" --role api >/dev/null
port="$(docker port "$name" 8080/tcp | head -n 1 | sed 's/.*://')"
base="http://127.0.0.1:${port}"

live=""
for _ in $(seq 1 30); do
  live="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$base/health/live" || true)"
  [ "$live" = "200" ] && break
  sleep 1
done
if [ "$live" = "200" ]; then pass "--role api: /health/live 200"; else
  fail "--role api: /health/live answered '${live}' within 30 s"
  docker logs "$name" 2>&1 | tail -n 30 >&2 || true
fi

ready="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$base/health/ready" || true)"
if [ "$ready" = "503" ]; then pass "--role api: /health/ready 503 without MongoDB"; else
  fail "--role api: /health/ready answered '${ready}', expected 503"
fi
docker rm -f "$name" >/dev/null

# 2. An unknown role exits 2 and names the valid roles.
set +e
output="$(docker run --rm "$image" --role nothing 2>&1)"
code=$?
set -e
if [ "$code" -eq 2 ] && grep -q "api, gateway, worker" <<<"$output"; then pass "--role nothing: exit 2, valid roles named"; else
  fail "--role nothing: exit ${code}, output: ${output}"
fi

# 3. The image runs as a non-root numeric user.
user="$(docker image inspect -f '{{.Config.User}}' "$image")"
if [[ "$user" =~ ^[0-9]+$ ]] && [ "$user" -ne 0 ]; then pass "user ${user}"; else
  fail "user is '${user}', expected a non-zero numeric id"
fi

# 4. The file system has the Berlin time zone and no local secrets or development settings.
docker create --name "$name-fs" "$image" >/dev/null
files="$(docker export "$name-fs" | tar -t)"
if grep -qx 'usr/share/zoneinfo/Europe/Berlin' <<<"$files"; then pass "usr/share/zoneinfo/Europe/Berlin present"; else
  fail "usr/share/zoneinfo/Europe/Berlin missing"
fi
for forbidden in '.env' 'appsettings.Development.json' 'secrets.json'; do
  found="$(grep -E "(^|/)${forbidden//./\\.}\$" <<<"$files" || true)"
  if [ -z "$found" ]; then pass "no ${forbidden}"; else fail "found ${forbidden}: ${found}"; fi
done

if [ "$failures" -gt 0 ]; then
  echo "${failures} smoke check(s) failed for ${image}" >&2
  exit 1
fi
echo "All smoke checks passed for ${image}"
