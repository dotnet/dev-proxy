#!/usr/bin/env bash
# curl smoke client. Prints one "RESULT<TAB>scenario<TAB>PASS|FAIL|SKIP<TAB>detail" line per scenario.
# Env: SMOKE_PROXY_URL, SMOKE_TARGET_URL, SMOKE_CA_FILE, SMOKE_SSE_MIN_SPAN_MS
set -uo pipefail

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

curl_() {
  curl --silent --show-error --max-time 30 --noproxy '' \
    --proxy "$SMOKE_PROXY_URL" --cacert "$SMOKE_CA_FILE" "$@"
}

result() { printf 'RESULT\t%s\t%s\t%s\n' "$1" "$2" "$3"; }

header() { grep -i "^$1:" "$2" | head -n1 | cut -d: -f2- | tr -d ' \r'; }

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1; }

now_ms() { date +%s%3N; }

# mock
if status=$(curl_ -D "$work/h" -o "$work/b" -w '%{http_code}' "$SMOKE_TARGET_URL/mocked" 2>"$work/e"); then
  if [[ $status == 200 && $(header x-smoke-mock "$work/h") == yes ]] && grep -q '"mocked": *true' "$work/b"; then
    result mock PASS ""
  else
    result mock FAIL "status=$status x-smoke-mock=$(header x-smoke-mock "$work/h") body=$(head -c 200 "$work/b")"
  fi
else
  result mock FAIL "$(cat "$work/e")"
fi

# passthrough
if status=$(curl_ -D "$work/h" -o "$work/b" -w '%{http_code}' -H 'X-Smoke-Request: hello-curl' "$SMOKE_TARGET_URL/passthrough" 2>"$work/e"); then
  if [[ $status == 203 && $(header x-smoke-echo "$work/h") == hello-curl ]] && grep -q '"smokeRequest":"hello-curl"' "$work/b"; then
    result passthrough PASS ""
  else
    result passthrough FAIL "status=$status x-smoke-echo=$(header x-smoke-echo "$work/h") body=$(head -c 200 "$work/b")"
  fi
else
  result passthrough FAIL "$(cat "$work/e")"
fi

# upload: 5 MiB sent with Transfer-Encoding: chunked
head -c $((5 * 1024 * 1024)) /dev/urandom >"$work/upload.bin"
expected_sha=$(sha256 "$work/upload.bin")
if body=$(curl_ -H 'Content-Type: application/octet-stream' -H 'Transfer-Encoding: chunked' \
    --data-binary @"$work/upload.bin" "$SMOKE_TARGET_URL/upload" 2>"$work/e"); then
  if [[ $body == *"\"bytes\":5242880"* && $body == *"\"sha256\":\"$expected_sha\""* ]]; then
    result upload PASS ""
  else
    result upload FAIL "response=$body expected sha256=$expected_sha"
  fi
else
  result upload FAIL "$(cat "$work/e")"
fi

# sse: events must arrive in order and incrementally (not buffered until the end)
curl_ --no-buffer "$SMOKE_TARGET_URL/sse" 2>"$work/e" | while IFS= read -r line; do
  if [[ $line == data:* ]]; then echo "$(now_ms) ${line#data: }"; fi
done >"$work/events"
if [[ ${PIPESTATUS[0]} -eq 0 ]]; then
  ids=$(cut -d' ' -f2 "$work/events" | tr -d '\r' | paste -sd, -)
  first=$(head -n1 "$work/events" | cut -d' ' -f1)
  last=$(tail -n1 "$work/events" | cut -d' ' -f1)
  span=$(( ${last:-0} - ${first:-0} ))
  if [[ $ids == 1,2,3 && $span -ge $SMOKE_SSE_MIN_SPAN_MS ]]; then
    result sse PASS "span=${span}ms"
  else
    result sse FAIL "events=[$ids] span=${span}ms (min ${SMOKE_SSE_MIN_SPAN_MS}ms)"
  fi
else
  result sse FAIL "$(cat "$work/e")"
fi

result websocket SKIP "curl CLI has no interactive WebSocket client"
