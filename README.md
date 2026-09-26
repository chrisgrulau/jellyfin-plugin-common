# jellyfin-plugin-common

Shared source for **Shoal**, a family of [Jellyfin](https://jellyfin.org) plugins:
[Shoal Ingest](https://github.com/chrisgrulau/jellyfin-ingest), [Shoal Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles)
and [Shoal AI](https://github.com/chrisgrulau/jellyfin-ai). It is **not a plugin** and is never installed on its own.

It holds what every plugin that calls an external service needs, written once:

| Area | What it does |
|---|---|
| Resilience | Classifies failures (no connection, transient, provider limit, authentication, bad request), reads the provider's `Retry-After`, and redacts secrets from error text. |
| Spending limits | Monthly limits overall and per provider, in the user's currency: every paid call is reserved against them first, then settled with its actual cost (or released). |
| Spend tracking | A ledger of what every call cost (published prices × usage), kept for the prepaid-credit countdown. Unknown costs block paid calls rather than count as free. |
| Currencies | Keeps every cost in the currency it was charged in and converts it with the European Central Bank's daily reference rates (validated; the last good rates are kept while offline). |
| Keys | An owner-only key file per plugin; keys are write-only from the settings pages. |
| Cross-plugin calls | Clients for the AI plugin's and the Subtitles plugin's in-process entry points (JSON in and out, no shared types). |

Not built yet (planned): per-purpose and per-day budgets, request-rate limits, approve-before-running estimates, and
shared user-facing alerts. Each plugin currently reports problems on its own page and in Jellyfin's Activity log.

## How plugins use it

Each plugin adds this repository as a git submodule and compiles its source in as **internal** code:

```bash
git submodule add https://github.com/chrisgrulau/jellyfin-plugin-common.git external/common
```

```xml
<!-- in the plugin's .csproj -->
<Import Project="../../external/common/Common.props" />
```

Compiling the source into each plugin (instead of shipping a shared DLL) means two plugins can carry different versions
of this code without clashing when Jellyfin loads them side by side. A plugin picks up changes by moving its submodule to
a newer commit, so every plugin release pins exactly the shared code it was tested with.

## Building

```bash
dotnet test --solution Jellyfin.Plugin.Common.sln
```

The code is built with the same analyser settings as the plugins (all rules enabled, warnings as errors), so it compiles
cleanly wherever it is imported.

Design notes: [`docs/DESIGN.md`](docs/DESIGN.md).

## Licence

[GPL-3.0](LICENSE), in line with Jellyfin's official plugins (the server itself is GPL-2.0).
