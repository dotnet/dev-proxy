---
applyTo: "DevProxy/Commands/**"
---

# CLI command conventions

- **Output goes through `ILogger`, not `Console`.** Inject `ILogger<TCommand>` and log human-readable text with `LogInformation`/`LogWarning`/`LogError`. For `--output json`, serialize with `ProxyUtils.JsonSerializerOptions` and call `_logger.LogStructuredOutput(json)`; the JSON console formatter wraps it in a JSON Lines envelope `{ "type": "result", "data": <your payload>, "category": …, "timestamp": … }`, so document the payload as `data`, not as top-level output. `System.Console` is banned in this folder by analyzer `RS0030` (see `BannedSymbols.txt`); the few existing files that need raw stdout/stderr (secrets, log streaming, prompts, help rendering) are listed in `.editorconfig`. Don't add files to that list.
- **Reuse global options and existing helpers.** `--output` is a global option: read it with `parseResult.GetValueOrDefault<OutputFormat?>(DevProxyCommand.OutputOptionName) ?? OutputFormat.Text`; don't add per-command output/format options. Before writing parsing or validation code, search `DevProxyCommand` (e.g. `ValidatePort`), `DevProxy.Abstractions/Utils/ProxyUtils.cs` (URL matching, JSON schema validation) and other commands, and reuse or centralize instead of duplicating.
- **Exit codes:** `0` success, `1` runtime failure (instance not running, I/O, network), `2` invalid input or usage (bad arguments, failed validation). These are documented in `ExitCodeHelpAction`; keep new commands consistent.
- **Don't hardcode defaults that exist in code.** Reference them, e.g. `proxyConfiguration.ApiPort` / `proxyConfiguration.Port` (`DevProxy/Proxy/ProxyConfiguration.cs`), instead of repeating `8897`/`8000` in help text or logic.
- **Naming:** group related subcommands under a noun and name the subcommand with a verb, matching existing ones (`config new|get|open|validate`, `cert ensure|remove`, `jwt create`, `api show`). Check existing verbs before introducing a new one. Register commands in `DevProxyCommand.cs` and keep lists sorted with `OrderByName()`.
- **Help examples:** add them with `HelpExamples.Add(command, [("devproxy …", "Description")])`; descriptions are aligned automatically. If you write examples inside a description string, align the descriptions in one column as rendered at runtime (escaped quotes like `\"` count as one character).
