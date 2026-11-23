#!/usr/bin/env bash
# Runs real HTTP clients (curl, Node.js, Python, .NET) through a running Dev Proxy
# against a local HTTPS server and prints a client x scenario results table.
#
# Usage: tests/clients/run.sh [client...]     (default: curl node python dotnet)
# Env:   DEVPROXY  path to the devproxy executable (default: devproxy on PATH)
#        PYTHON    Python interpreter with requirements.txt installed (default: python3)
#
# Nothing touches the real user profile or OS trust store: Dev Proxy runs with an
# isolated HOME/XDG_CONFIG_HOME (where it keeps its root certificate and state),
# and each client is pointed at the exported root certificate explicitly.
# No external network access is needed.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
devproxy=${DEVPROXY:-devproxy}
clients=("$@")
[[ ${#clients[@]} -gt 0 ]] || clients=(curl node python dotnet)
scenarios=(mock passthrough upload sse websocket)

work=$(mktemp -d)
proxy_home="$work/home"
mkdir -p "$proxy_home/.config"
server_pid=""
proxy_pid=""
log_file=""

# DEV_PROXY_CERT_PATH is set explicitly so an inherited value can't point at a real root cert.
devproxy_() {
  HOME="$proxy_home" XDG_CONFIG_HOME="$proxy_home/.config" \
    DEV_PROXY_CERT_PATH="$proxy_home/.config/dev-proxy/rootCert.pfx" "$devproxy" "$@"
}

# PIDs of processes started with this run's isolated config dir. Catches a detached
# Dev Proxy child even if `--detach` failed (e.g. timed out) before reporting its PID.
isolated_pids() {
  local environ pid
  for environ in /proc/[0-9]*/environ; do
    pid=${environ#/proc/}
    pid=${pid%/environ}
    tr '\0' '\n' <"$environ" 2>/dev/null | grep -qxF "XDG_CONFIG_HOME=$proxy_home/.config" && echo "$pid"
  done
  return 0
}

cleanup() {
  local code=$?
  if [[ -n $proxy_pid ]]; then
    devproxy_ stop --pid "$proxy_pid" >/dev/null 2>&1 || kill "$proxy_pid" 2>/dev/null || true
  fi
  local leftover
  leftover=$(isolated_pids)
  if [[ -n $leftover ]]; then
    kill $leftover 2>/dev/null || true
    for _ in $(seq 1 50); do
      [[ -z $(isolated_pids) ]] && break
      sleep 0.1
    done
    leftover=$(isolated_pids)
    [[ -n $leftover ]] && kill -9 $leftover 2>/dev/null || true
  fi
  [[ -n $server_pid ]] && kill "$server_pid" 2>/dev/null || true
  if [[ $code -ne 0 ]]; then
    echo "::group::Dev Proxy log"
    [[ -n $log_file && -f $log_file ]] && cat "$log_file"
    echo "::endgroup::"
    echo "::group::Target server log"
    cat "$work/server.log" 2>/dev/null || true
    echo "::endgroup::"
  fi
  rm -rf "$work"
  exit "$code"
}
trap cleanup EXIT

wait_for_file() {
  for _ in $(seq 1 100); do
    [[ -s $1 ]] && return 0
    sleep 0.1
  done
  echo "Timed out waiting for $1" >&2
  return 1
}

echo "Creating TLS certificate for the local target server..."
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj "/CN=Smoke Test CA" \
  -keyout "$work/target-ca.key" -out "$work/target-ca.pem" \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj "/CN=localhost" \
  -keyout "$work/target.key" -out "$work/target.csr" 2>/dev/null
printf 'subjectAltName=DNS:localhost\nextendedKeyUsage=serverAuth\n' >"$work/target.ext"
openssl x509 -req -in "$work/target.csr" -CA "$work/target-ca.pem" -CAkey "$work/target-ca.key" \
  -CAcreateserial -days 1 -extfile "$work/target.ext" -out "$work/target.pem" 2>/dev/null

echo "Starting local target server..."
node "$here/server/server.mjs" "$work/target.pem" "$work/target.key" "$work/target.port" >"$work/server.log" 2>&1 &
server_pid=$!
wait_for_file "$work/target.port"
target_port=$(cat "$work/target.port")
target_url="https://localhost:$target_port"
echo "Target server: $target_url"

echo "Starting Dev Proxy..."
# Dev Proxy validates the upstream (target) certificate; on Linux .NET reads trusted
# roots from SSL_CERT_FILE, so only the local target CA is trusted upstream.
started=$(SSL_CERT_FILE="$work/target-ca.pem" SMOKE_TARGET_PORT="$target_port" devproxy_ \
  --config-file "$here/devproxy/devproxyrc.json" \
  --detach --output json \
  --as-system-proxy false --install-cert false --port 0 --api-port 0)
echo "$started"
read -r proxy_pid proxy_url log_file < <(node -e '
  const lines = require("fs").readFileSync(0, "utf8").trim().split("\n");
  const entry = lines.map(l => { try { return JSON.parse(l); } catch { return {}; } })
    .map(e => e.data ?? e).find(e => e.ProxyUrl ?? e.proxyUrl);
  if (!entry) process.exit(1);
  console.log([entry.Pid ?? entry.pid, entry.ProxyUrl ?? entry.proxyUrl, entry.LogFile ?? entry.logFile].join(" "));
' <<<"$started") || true
if [[ -z $proxy_pid || -z $proxy_url ]]; then
  echo "Couldn't read the Dev Proxy PID and URL from its startup output." >&2
  exit 1
fi
echo "Dev Proxy: $proxy_url (PID $proxy_pid)"

wait_for_file "$proxy_home/.config/dev-proxy/rootCert.pfx"
openssl pkcs12 -in "$proxy_home/.config/dev-proxy/rootCert.pfx" -passin pass: -nokeys -out "$work/devproxy-ca.pem" 2>/dev/null \
  || openssl pkcs12 -legacy -in "$proxy_home/.config/dev-proxy/rootCert.pfx" -passin pass: -nokeys -out "$work/devproxy-ca.pem"

export SMOKE_PROXY_URL="$proxy_url"
export SMOKE_TARGET_URL="$target_url"
export SMOKE_CA_FILE="$work/devproxy-ca.pem"
# The server sends 3 events 1s apart; buffering would make them arrive together.
export SMOKE_SSE_MIN_SPAN_MS=1500

run_client() {
  case $1 in
    curl) timeout 120 bash "$here/curl/smoke.sh" ;;
    node) NODE_USE_ENV_PROXY=1 HTTPS_PROXY="$proxy_url" NO_PROXY='' NODE_EXTRA_CA_CERTS="$SMOKE_CA_FILE" \
            timeout 120 node "$here/node/smoke.mjs" ;;
    python) SSL_CERT_FILE="$SMOKE_CA_FILE" timeout 120 "${PYTHON:-python3}" "$here/python/smoke.py" ;;
    dotnet) timeout 300 dotnet run --file "$here/dotnet/smoke.cs" ;;
    *) echo "Unknown client: $1" >&2; return 1 ;;
  esac
}

results="$work/results.tsv"
: >"$results"
for client in "${clients[@]}"; do
  echo "::group::$client"
  set +e
  run_client "$client" >"$work/$client.out" 2>&1
  code=$?
  set -e
  cat "$work/$client.out"
  echo "::endgroup::"
  for scenario in "${scenarios[@]}"; do
    line=$(grep -P "^RESULT\t$scenario\t" "$work/$client.out" | head -n1 || true)
    if [[ -n $line ]]; then
      printf '%s\t%s\n' "$client" "${line#RESULT	}" >>"$results"
    else
      printf '%s\t%s\tFAIL\tno result (client exited with %s)\n' "$client" "$scenario" "$code" >>"$results"
    fi
  done
  # A crash or timeout after all scenarios reported must still fail the run.
  if [[ $code -ne 0 ]]; then
    printf '%s\tprocess\tFAIL\tclient exited with %s\n' "$client" "$code" >>"$results"
  fi
done

icon() { case $1 in PASS) echo "✅" ;; FAIL) echo "❌" ;; *) echo "➖" ;; esac; }
summary="$work/summary.md"
{
  echo "## Client smoke tests"
  echo
  printf '| Client |'; printf ' %s |' "${scenarios[@]}"; echo
  printf '|---|'; printf -- '---|%.0s' "${scenarios[@]}"; echo
  for client in "${clients[@]}"; do
    printf '| %s |' "$client"
    for scenario in "${scenarios[@]}"; do
      status=$(awk -F'\t' -v c="$client" -v s="$scenario" '$1==c && $2==s {print $3; exit}' "$results")
      printf ' %s |' "$(icon "$status")"
    done
    echo
  done
  if awk -F'\t' '$3!="PASS"' "$results" | grep -q .; then
    echo
    echo "| Client | Scenario | Result | Detail |"
    echo "|---|---|---|---|"
    awk -F'\t' '$3!="PASS" {gsub(/\|/, "\\|", $4); printf "| %s | %s | %s | %s |\n", $1, $2, $3, $4}' "$results"
  fi
} >"$summary"

cat "$summary"
[[ -n ${GITHUB_STEP_SUMMARY:-} ]] && cat "$summary" >>"$GITHUB_STEP_SUMMARY"

if awk -F'\t' '$3=="FAIL"' "$results" | grep -q .; then
  echo "Client smoke tests failed." >&2
  exit 1
fi
