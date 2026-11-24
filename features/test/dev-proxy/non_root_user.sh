#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Non-root remote user without sudo access.

set -e

source dev-container-features-test-lib
source "$(dirname "$0")/common.sh"

check "runs as devuser" bash -c "[ \"\$(id -un)\" = devuser ]"
check "devproxy runs" devproxy --version
check "config folder is owned by the user" bash -c "[ \"\$(stat -c %U \"$(devproxy_config_dir)\")\" = devuser ]"
check "user can't run arbitrary commands with sudo" bash -c "! sudo -n true 2>/dev/null"
check "user can run the certificate helper with sudo" bash -c "sudo -n -l /usr/local/share/devproxy/trust-cert.sh"
check "certificate helper is owned by root" bash -c "[ \"\$(stat -c %U:%a /usr/local/share/devproxy/trust-cert.sh)\" = root:755 ]"
check "certificate helper rejects non-certificates" bash -c "! echo not-a-cert | sudo -n /usr/local/share/devproxy/trust-cert.sh"
check "root certificate is in the system store" test -f "${DEVPROXY_TEST_CERT}"
check "trusted certificate is the user's root certificate" devproxy_cert_matches_profile
check "root certificate is in the CA bundle" devproxy_cert_in_bundle
check "no certificate outside the user's profile" devproxy_no_stray_certs "$(devproxy_config_dir)"
check "start Dev Proxy" devproxy_start
check "curl trusts Dev Proxy" devproxy_curl
devproxy_stop

reportResults
