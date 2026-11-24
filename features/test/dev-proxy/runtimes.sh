#!/bin/bash
# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.

# Node.js, Python, and .NET apps trust Dev Proxy without extra configuration.

set -e

source dev-container-features-test-lib
source "$(dirname "$0")/common.sh"

node_request() {
    local body
    # NODE_USE_ENV_PROXY makes fetch use HTTPS_PROXY (Node.js 24+). Node.js
    # doesn't use the system store, so this relies on NODE_EXTRA_CA_CERTS.
    body="$(NODE_USE_ENV_PROXY=1 HTTPS_PROXY="${DEVPROXY_TEST_PROXY}" node -e '
fetch(process.env.DEVPROXY_TEST_URL)
  .then(r => r.text())
  .then(t => console.log(t))
  .catch(e => { console.error(String(e.cause || e)); process.exit(1); });
')"
    devproxy_is_mock_body "${body}"
}

python_urllib_request() {
    local body
    body="$(python3 - <<'PYTHON'
import os, urllib.request
proxy = os.environ["DEVPROXY_TEST_PROXY"]
opener = urllib.request.build_opener(urllib.request.ProxyHandler({"https": proxy}))
print(opener.open(os.environ["DEVPROXY_TEST_URL"]).read().decode())
PYTHON
)"
    devproxy_is_mock_body "${body}"
}

python_requests_request() {
    local body
    body="$(/tmp/devproxy-venv/bin/python - <<'PYTHON'
import os, requests
proxy = os.environ["DEVPROXY_TEST_PROXY"]
print(requests.get(os.environ["DEVPROXY_TEST_URL"], proxies={"https": proxy}).text)
PYTHON
)"
    devproxy_is_mock_body "${body}"
}

dotnet_request() {
    local body
    body="$(cd /tmp/devproxy-dotnet && HTTPS_PROXY="${DEVPROXY_TEST_PROXY}" dotnet run --no-build app.cs)"
    devproxy_is_mock_body "${body}"
}

export DEVPROXY_TEST_URL DEVPROXY_TEST_PROXY DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Prepare the clients before starting Dev Proxy
python3 -m venv /tmp/devproxy-venv
/tmp/devproxy-venv/bin/pip install -q requests
mkdir -p /tmp/devproxy-dotnet
cat > /tmp/devproxy-dotnet/app.cs <<'CSHARP'
using var client = new HttpClient();
Console.WriteLine(await client.GetStringAsync(Environment.GetEnvironmentVariable("DEVPROXY_TEST_URL")));
CSHARP
(cd /tmp/devproxy-dotnet && dotnet build app.cs >/dev/null)

check "root certificate is in the system store" test -f "${DEVPROXY_TEST_CERT}"
check "trusted certificate is the user's root certificate" devproxy_cert_matches_profile
check "root certificate is in the CA bundle" devproxy_cert_in_bundle
check "no certificate outside the user's profile" devproxy_no_stray_certs "$(devproxy_config_dir)"
check "start Dev Proxy" devproxy_start
check "curl trusts Dev Proxy" devproxy_curl
check "Node.js trusts Dev Proxy" node_request
check "Python (urllib) trusts Dev Proxy" python_urllib_request
check "Python (requests) trusts Dev Proxy" python_requests_request
check ".NET trusts Dev Proxy" dotnet_request
devproxy_stop

reportResults
