#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Shared helpers for the Dev Proxy Feature tests. Source after
# dev-container-features-test-lib.

DEVPROXY_TEST_PORT="${DEVPROXY_TEST_PORT:-8000}"
DEVPROXY_TEST_API_PORT="${DEVPROXY_TEST_API_PORT:-8897}"
DEVPROXY_TEST_URL="https://devproxy-feature.test/hello"
DEVPROXY_TEST_BODY="hello-from-dev-proxy"
DEVPROXY_TEST_PROXY="http://127.0.0.1:${DEVPROXY_TEST_PORT}"
DEVPROXY_TEST_CERT=/usr/local/share/ca-certificates/devproxy-ca.crt
DEVPROXY_TEST_BUNDLE=/etc/ssl/certs/ca-certificates.crt

devproxy_config_dir() {
    echo "${XDG_CONFIG_HOME:-${HOME}/.config}/dev-proxy"
}

# Checks that the Dev Proxy root certificate in the user's profile is the one
# trusted in the system store.
devproxy_cert_matches_profile() {
    local pfx
    pfx="$(devproxy_config_dir)/rootCert.pfx"
    [ -f "${pfx}" ] || return 1
    local profile_fp system_fp
    profile_fp="$(openssl pkcs12 -in "${pfx}" -clcerts -nokeys -passin pass: 2>/dev/null | openssl x509 -noout -fingerprint -sha256)"
    system_fp="$(openssl x509 -in "${DEVPROXY_TEST_CERT}" -noout -fingerprint -sha256)"
    [ -n "${profile_fp}" ] && [ "${profile_fp}" = "${system_fp}" ]
}

# Checks that the certificate is part of the system CA bundle.
devproxy_cert_in_bundle() {
    local cert_body
    cert_body="$(sed -n '2p' "${DEVPROXY_TEST_CERT}")"
    grep -qF "${cert_body}" "${DEVPROXY_TEST_BUNDLE}"
}

# Checks that no Dev Proxy root certificate exists outside of the given home
# directory (e.g. in root's profile or the install directory). Scans as root
# when possible and fails if the scan is incomplete. Users without root access
# can't read root-only folders like /root; root-user scenarios cover those.
devproxy_no_stray_certs() {
    local allowed_dir="${1:-}"
    local -a run_as=()
    if [ "$(id -u)" -ne 0 ]; then
        if sudo -n true 2>/dev/null; then
            run_as=(sudo -n)
        else
            echo "(!) No root access. Root-only folders aren't scanned." >&2
        fi
    fi
    local found
    if [ "${#run_as[@]}" -gt 0 ] || [ "$(id -u)" -eq 0 ]; then
        found="$("${run_as[@]}" find / -xdev -name 'rootCert.pfx' -not -path '/proc/*')" || return 1
    else
        found="$(find / -xdev -name 'rootCert.pfx' -not -path '/proc/*' 2>/dev/null || true)"
    fi
    if [ -n "${allowed_dir}" ]; then
        found="$(echo "${found}" | grep -v "^${allowed_dir}/" || true)"
    fi
    if [ -n "${found}" ]; then
        echo "Found certificates: ${found}" >&2
        return 1
    fi
}

# Starts Dev Proxy with a mock that answers DEVPROXY_TEST_URL so that tests
# don't depend on external services.
devproxy_start() {
    DEVPROXY_TEST_DIR="$(mktemp -d)"
    cat > "${DEVPROXY_TEST_DIR}/mocks.json" <<EOF
{
  "mocks": [
    {
      "request": { "url": "${DEVPROXY_TEST_URL}" },
      "response": {
        "statusCode": 200,
        "headers": [ { "name": "content-type", "value": "text/plain" } ],
        "body": "${DEVPROXY_TEST_BODY}"
      }
    }
  ]
}
EOF
    cat > "${DEVPROXY_TEST_DIR}/devproxyrc.json" <<EOF
{
  "plugins": [
    {
      "name": "MockResponsePlugin",
      "enabled": true,
      "pluginPath": "~appFolder/plugins/DevProxy.Plugins.dll",
      "configSection": "mocksPlugin"
    }
  ],
  "urlsToWatch": [ "https://devproxy-feature.test/*" ],
  "mocksPlugin": { "mocksFile": "mocks.json" },
  "port": ${DEVPROXY_TEST_PORT},
  "apiPort": ${DEVPROXY_TEST_API_PORT},
  "validateSchemas": false
}
EOF
    pushd "${DEVPROXY_TEST_DIR}" >/dev/null
    devproxy --config-file devproxyrc.json --as-system-proxy false </dev/null >"${DEVPROXY_TEST_DIR}/devproxy.log" 2>&1 &
    DEVPROXY_TEST_PID=$!
    popd >/dev/null

    local i
    for i in $(seq 1 60); do
        if curl -s -o /dev/null "http://127.0.0.1:${DEVPROXY_TEST_API_PORT}/proxy"; then
            return 0
        fi
        sleep 1
    done
    echo "Dev Proxy didn't start. Log:" >&2
    cat "${DEVPROXY_TEST_DIR}/devproxy.log" >&2
    return 1
}

devproxy_stop() {
    if [ -n "${DEVPROXY_TEST_PID:-}" ]; then
        kill "${DEVPROXY_TEST_PID}" 2>/dev/null || true
        wait "${DEVPROXY_TEST_PID}" 2>/dev/null || true
    fi
    rm -rf "${DEVPROXY_TEST_DIR:-}"
}

# Checks that a response body is the mocked one. Some Dev Proxy versions
# return the mocked string body JSON-encoded.
devproxy_is_mock_body() {
    [ "$(echo "$1" | tr -d '"[:space:]')" = "${DEVPROXY_TEST_BODY}" ]
}

# Requests through Dev Proxy don't disable certificate validation, so they
# only succeed if the Dev Proxy root certificate is trusted.
devproxy_curl() {
    devproxy_is_mock_body "$(curl -sS --fail -x "${DEVPROXY_TEST_PROXY}" "${DEVPROXY_TEST_URL}")"
}
