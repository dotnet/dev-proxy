<h1 align="center">
    <img alt="Dev Proxy" src="./samples/img/devproxy.png" width="125" />
  <br>Dev Proxy<br>
</h1>

<h4 align="center">
  Test the untestable
</h4>
 
<p align="center">
    <a href="https://aka.ms/devproxy/download">
        <img alt="Download Now" src="https://img.shields.io/badge/download-now-green?style=for-the-badge">
    </a>
</p>
<p align="center">
    <a href="https://aka.ms/devproxy/discord">
        <img alt="Discord" src="https://img.shields.io/badge/discord-chat-green?style=for-the-badge&logo=discord">
    </a>
</p>

<p align="center">
  <a href="#get-started">Get started</a> |
  <a href="https://aka.ms/devproxy/docs">Documentation</a>
</p>

<p align="center">
  <a href="#example">Example</a> |
  <a href="#features">Features</a> |
  <a href="#trademarks">Trademarks</a>
</p>

<p align="center">
    <details class="details-reset border rounded-2" open="">
  <summary class="px-3 py-2">
    <svg aria-hidden="true" height="16" viewBox="0 0 16 16" version="1.1" width="16" data-view-component="true" class="octicon octicon-device-camera-video">
    <path d="M16 3.75v8.5a.75.75 0 0 1-1.136.643L11 10.575v.675A1.75 1.75 0 0 1 9.25 13h-7.5A1.75 1.75 0 0 1 0 11.25v-6.5C0 3.784.784 3 1.75 3h7.5c.966 0 1.75.784 1.75 1.75v.675l3.864-2.318A.75.75 0 0 1 16 3.75Zm-6.5 1a.25.25 0 0 0-.25-.25h-7.5a.25.25 0 0 0-.25.25v6.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-6.5ZM11 8.825l3.5 2.1v-5.85l-3.5 2.1Z"></path>
</svg>
    <span aria-label="" class="m-1">📽️ Simulate throttling using Dev Proxy</span>
    <span class="dropdown-caret"></span>
  </summary>

  <video src="https://user-images.githubusercontent.com/11563347/249426565-412849a4-15bb-446d-acd8-40b9d64ef8bc.mp4" data-canonical-src="https://user-images.githubusercontent.com/11563347/249426565-412849a4-15bb-446d-acd8-40b9d64ef8bc.mp4" controls="controls" muted="muted" class="d-block rounded-bottom-2 border-top width-fit" style="max-height:640px; min-height: 200px">

  </video>
</details>
</p>

Dev Proxy is a command line tool for simulating APIs for testing apps.

It aims to provide a better way to test applications.

Use the proxy to:

- simulate errors
- simulate API behaviours
- mock responses

Identify and fix issues in your code before they reach production.

## Get started

Begin with our [tutorial](https://learn.microsoft.com/microsoft-cloud/dev/dev-proxy/get-started/). It will guide you through the installation process and running the proxy for the first time.

## Example

Fail requests (with a 50% chance) and respond with `429 Too Many Requests` or `503 Service Unavailable`:

```text
devproxy --failure-rate 50 --no-mocks --allowed-errors 429 503
```

## Features

- run on any OS
  - Windows
  - macOS
  - Linux
- intercept requests from Microsoft Graph and other APIs
- simulate errors
- simulate throttling
- simulate rate-limiting
- mock responses
- mock error responses
- define wildcard paths to serve mocked responses
- mock responses of different types (JSON, binary, etc.)
- `$select` guidance to improve performance
- caching guidance to improve performance
- OData paging guidance
- client-request-id header guidance
- non-production beta endpoint guidance for Microsoft Graph
- configure proxy to your needs, by setting:
  - failure rate
  - port
  - whether to use mock responses or not
  - URLs to intercept traffic
- record proxy activity
- get proxy activity summary report
- detect minimal Microsoft Graph API permissions
- check for excessive Microsoft Graph API permissions

### .NET Foundation

This project is supported by the [.NET Foundation](https://dotnetfoundation.org).
