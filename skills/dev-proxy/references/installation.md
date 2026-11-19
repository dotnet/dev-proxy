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
   export PATH="$PATH:$HOME/devproxy"
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
   export PATH="$PATH:$HOME/devproxy"
   ```
3. Reload: `source ~/.bashrc`

### Dev Containers and GitHub Codespaces

Add the Dev Proxy Feature to `devcontainer.json`:

```json
"features": {
  "ghcr.io/dotnet/dev-proxy/dev-proxy:1": {}
}
```

The Feature installs Dev Proxy, and after the container is created, generates a root certificate unique to the container and trusts it system-wide (including Node.js and Python). No first run setup is needed. Use the `version` option to install a specific version (`"version": "4.0.0"`) or the latest beta (`"version": "beta"`).

### Install a Specific Version

Pass the version to the setup script:

```bash
bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)" -- v4.0.0
```

With winget:

```console
winget install DevProxy.DevProxy --version 4.0.0 --silent
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

### Linux

Trust the certificate (requires sudo, so Dev Proxy doesn't do it automatically on first run):

```bash
devproxy cert ensure
```

This adds the certificate to the system certificate store on Debian/Ubuntu (`update-ca-certificates`), Fedora/RHEL and Arch (`update-ca-trust`). If `certutil` is installed (`libnss3-tools` on Debian/Ubuntu, `nss-tools` on Fedora/RHEL, `nss` on Arch), it also adds it to the NSS database used by Chrome (`~/.pki/nssdb` if it exists, otherwise `~/.local/share/pki/nssdb`). Without a terminal, if sudo needs a password, `cert ensure` creates the certificate but skips trusting it and shows a warning. If the system store is trusted but the NSS step fails (for example, the database has a password), it shows a warning. To undo, run `devproxy cert remove`, which also removes the certificate from both NSS databases. If it can't update the system store (for example, sudo needs a password and there's no terminal) or delete the certificate from an NSS database, it exits with an error.

On other distributions, `cert ensure` saves the public certificate to `devproxy-ca.crt` in the current directory; add it to your system's CA anchors. To do it fully by hand, for example on Ubuntu:

```bash
# Export Dev Proxy root certificate
openssl pkcs12 -in ~/.config/dev-proxy/rootCert.pfx -clcerts -nokeys -out dev-proxy-ca.crt -passin pass:""
# Install the certificate
sudo cp dev-proxy-ca.crt /usr/local/share/ca-certificates/
# Update certificates
sudo update-ca-certificates
```

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
