#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Latest beta version.

set -e

source dev-container-features-test-lib
source "$(dirname "$0")/common.sh"

check "installs a beta version" bash -c "devproxy --version | grep -q -- '-beta'"
check "devproxy-beta is on PATH" bash -c "command -v devproxy-beta"
check "devproxy and devproxy-beta point to the same executable" bash -c "[ \"\$(readlink -f \"\$(command -v devproxy)\")\" = \"\$(readlink -f \"\$(command -v devproxy-beta)\")\" ]"
check "root certificate is in the system store" test -f "${DEVPROXY_TEST_CERT}"
check "trusted certificate is the user's root certificate" devproxy_cert_matches_profile
check "root certificate is in the CA bundle" devproxy_cert_in_bundle
check "no certificate outside the user's profile" devproxy_no_stray_certs "$(devproxy_config_dir)"
check "start Dev Proxy" devproxy_start
check "curl trusts Dev Proxy" devproxy_curl
devproxy_stop

reportResults
