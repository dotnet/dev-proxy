---
applyTo: "DevProxy.Plugins/**,DevProxy.Abstractions/**"
excludeAgent: "cloud-agent"
---

# Code review: plugins and abstractions

Plugins process intercepted traffic (untrusted) using configuration the user wrote (trusted). Don't ask to validate or sandbox user-supplied file paths, URLs or regexes from plugin config.

## Check

- **Schema and runtime agree.** When a plugin config property is added, renamed or its default changes, the matching file under `schemas/v<current version>/` must change too, and the C# default must equal the schema `default`. Flag mismatches in type, enum values, required-ness and naming (camelCase in JSON).
- **Pipeline order and mutation.** Plugins run in the order listed in config. Check what a plugin assumes about earlier plugins (for example `RewritePlugin` changing `RequestUri`, a mock already set on the response) and whether it respects `ResponseState.HasBeenSet` and URL matching via `urlsToWatch`.
- **Body handling.** Decode bodies using the declared charset and content encoding (gzip/br/deflate); don't assume UTF-8 or JSON. Avoid reading large bodies fully into memory when only a header check is needed.
- **Culture, time and numbers.** Use `CultureInfo.InvariantCulture` for parsing and formatting; UTC for timestamps in reports, HAR, `Retry-After`, rate-limit windows. Watch rounding and integer overflow in token/cost calculations.
- **Probability and limits.** Off-by-one in `Random.Next` comparisons, rate/limit windows that reset at the wrong boundary, counters shared across requests without synchronization (plugins handle concurrent requests).
- **Concurrency.** Plugin instances are shared across concurrent requests. Mutable per-request state stored on the plugin instance is a bug; shared collections need thread-safe types or locks.
- **Disposal.** Plugins holding `HttpClient`, file watchers, `Process` or timers must release them, and must handle config hot reload without leaking.
- **Reporting.** Reporters run last and must tolerate missing or partial data from earlier plugins. Output formats (Markdown, JSON, plain text) should stay consistent with existing reporters.
- **Abstractions are public API.** Changes in `DevProxy.Abstractions` affect third-party plugins. Flag breaking changes to public types, members or behavior.

## Don't flag

- Missing input validation on values from plugin config files beyond what's needed to give a clear error.
- Logging of request/response data: showing traffic is the product's purpose.
