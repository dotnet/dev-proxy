#!/usr/bin/env bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Runs as the remote user after the container is created (postCreateCommand).
# Generates a Dev Proxy root certificate unique to this container and trusts
# it system-wide. Never prompts for input.

set -euo pipefail

SHARE_DIR=/usr/local/share/devproxy
TRUST_HELPER="${SHARE_DIR}/trust-cert.sh"

DEVPROXY_FEATURE_TRUST_CERTIFICATE=false
if [ -f "${SHARE_DIR}/feature.env" ]; then
    # shellcheck source=/dev/null
    . "${SHARE_DIR}/feature.env"
fi

if [ "${DEVPROXY_FEATURE_TRUST_CERTIFICATE}" != true ]; then
    exit 0
fi

echo "Setting up the Dev Proxy root certificate..."

config_dir="${XDG_CONFIG_HOME:-${HOME}/.config}/dev-proxy"
# .NET resolves the config folder to a relative path if it doesn't exist,
# which would put the certificate in the current (workspace) folder.
mkdir -p "${config_dir}"
cd "${config_dir}"

# The root certificate's private key is stored without a password. Keep it
# readable only by the remote user.
umask 077

if ! devproxy cert ensure </dev/null; then
    echo "(!) Couldn't generate the Dev Proxy root certificate." >&2
    exit 1
fi

cert_path="${DEV_PROXY_CERT_PATH:-rootCert.pfx}"
if [[ "${cert_path}" != /* ]]; then
    cert_path="${config_dir}/${cert_path}"
fi

if [ ! -f "${cert_path}" ]; then
    echo "(!) Dev Proxy root certificate not found at ${cert_path}." >&2
    exit 1
fi
if [ -O "${cert_path}" ]; then
    chmod 0600 "${cert_path}"
fi

if ! pem="$(openssl pkcs12 -in "${cert_path}" -clcerts -nokeys -passin pass: 2>/dev/null | openssl x509 -outform PEM 2>/dev/null)"; then
    if ! pem="$(openssl pkcs12 -legacy -in "${cert_path}" -clcerts -nokeys -passin pass: 2>/dev/null | openssl x509 -outform PEM 2>/dev/null)"; then
        echo "(!) Couldn't export the Dev Proxy root certificate from ${cert_path}." >&2
        exit 1
    fi
fi

if [ "$(id -u)" -eq 0 ]; then
    printf '%s\n' "${pem}" | "${TRUST_HELPER}"
else
    printf '%s\n' "${pem}" | sudo -n "${TRUST_HELPER}"
fi
