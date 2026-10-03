<h1 align="center"> 
  <img alt="Dev Proxy" src="./media/icon.png" width="125" />
  <p>Dev Proxy</p>  
</h1>

<h4 align="center">
  Test beyond the happy path
</h4>

<p align="center">
  <a href="https://youtube.com/@devproxy">
    <img alt="YouTube" src="https://img.shields.io/badge/youTube-%40devproxy%E2%80%AC-red?style=social&logo=youtube&link=https%3A%2F%2Fyoutube.com%2F%40devproxy" />
  </a>
  <br />
  <a href="https://aka.ms/devproxy">
    Documentation
  </a>
</p>

# What is Dev Proxy?

Dev Proxy is an API simulator that helps you test how your app handles errors, throttling, and slow responses, without changing a single line of code. Dev Proxy is a command-line tool that works on any platform. Because it intercepts network requests, it works with any language, framework, and API.

Dev Proxy is **open source** and **free to use**.

[![Dev Proxy in 40 seconds](./media/showreel-thumbnail.jpg)](https://www.youtube.com/watch?v=kbTK0A4Psv0)

You test the happy path. Your users get the rest. What if the APIs you use fail or throttle you? Will your app lose your customers' data? How do you test for this? Simulating API failures is hard. You end up writing code that you won't be shipping or worse: not testing at all. That's why we built Dev Proxy, to simulate API errors so that you can easily test your app without changing your code.

With Dev Proxy you:

- **See how your app responds to API errors**, without changing your app’s code, so that you can **build more robust apps and don't lose customers' data**.
- **Verify how your app handles API rate limits**, so that you can avoid getting throttled and **improve the user experience for your customers**.
- **See how your app handles slow APIs**, so that you can implement the necessary affordances, and **make your app more user-friendly**.
- **Quickly stand-up mock APIs** without writing a line of code, so that you can **focus on building your app instead of writing code you won't be shipping**.
- **Test your AI apps** by routing OpenAI-compatible requests to a local language model and simulating LLM failures, so that you can **build reliable AI experiences without burning tokens**.
- **Automate resilience testing in your CI/CD pipeline** with [GitHub Actions](https://github.com/dev-proxy-tools/actions), so that you can **catch issues before your customers do**.
- **Configure Dev Proxy in plain English** using the [Dev Proxy MCP server](https://www.npmjs.com/package/@devproxy/mcp) with your AI coding agent, so that you can **get started in seconds**.
- Improve your app with contextual guidance on how you use APIs, to **make your app even better**.

## Get started

Install Dev Proxy using the command for your operating system.

**Windows** (after the installation finishes, open a new terminal):

```bash
winget install DevProxy.DevProxy --silent
```

**macOS**:

```bash
brew tap dotnet/dev-proxy
brew install dev-proxy
```

**Linux** (after the installation finishes, run the `source` command the installer prints, or open a new terminal):

```bash
bash -c "$(curl -sL https://aka.ms/devproxy/setup.sh)"
```

Download the preset for the API your app calls, and start Dev Proxy with it:

```bash
devproxy config get github-rate-limiting
devproxy --config-file "~dataFolder/configs/github-rate-limiting/.devproxy/devproxyrc.json"
```

API | Preset
----|-------
GitHub | `github-rate-limiting`
OpenAI | `openai-throttling`
Anthropic | `anthropic-throttling`
Microsoft Graph | `microsoft-graph-rate-limiting`

The first time you start Dev Proxy, trust its certificate so that it can intercept HTTPS requests. On Linux, you need to trust the certificate manually. Our [tutorial](https://aka.ms/devproxy/start) walks you through these steps.

Then, run your app as usual. It keeps calling the real API URLs, and Dev Proxy simulates the API's rate limits and errors. Find presets for other APIs in the [samples gallery](https://aka.ms/devproxy/samples).

[![Getting started with Dev Proxy](https://img.youtube.com/vi/HVTJlGSxhcw/0.jpg)](https://www.youtube.com/watch?v=HVTJlGSxhcw)

## .NET Foundation

This project is supported by the [.NET Foundation](https://dotnetfoundation.org).
