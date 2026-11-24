#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Adds the Dev Proxy root certificate to the system certificate store.
# Runs as root (the remote user can run it through sudo without a password).
# Reads a PEM-encoded certificate from stdin so that root never reads files
# from user-controlled paths, and only accepts a single CA certificate.

set -euo pipefail

CERT_FILE=/usr/local/share/ca-certificates/devproxy-ca.crt

if [ "$(id -u)" -ne 0 ]; then
    echo "(!) This script must run as root." >&2
    exit 1
fi

input="$(head -c 65536)"
if [ -z "${input}" ]; then
    echo "(!) Pipe the Dev Proxy root certificate (PEM) to this script." >&2
    exit 1
fi

if ! pem="$(printf '%s\n' "${input}" | openssl x509 -outform PEM 2>/dev/null)"; then
    echo "(!) The input isn't a valid PEM-encoded certificate." >&2
    exit 1
fi

if ! printf '%s\n' "${pem}" | openssl x509 -noout -text 2>/dev/null | grep -q 'CA:TRUE'; then
    echo "(!) The certificate isn't a CA certificate." >&2
    exit 1
fi

mkdir -p "$(dirname "${CERT_FILE}")"
tmp_file="$(mktemp "${CERT_FILE}.XXXXXX")"
printf '%s\n' "${pem}" > "${tmp_file}"
chmod 0644 "${tmp_file}"
mv -f "${tmp_file}" "${CERT_FILE}"

if ! output="$(update-ca-certificates 2>&1)"; then
    echo "${output}" >&2
    echo "(!) Couldn't update the system certificate store." >&2
    exit 1
fi
echo "Dev Proxy root certificate added to the system certificate store."
