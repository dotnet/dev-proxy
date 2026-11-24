#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# trustCertificate disabled: Dev Proxy is installed, but no certificate is
# generated or trusted, and no sudo access is granted.

set -e

source dev-container-features-test-lib
source "$(dirname "$0")/common.sh"

check "devproxy runs" devproxy --version
check "no root certificate is generated" devproxy_no_stray_certs
check "no certificate in the system store" test ! -e "${DEVPROXY_TEST_CERT}"
check "certificate helper isn't installed" test ! -e /usr/local/share/devproxy/trust-cert.sh
check "no sudo rule is added" test ! -e /etc/sudoers.d/devproxy

reportResults
