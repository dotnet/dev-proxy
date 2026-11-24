#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Default options, run against several base images.

set -e

source dev-container-features-test-lib
source "$(dirname "$0")/common.sh"

check "devproxy is on PATH" bash -c "command -v devproxy"
check "devproxy runs" devproxy --version
check "devproxy is installed in /opt/devproxy" test -L /usr/local/bin/devproxy -a -d /opt/devproxy
check "config folder is owned by the user" bash -c "[ \"\$(stat -c %U \"$(devproxy_config_dir)\")\" = \"\$(id -un)\" ]"
check "root certificate is in the system store" test -f "${DEVPROXY_TEST_CERT}"
check "trusted certificate is the user's root certificate" devproxy_cert_matches_profile
check "root certificate is in the CA bundle" devproxy_cert_in_bundle
check "root certificate is only readable by the user" bash -c "[ \"\$(stat -c %a \"$(devproxy_config_dir)/rootCert.pfx\")\" = 600 ]"
check "no certificate outside the user's profile" devproxy_no_stray_certs "$(devproxy_config_dir)"
check "NODE_EXTRA_CA_CERTS is set" test -f "${NODE_EXTRA_CA_CERTS}"
check "SSL_CERT_FILE is set" test -f "${SSL_CERT_FILE}"
check "REQUESTS_CA_BUNDLE is set" test -f "${REQUESTS_CA_BUNDLE}"

check "start Dev Proxy" devproxy_start
check "curl trusts Dev Proxy" devproxy_curl
devproxy_stop

reportResults
