# CI/CD Integration

Dev Proxy runs headlessly in CI/CD pipelines to automate API testing — catching shadow APIs, verifying permissions, ensuring production-ready API versions, and validating resilience.

## GitHub Actions (Recommended)

Use the official [Dev Proxy Actions](https://github.com/marketplace/actions/dev-proxy-actions) for the simplest integration.

### Basic Setup

```yaml
steps:
  - uses: dev-proxy-tools/actions/setup@v1

  - uses: dev-proxy-tools/actions/start@v1
    with:
      auto-record: true
      config-file: .devproxy/devproxyrc.json

  # Run your tests here
  - run: npm test

  - uses: dev-proxy-tools/actions/stop@v1

  - uses: actions/upload-artifact@v4
    with:
      name: dev-proxy-reports
      path: ./*Reporter*
```

### Action Reference

| Action | Purpose |
|--------|---------|
| `dev-proxy-tools/actions/setup@v1` | Install and optionally start Dev Proxy |
| `dev-proxy-tools/actions/start@v1` | Start Dev Proxy (after setup with `auto-start: false`) |
| `dev-proxy-tools/actions/stop@v1` | Stop Dev Proxy and trigger report generation |
| `dev-proxy-tools/actions/record-start@v1` | Start recording manually |
| `dev-proxy-tools/actions/record-stop@v1` | Stop recording manually |

### Setup Action Inputs

| Input | Default | Description |
|-------|---------|-------------|
| `version` | latest | Specific Dev Proxy version to install |
| `auto-start` | `true` | Start Dev Proxy after installation |
| `auto-stop` | `true` | Stop Dev Proxy when job completes |
| `auto-record` | `false` | Start recording immediately |
| `config-file` | `devproxyrc.json` | Path to configuration file |
| `log-file` | `devproxy.log` | Path to log file |

### Outputs

| Output | Description |
|--------|-------------|
| `proxy-url` | URL of the running Dev Proxy instance |
| `api-url` | URL of the Dev Proxy API |

Access via `${{ steps.<step-id>.outputs.proxy-url }}`.

### Pin Version

```yaml
- uses: dev-proxy-tools/actions/setup@v1
  with:
    version: 2.4.0
```

### Multiple Recording Sessions

```yaml
- uses: dev-proxy-tools/actions/start@v1
  with:
    auto-record: false

- uses: dev-proxy-tools/actions/record-start@v1
- run: npm run test:api-calls
- uses: dev-proxy-tools/actions/record-stop@v1

- uses: dev-proxy-tools/actions/record-start@v1
- run: npm run test:graph-calls
- uses: dev-proxy-tools/actions/record-stop@v1

- uses: dev-proxy-tools/actions/stop@v1
```

### Shadow API Detection Workflow

```yaml
name: Check Shadow APIs
on: [pull_request]

jobs:
  shadow-api-check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Azure Login
        uses: azure/login@v2
        with:
          creds: ${{ secrets.AZURE_CREDENTIALS }}

      - uses: dev-proxy-tools/actions/setup@v1
        with:
          version: 2.4.0

      - id: start-proxy
        uses: dev-proxy-tools/actions/start@v1
        with:
          auto-record: true
          config-file: .devproxy/shadow-apis.json

      - run: npm test
        env:
          http_proxy: ${{ steps.start-proxy.outputs.proxy-url }}
          https_proxy: ${{ steps.start-proxy.outputs.proxy-url }}

      - uses: dev-proxy-tools/actions/stop@v1

      - uses: actions/upload-artifact@v4
        with:
          name: shadow-api-report
          path: ./*Reporter*
```

### Graph Permissions Check Workflow

```yaml
name: Check Graph Permissions
on: [pull_request]

jobs:
  permissions-check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: dev-proxy-tools/actions/setup@v1
      - uses: dev-proxy-tools/actions/start@v1
        with:
          auto-record: true
          config-file: .devproxy/graph-permissions.json
      - run: npm test
      - uses: dev-proxy-tools/actions/stop@v1
      - uses: actions/upload-artifact@v4
        with:
          name: graph-permissions-report
          path: ./*Reporter*
```

### LLM Usage Cost Tracking Workflow

```yaml
name: Track LLM Costs
on: [push]

jobs:
  llm-cost-tracking:
    runs-on: ubuntu-latest
    services:
      aspire-dashboard:
        image: mcr.microsoft.com/dotnet/aspire-dashboard:latest
        ports:
          - 18888:18888
          - 4318:18889
    steps:
      - uses: actions/checkout@v4
      - uses: dev-proxy-tools/actions/setup@v1
      - uses: dev-proxy-tools/actions/start@v1
        with:
          config-file: .devproxy/llm-telemetry.json
      - run: npm test
      - uses: dev-proxy-tools/actions/stop@v1
```

## Azure Pipelines

No dedicated actions — use script tasks with the Dev Proxy API.

### Install and Cache

```yaml
variables:
  - name: DEV_PROXY_VERSION
    value: v2.4.0

steps:
  - task: Cache@2
    inputs:
      key: '"dev-proxy-$(DEV_PROXY_VERSION)"'
      path: ./devproxy
      cacheHitVar: DEV_PROXY_CACHE_RESTORED
    displayName: Cache Dev Proxy

  - script: bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)" $DEV_PROXY_VERSION
    displayName: Install Dev Proxy
    condition: ne(variables.DEV_PROXY_CACHE_RESTORED, 'true')
```

### Start, Test, Stop

```yaml
  - script: bash ./start.sh
    displayName: Start Dev Proxy

  - script: npm test
    displayName: Run tests

  - script: curl -X POST http://localhost:8897/proxy/stopProxy
    displayName: Stop Dev Proxy

  - script: |
      while true; do
        if grep -q -e "DONE" -e "No requests to process" devproxy.log; then break; fi
        sleep 1
      done
    displayName: Wait for reports

  - task: PublishPipelineArtifact@1
    inputs:
      targetPath: ./*Reporter*
      artifact: Reports
```

### Full Azure Pipelines Example

```yaml
trigger:
  - main

pool:
  vmImage: ubuntu-latest

variables:
  - name: DEV_PROXY_VERSION
    value: v2.4.0
  - name: LOG_FILE
    value: devproxy.log

steps:
  - task: Cache@2
    inputs:
      key: '"dev-proxy-$(DEV_PROXY_VERSION)"'
      path: ./devproxy
      cacheHitVar: DEV_PROXY_CACHE_RESTORED
    displayName: Cache Dev Proxy

  - script: bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)" $(DEV_PROXY_VERSION)
    displayName: Install Dev Proxy
    condition: ne(variables.DEV_PROXY_CACHE_RESTORED, 'true')

  - script: |
      mkdir -p ~/.config/dev-proxy
      ./devproxy/devproxy --config-file .devproxy/config.json > $(LOG_FILE) 2>&1 &
      echo "Waiting for Dev Proxy to start..."
      while true; do
        if grep -q "Dev Proxy listening on" $(LOG_FILE); then break; fi
        sleep 1
      done
      echo "Exporting root certificate"
      openssl pkcs12 -in ~/.config/dev-proxy/rootCert.pfx -clcerts -nokeys -out dev-proxy-ca.crt -passin pass:""
      sudo cp dev-proxy-ca.crt /usr/local/share/ca-certificates/
      sudo update-ca-certificates
    displayName: Start Dev Proxy

  - script: curl -X POST http://localhost:8897/proxy -H "Content-Type: application/json" -d '{"recording": true}'
    displayName: Start recording

  - script: npm test
    displayName: Run tests
    env:
      http_proxy: http://127.0.0.1:8000
      https_proxy: http://127.0.0.1:8000

  - script: curl -X POST http://localhost:8897/proxy -H "Content-Type: application/json" -d '{"recording": false}'
    displayName: Stop recording

  - script: curl -X POST http://localhost:8897/proxy/stopProxy
    displayName: Stop Dev Proxy

  - script: |
      while true; do
        if grep -q -e "DONE" -e "No requests to process" -e "An error occurred in a plugin" $(LOG_FILE); then break; fi
        sleep 1
      done
    displayName: Wait for completion

  - task: PublishPipelineArtifact@1
    displayName: Upload reports
    inputs:
      targetPath: ./*Reporter*
      artifact: Reports
    condition: always()
```

## General CI/CD Pattern

For any CI system, follow these steps:

### 1. Install with Pinned Version

```bash
bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)" -- v2.4.0
```

### 2. Start in Background with Logging

```bash
mkdir -p ~/.config/dev-proxy
./devproxy/devproxy > devproxy.log 2>&1 &
```

### 3. Wait for Startup

```bash
while true; do
  if grep -q "Dev Proxy listening on" devproxy.log; then break; fi
  sleep 1
done
```

### 4. Trust the Root Certificate

```bash
openssl pkcs12 -in ~/.config/dev-proxy/rootCert.pfx -clcerts -nokeys -out dev-proxy-ca.crt -passin pass:""
sudo cp dev-proxy-ca.crt /usr/local/share/ca-certificates/
sudo update-ca-certificates
```

### 5. Set Proxy Environment Variables

```bash
export http_proxy=http://127.0.0.1:8000
export https_proxy=http://127.0.0.1:8000
```

### 6. Control via API

```bash
# Start recording
curl -X POST http://localhost:8897/proxy -H "Content-Type: application/json" -d '{"recording": true}'
# Run tests
npm test
# Stop recording
curl -X POST http://localhost:8897/proxy -H "Content-Type: application/json" -d '{"recording": false}'
# Stop proxy
curl -X POST http://localhost:8897/proxy/stopProxy
```

### 7. Wait for Completion

```bash
while true; do
  if grep -q -e "DONE" -e "No requests to process" -e "An error occurred in a plugin" devproxy.log; then break; fi
  sleep 1
done
```

## Dev Proxy API Reference

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/proxy` | GET | Get proxy status (`recording`, `configFile`) |
| `/proxy` | POST | Start/stop recording: `{"recording": true/false}` |
| `/proxy/stopProxy` | POST | Graceful shutdown |
| `/proxy/jwtToken` | POST | Generate JWT for testing |
| `/proxy/mockRequest` | POST | Trigger mock request (equivalent of pressing `w`) |
| `/proxy/rootCertificate?format=crt` | GET | Download root cert in PEM format |

Swagger: `http://localhost:8897/swagger`

## Environment Variables for Azure API Center

Azure API Center plugins support `@ENV_VAR` syntax for CI/CD:

```json
{
  "apiCenterOnboardingPlugin": {
    "subscriptionId": "@AZURE_SUBSCRIPTION_ID",
    "resourceGroupName": "@AZURE_RESOURCE_GROUP_NAME",
    "serviceName": "@AZURE_APIC_INSTANCE_NAME"
  }
}
```

## Start Script Template (start.sh)

```bash
#!/bin/bash
set -e

LOG_FILE=${LOG_FILE:-devproxy.log}
CONFIG_FILE=${CONFIG_FILE:-.devproxy/devproxyrc.json}

mkdir -p ~/.config/dev-proxy
./devproxy/devproxy --config-file "$CONFIG_FILE" > "$LOG_FILE" 2>&1 &

while true; do
  if grep -q "Dev Proxy listening on" "$LOG_FILE"; then break; fi
  sleep 1
done

openssl pkcs12 -in ~/.config/dev-proxy/rootCert.pfx -clcerts -nokeys -out dev-proxy-ca.crt -passin pass:""
sudo cp dev-proxy-ca.crt /usr/local/share/ca-certificates/
sudo update-ca-certificates

export http_proxy=http://127.0.0.1:8000
export https_proxy=http://127.0.0.1:8000
echo "Dev Proxy is ready"
```

## Stop and Wait Script Template (stop.sh)

```bash
#!/bin/bash
set -e

LOG_FILE=${LOG_FILE:-devproxy.log}

curl -s -X POST http://localhost:8897/proxy/stopProxy

while true; do
  if grep -q -e "DONE" -e "No requests to process" -e "An error occurred in a plugin" "$LOG_FILE"; then break; fi
  sleep 1
done
echo "Dev Proxy stopped"
```
