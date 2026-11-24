#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Specific version with the 'v' prefix, as root.

set -e

source dev-container-features-test-lib
source "$(dirname "$0")/common.sh"

check "installs Dev Proxy 2.2.0" bash -c "devproxy --version | grep -q '^2.2.0'"
check "root certificate is in the system store" test -f "${DEVPROXY_TEST_CERT}"
check "trusted certificate is the user's root certificate" devproxy_cert_matches_profile
check "root certificate is in the CA bundle" devproxy_cert_in_bundle
check "no certificate outside the user's profile" devproxy_no_stray_certs "$(devproxy_config_dir)"
check "start Dev Proxy" devproxy_start
check "curl trusts Dev Proxy" devproxy_curl
devproxy_stop

reportResults
