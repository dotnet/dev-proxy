#!/bin/sh
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Installs Dev Proxy into a dev container. Runs as root at image build time.
# This script intentionally doesn't generate the Dev Proxy root certificate:
# that happens per container in post-create.sh so that the CA private key
# never ends up in an image layer or prebuild.

# Check for unsupported (musl-based) distros before relying on bash.
if [ -f /etc/alpine-release ] || (ldd --version 2>&1 | grep -qi musl); then
    echo "(!) The Dev Proxy Feature doesn't support Alpine or other musl-based images. Use a Debian or Ubuntu-based image instead." >&2
    exit 1
fi

if [ -z "${BASH_VERSION:-}" ]; then
    if ! command -v bash >/dev/null 2>&1; then
        echo "(!) The Dev Proxy Feature requires bash. Use a Debian or Ubuntu-based image." >&2
        exit 1
    fi
    exec bash "$0" "$@"
fi

set -euo pipefail

VERSION="${VERSION:-latest}"
TRUST_CERTIFICATE="${TRUSTCERTIFICATE:-true}"

INSTALL_DIR=/opt/devproxy
SHARE_DIR=/usr/local/share/devproxy
BIN_DIR=/usr/local/bin
TRUST_HELPER="${SHARE_DIR}/trust-cert.sh"
SUDOERS_FILE=/etc/sudoers.d/devproxy
REPO_URL="https://github.com/dotnet/dev-proxy"
FEATURE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

fail() {
    echo "(!) $*" >&2
    exit 1
}

if [ "$(id -u)" -ne 0 ]; then
    fail "The Dev Proxy Feature install script must run as root."
fi

if [ "$(uname -s)" != "Linux" ]; then
    fail "The Dev Proxy Feature supports only Linux containers."
fi

if ! command -v apt-get >/dev/null 2>&1; then
    fail "The Dev Proxy Feature supports only Debian and Ubuntu-based images (apt-get not found)."
fi

case "$(uname -m)" in
    x86_64 | amd64) ARCH=x64 ;;
    aarch64 | arm64) ARCH=arm64 ;;
    *) fail "Unsupported architecture $(uname -m). Dev Proxy supports x64 and arm64." ;;
esac

REMOTE_USER="${_REMOTE_USER:-root}"
if ! id "${REMOTE_USER}" >/dev/null 2>&1; then
    echo "User '${REMOTE_USER}' doesn't exist. Using root instead."
    REMOTE_USER=root
fi
REMOTE_USER_HOME="$(getent passwd "${REMOTE_USER}" | cut -d: -f6)"
REMOTE_USER_GROUP="$(id -gn "${REMOTE_USER}")"

# --- Dependencies -----------------------------------------------------------

APT_UPDATED=false
apt_update() {
    if [ "${APT_UPDATED}" = false ]; then
        echo "Updating package lists..."
        apt-get update -y
        APT_UPDATED=true
    fi
}

apt_install() {
    local missing=()
    local pkg
    for pkg in "$@"; do
        if ! dpkg -s "${pkg}" >/dev/null 2>&1; then
            missing+=("${pkg}")
        fi
    done
    if [ "${#missing[@]}" -gt 0 ]; then
        apt_update
        echo "Installing ${missing[*]}..."
        DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${missing[@]}"
    fi
}

install_dependencies() {
    local packages=(curl ca-certificates unzip openssl)
    if [ "${TRUST_CERTIFICATE}" = true ] && [ "${REMOTE_USER}" != root ]; then
        packages+=(sudo)
    fi
    apt_install "${packages[@]}"

    # Dev Proxy is a .NET app and needs ICU for globalization support
    if ! ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so'; then
        apt_update
        local icu_package
        icu_package="$(apt-cache pkgnames libicu 2>/dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1 || true)"
        if [ -z "${icu_package}" ]; then
            fail "Couldn't find a libicu package to install. Dev Proxy requires ICU."
        fi
        apt_install "${icu_package}"
    fi
}

# --- Dev Proxy --------------------------------------------------------------

download_url() {
    echo "${REPO_URL}/releases/download/$1/dev-proxy-linux-${ARCH}-$1.zip"
}

resolve_version() {
    local tag
    case "${VERSION}" in
        "" | latest)
            echo "Resolving the latest Dev Proxy version..." >&2
            tag="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "${REPO_URL}/releases/latest")"
            tag="${tag##*/}"
            ;;
        beta)
            echo "Resolving the latest Dev Proxy beta version..." >&2
            # A tag can show up before its release assets are uploaded, so
            # pick the newest beta that has a download for this architecture.
            local candidates candidate
            candidates="$(curl -fsSL "${REPO_URL}/releases.atom" | grep -oE '/releases/tag/v[0-9]+\.[0-9]+\.[0-9]+-beta\.[0-9]+' | sed 's|.*/||' || true)"
            if [ -z "${candidates}" ]; then
                candidates="$(curl -fsSL "https://api.github.com/repos/dotnet/dev-proxy/releases?per_page=50" | grep -oE '"tag_name": *"v[^"]+-beta[^"]*"' | sed -E 's/.*"(v[^"]+)"$/\1/' || true)"
            fi
            tag=""
            for candidate in ${candidates}; do
                if curl -fsSLI -o /dev/null "$(download_url "${candidate}")"; then
                    tag="${candidate}"
                    break
                fi
            done
            ;;
        v*) tag="${VERSION}" ;;
        *) tag="v${VERSION}" ;;
    esac

    if ! [[ "${tag}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$ ]]; then
        fail "Couldn't resolve Dev Proxy version '${VERSION}'. Use 'latest', 'beta', or a version like '2.2.0'."
    fi
    echo "${tag}"
}

install_devproxy() {
    local tag="$1"
    local version_file="${INSTALL_DIR}/.feature-version"

    if [ -f "${version_file}" ] && [ "$(cat "${version_file}")" = "${tag}" ]; then
        echo "Dev Proxy ${tag} is already installed in ${INSTALL_DIR}."
        return
    fi

    local url
    url="$(download_url "${tag}")"
    local tmp_dir
    tmp_dir="$(mktemp -d)"

    echo "Downloading Dev Proxy ${tag} (linux-${ARCH})..."
    if ! curl -fsSL --retry 3 -o "${tmp_dir}/devproxy.zip" "${url}"; then
        rm -rf "${tmp_dir}"
        fail "Couldn't download Dev Proxy ${tag} from ${url}. Check that the version exists."
    fi

    unzip -q "${tmp_dir}/devproxy.zip" -d "${tmp_dir}/devproxy"
    rm -rf "${INSTALL_DIR}"
    mkdir -p "$(dirname "${INSTALL_DIR}")"
    mv "${tmp_dir}/devproxy" "${INSTALL_DIR}"
    rm -rf "${tmp_dir}"
    echo "${tag}" > "${version_file}"
}

link_devproxy() {
    local binary=devproxy
    if [ -f "${INSTALL_DIR}/devproxy-beta" ]; then
        binary=devproxy-beta
    fi
    if [ ! -f "${INSTALL_DIR}/${binary}" ]; then
        fail "Couldn't find the Dev Proxy executable in ${INSTALL_DIR}."
    fi

    chmod -R a+rX "${INSTALL_DIR}"
    chmod +x "${INSTALL_DIR}/${binary}"
    if [ -f "${INSTALL_DIR}/libe_sqlite3.so" ]; then
        chmod +x "${INSTALL_DIR}/libe_sqlite3.so"
    fi

    rm -f "${BIN_DIR}/devproxy" "${BIN_DIR}/devproxy-beta"
    ln -s "${INSTALL_DIR}/${binary}" "${BIN_DIR}/devproxy"
    if [ "${binary}" = devproxy-beta ]; then
        ln -s "${INSTALL_DIR}/${binary}" "${BIN_DIR}/devproxy-beta"
    fi
}

verify_devproxy() {
    # Some versions create a root certificate on any run. Use a throwaway home
    # so that no state (like a root certificate) ends up in the image. .NET
    # resolves the config folder to an empty (relative) path if it doesn't
    # exist, so create it and run from the throwaway home.
    local tmp_home
    tmp_home="$(mktemp -d)"
    mkdir -p "${tmp_home}/.config" "${tmp_home}/.local/share"
    local installed_version
    if ! installed_version="$(cd "${tmp_home}" && HOME="${tmp_home}" XDG_CONFIG_HOME="${tmp_home}/.config" XDG_DATA_HOME="${tmp_home}/.local/share" DEV_PROXY_CERT_PATH="${tmp_home}/rootCert.pfx" "${BIN_DIR}/devproxy" --version </dev/null 2>/dev/null | tail -n 1)"; then
        rm -rf "${tmp_home}"
        fail "Dev Proxy was installed but couldn't run. Run 'devproxy --version' in the container for details."
    fi
    rm -rf "${tmp_home}"
    echo "Installed Dev Proxy ${installed_version}"
}

# --- Remote user and certificate trust --------------------------------------

prepare_config_dir() {
    if [ -z "${REMOTE_USER_HOME}" ] || [ ! -d "${REMOTE_USER_HOME}" ]; then
        echo "Home directory for '${REMOTE_USER}' not found. Skipping config directory setup."
        return
    fi

    local config_root="${REMOTE_USER_HOME}/.config"
    if [ ! -d "${config_root}" ]; then
        mkdir -p "${config_root}"
        chown "${REMOTE_USER}:${REMOTE_USER_GROUP}" "${config_root}"
    fi
    mkdir -p "${config_root}/dev-proxy"
    chown -R "${REMOTE_USER}:${REMOTE_USER_GROUP}" "${config_root}/dev-proxy"
}

install_scripts() {
    mkdir -p "${SHARE_DIR}"
    install -o root -g root -m 0755 "${FEATURE_DIR}/scripts/post-create.sh" "${SHARE_DIR}/post-create.sh"
    cat > "${SHARE_DIR}/feature.env" <<EOF
DEVPROXY_FEATURE_TRUST_CERTIFICATE=${TRUST_CERTIFICATE}
EOF
    chmod 0644 "${SHARE_DIR}/feature.env"

    rm -f "${SUDOERS_FILE}"

    if [ "${TRUST_CERTIFICATE}" != true ]; then
        rm -f "${TRUST_HELPER}"
        return
    fi

    install -o root -g root -m 0755 "${FEATURE_DIR}/scripts/trust-cert.sh" "${TRUST_HELPER}"

    if [ "${REMOTE_USER}" != root ]; then
        mkdir -p /etc/sudoers.d
        echo "${REMOTE_USER} ALL=(root) NOPASSWD: ${TRUST_HELPER}" > "${SUDOERS_FILE}"
        chmod 0440 "${SUDOERS_FILE}"
        if command -v visudo >/dev/null 2>&1 && ! visudo -cf "${SUDOERS_FILE}" >/dev/null; then
            rm -f "${SUDOERS_FILE}"
            fail "Couldn't configure sudo for the Dev Proxy certificate helper."
        fi
    fi
}

# --- Main -------------------------------------------------------------------

echo "Installing Dev Proxy (version: ${VERSION}, trustCertificate: ${TRUST_CERTIFICATE}, user: ${REMOTE_USER})..."

install_dependencies
TAG="$(resolve_version)"
install_devproxy "${TAG}"
link_devproxy
verify_devproxy
prepare_config_dir
install_scripts

if [ "${APT_UPDATED}" = true ]; then
    rm -rf /var/lib/apt/lists/*
fi

echo "Done. Dev Proxy is available as 'devproxy'."
if [ "${TRUST_CERTIFICATE}" = true ]; then
    echo "The Dev Proxy root certificate will be generated and trusted after the container is created."
fi
