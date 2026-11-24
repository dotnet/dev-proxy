# Dev Proxy (dev-proxy)

Installs [Dev Proxy](https://aka.ms/devproxy), an API simulator that helps you test how your app handles API failures, rate limits, latency, and more. Trusts a per-container Dev Proxy root certificate system-wide.

## Example Usage

```json
"features": {
    "ghcr.io/dotnet/dev-proxy/dev-proxy:1": {}
}
```

## Options

| Options Id | Description | Type | Default Value |
|-----|-----|-----|-----|
| version | Dev Proxy version to install: 'latest', 'beta', or a specific version like '2.2.0' or 'v2.2.0'. | string | latest |
| trustCertificate | After the container is created, generate a Dev Proxy root certificate unique to the container and trust it system-wide and for common runtimes. | boolean | true |

## Customizations

### VS Code Extensions

- `garrytrinder.dev-proxy-toolkit`

## What the Feature does

When the image is built, the Feature:

- Installs Dev Proxy in `/opt/devproxy` and adds the `devproxy` command to `/usr/local/bin`. For beta versions, `devproxy-beta` is available too.
- Installs the required dependencies (`curl`, `unzip`, `ca-certificates`, `openssl`, ICU) if they're missing.
- Creates the Dev Proxy configuration folder (`~/.config/dev-proxy`) for the remote user.
- If `trustCertificate` is `true`, installs a root-owned helper that adds the Dev Proxy root certificate to the system certificate store, and allows the remote user to run it with `sudo` without a password.

The Feature doesn't generate the Dev Proxy root certificate when building the image. If it did, the certificate and its private key would end up in an image layer, and everyone using the same image or Codespaces prebuild would share the same trusted CA.

Instead, after the container is created (`postCreateCommand`), the Feature:

1. Runs `devproxy cert ensure` as the remote user to generate a root certificate unique to the container.
2. Adds the certificate to the system certificate store (`/usr/local/share/ca-certificates` and `update-ca-certificates`), so that curl, .NET, and other tools that use the system store trust it.

Some runtimes don't use the system certificate store, so the Feature points them at the system CA bundle (`/etc/ssl/certs/ca-certificates.crt`):

| Variable | Used by |
|----------|---------|
| `NODE_EXTRA_CA_CERTS` | Node.js |
| `REQUESTS_CA_BUNDLE` | Python `requests` |
| `SSL_CERT_FILE` | Python and other OpenSSL-based tools |

Java keystores aren't supported.

Python 3.13 and newer enable strict certificate verification (`VERIFY_X509_STRICT`), which rejects the certificates that Dev Proxy v3 and earlier generate for intercepted hosts. Use Dev Proxy v4 or newer (`"version": "beta"` until v4 is released) to call APIs through Dev Proxy from Python 3.13+.

## Using Dev Proxy

The Feature doesn't start Dev Proxy or set `HTTP_PROXY`/`HTTPS_PROXY` for the whole container. Container-wide proxy variables would route package managers, git, and VS Code's own traffic through Dev Proxy, which breaks them when Dev Proxy isn't running.

Start Dev Proxy with your configuration, and configure the app you're testing to use it:

```bash
devproxy --config-file .devproxy/devproxyrc.json
```

```bash
HTTPS_PROXY=http://127.0.0.1:8000 npm start
```

## Not installing the Dev Proxy Toolkit

The Feature adds the [Dev Proxy Toolkit](https://marketplace.visualstudio.com/items?itemName=garrytrinder.dev-proxy-toolkit) VS Code extension. To skip it, exclude it in your `devcontainer.json`:

```json
"customizations": {
    "vscode": {
        "extensions": ["-garrytrinder.dev-proxy-toolkit"]
    }
}
```

## OS support

The Feature supports Debian and Ubuntu-based images on x64 and arm64. Alpine and other musl-based images aren't supported.

