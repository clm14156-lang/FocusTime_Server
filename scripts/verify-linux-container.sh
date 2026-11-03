#!/usr/bin/env bash
set -euo pipefail

if [[ $(uname -s) != Linux ]]; then
  echo "This acceptance check must run on Linux." >&2
  exit 2
fi
for command in docker curl python3 od; do
  command -v "$command" >/dev/null || { echo "Missing prerequisite: $command" >&2; exit 2; }
done
if [[ $(docker info --format '{{.OSType}}') != linux ]]; then
  echo "A Linux Docker engine is required." >&2
  exit 2
fi

root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
output="$root/.artifacts/linux-container"
mkdir -p "$output"
run="${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}-$$"
network="focustime-validation-$run"
database="focustime-validation-db-$run"
api="focustime-validation-api-$run"
image="focustime-foundation:validation-$run"
umask 077
scratch=$(mktemp -d)

cleanup() {
  local status=$?
  trap - EXIT
  docker logs "$api" >"$output/api.log" 2>&1 || true
  docker logs "$database" >"$output/postgres.log" 2>&1 || true
  docker rm -f "$api" "$database" >/dev/null 2>&1 || true
  docker network rm "$network" >/dev/null 2>&1 || true
  rm -f -- "$scratch/database.env" "$scratch/api.env"
  rmdir -- "$scratch" || true
  exit "$status"
}
trap cleanup EXIT

password=$(od -An -N24 -tx1 /dev/urandom | tr -d ' \n')
printf 'POSTGRES_DB=focustime_validation\nPOSTGRES_USER=focustime\nPOSTGRES_PASSWORD=%s\n' "$password" >"$scratch/database.env"
printf 'ASPNETCORE_ENVIRONMENT=Production\nConnectionStrings__Database=Host=db;Port=5432;Database=focustime_validation;Username=focustime;Password=%s;Timeout=2;Command Timeout=3\n' "$password" >"$scratch/api.env"

docker build -t "$image" . 2>&1 | tee "$output/build.log"
docker image inspect "$image" --format '{{.Id}}' >"$output/image-id.txt"
docker run --rm --entrypoint dotnet "$image" --info >"$output/runtime.txt"
docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" --network-alias db \
  --env-file "$scratch/database.env" \
  --health-cmd 'pg_isready -U focustime -d focustime_validation' \
  --health-interval 1s --health-timeout 3s --health-retries 60 postgres:18 >/dev/null

wait_database() {
  for attempt in {1..60}; do
    if [[ $(docker inspect --format '{{.State.Health.Status}}' "$database") == healthy ]]; then
      return 0
    fi
    sleep 1
  done
  echo "Temporary PostgreSQL did not become healthy." >&2
  return 1
}
wait_database
docker run --rm --network "$network" --env-file "$scratch/api.env" "$image" --migrate 2>&1 | tee "$output/migrate-first.log"
docker run --rm --network "$network" --env-file "$scratch/api.env" "$image" --migrate 2>&1 | tee "$output/migrate-repeat.log"
docker run -d --name "$api" --network "$network" --env-file "$scratch/api.env" \
  -p '127.0.0.1::8080' "$image" >/dev/null
base="http://$(docker port "$api" 8080/tcp | head -n 1)"

expect_http() {
  local path=$1 expected=$2 name=$3 status
  for attempt in {1..45}; do
    if status=$(curl --silent --show-error --max-time 5 --connect-timeout 2 \
      --dump-header "$output/$name.headers" --output "$output/$name.json" \
      --write-out '%{http_code}' "$base$path" 2>"$output/$name.curl-error.log"); then
      if [[ $status == "$expected" ]]; then
        printf '%s %s -> %s\n' "$name" "$path" "$status"
        return 0
      fi
    fi
    sleep 1
  done
  echo "$path: expected HTTP $expected, got ${status:-unavailable}." >&2
  return 1
}

expect_http /health/ready 200 ready
expect_http /health/live 200 live
expect_http /api/v1/system/info 200 info
expect_http /not-found 404 not-found
expect_http /openapi/v1.json 404 production-openapi
docker stop --time 5 "$database" >/dev/null
expect_http /health/ready 503 database-down-ready
expect_http /health/live 200 database-down-live
docker start "$database" >/dev/null
wait_database
expect_http /health/ready 200 database-recovered-ready

python3 - "$output" <<'PY'
import json
import pathlib
import sys

output = pathlib.Path(sys.argv[1])
def read(name):
    return json.loads((output / f"{name}.json").read_text(encoding="utf-8"))

assert read("ready")["checks"]["database"] == "healthy"
assert read("live")["status"] == "healthy"
assert read("info")["apiVersion"] == "v1"
error = read("not-found")
assert error["code"] == "not_found"
headers = dict(line.split(":", 1) for line in (output / "not-found.headers").read_text().splitlines() if ":" in line)
trace = next(value.strip() for name, value in headers.items() if name.lower() == "x-trace-id")
assert error["traceId"] == trace
assert read("production-openapi")["code"] == "not_found"
assert read("database-down-ready")["checks"]["database"] == "unhealthy"
assert read("database-down-live")["status"] == "healthy"
assert read("database-recovered-ready")["checks"]["database"] == "healthy"
result = {"platform": "linux", "passed": True, "cliMigrations": 2,
          "ready": 200, "live": 200, "info": 200, "notFound": 404,
          "productionOpenApi": 404, "databaseDownReady": 503,
          "databaseDownLive": 200, "databaseRecoveredReady": 200,
          "imageId": (output / "image-id.txt").read_text().strip()}
(output / "result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
print(json.dumps(result, indent=2))
PY
