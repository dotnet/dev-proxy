# Installing Dev Proxy

## Check if Dev Proxy is Installed

Run `devproxy --version` to check if Dev Proxy is already installed. If the command succeeds, Dev Proxy is ready to use — skip the rest of this document.

## Install Dev Proxy

### macOS (Homebrew — Recommended)

```bash
brew tap dotnet/dev-proxy
brew install dev-proxy
```

### macOS (Manual)

1. [Download](https://aka.ms/devproxy/download/) the latest release and extract into a folder (e.g., `~/devproxy`).
2. Add to PATH in `~/.zshrc`:
   ```bash
   export PATH=".:$PATH:$HOME/devproxy"
   ```
3. Reload: `source ~/.zshrc`

### Windows (winget — Recommended)

```console
winget install DevProxy.DevProxy --silent
```

Restart the command prompt after installation to refresh PATH.

### Windows (Manual)

1. [Download](https://aka.ms/devproxy/download/) the latest release and extract into a folder (e.g., `%USERPROFILE%\devproxy`).
2. Add the folder to your user PATH:
   - Open Start → "Edit environment variables for your account"
   - Edit `Path` → New → `%USERPROFILE%\devproxy` → OK

### Linux (Setup Script — Recommended)

```bash
bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)"
```

Or with PowerShell:

```powershell
(Invoke-WebRequest https://aka.ms/devproxy/setup.ps1).Content | Invoke-Expression
```

### Linux (Manual)

1. [Download](https://aka.ms/devproxy/download/) the latest release and extract into a folder (e.g., `~/devproxy`).
2. Add to PATH in `~/.bashrc`:
   ```bash
   export PATH=".:$PATH:$HOME/devproxy"
   ```
3. Reload: `source ~/.bashrc`

### Install a Specific Version

Pass the version to the setup script:

```bash
bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)" -- v2.4.0
```

With winget:

```console
winget install DevProxy.DevProxy --version 2.4.0 --silent
```

## First Run Setup

The first time Dev Proxy starts, it requires certificate trust and (on some platforms) firewall access. These steps only need to be done once.

### macOS

1. Run `devproxy` and press Enter.
2. When prompted to trust the "Dev Proxy CA" certificate, press `y`.
3. When prompted to allow incoming connections, select "Allow".

### Windows

1. Run `devproxy` and press Enter.
2. When the certificate warning appears for "Dev Proxy CA", select "Yes" to install.
3. When the Windows Firewall warning appears, select "Allow access".

### Linux (Ubuntu)

1. Run `devproxy` and press Enter.
2. In a separate terminal, trust the certificate:

```bash
# Export Dev Proxy root certificate
openssl pkcs12 -in ~/.config/dev-proxy/rootCert.pfx -clcerts -nokeys -out dev-proxy-ca.crt -passin pass:""
# Install the certificate
sudo cp dev-proxy-ca.crt /usr/local/share/ca-certificates/
# Update certificates
sudo update-ca-certificates
```

For non-Ubuntu distributions, the certificate trust steps may differ.

## Verify Installation

After first-run setup, verify Dev Proxy intercepts requests:

```bash
curl -ikx http://localhost:8000 https://jsonplaceholder.typicode.com/posts
```

Dev Proxy output should show the intercepted request:

```text
 req   ╭ GET https://jsonplaceholder.typicode.com/posts
 time  │ ...
 ...   ╰ ...
```

## Stop Dev Proxy

Always stop Dev Proxy safely with Ctrl+C. Do not close the terminal directly — Dev Proxy needs to unregister as the system proxy.

## About the Certificate

Dev Proxy uses a local SSL certificate to decrypt HTTPS traffic only on your machine. The certificate is stored locally and Dev Proxy does not upload any data to Microsoft.
