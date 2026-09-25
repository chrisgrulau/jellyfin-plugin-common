# jellyfin-plugin-common

Shared source for a family of [Jellyfin](https://jellyfin.org) plugins:
[Ingest](https://github.com/chrisgrulau/jellyfin-ingest), [Subtitles](https://github.com/chrisgrulau/jellyfin-subtitles)
and [AI](https://github.com/chrisgrulau/jellyfin-ai). It is **not a plugin** and is never installed on its own.

It holds what every plugin that calls an external service needs, written once:

| Area | What it does |
|---|---|
| Resilience | Classifies failures (no connection, transient, provider limit, authentication, bad request) and applies the right retry and back-off to each, honouring the provider's own reset times. |
| Budgets and rate limits | Per-service and per-purpose spending caps (per request, day, month or none), request-rate limits, and approve-before-running estimates. |
| Spend tracking | Records what every call cost, using the most authoritative source available (cost in the response, the provider's cost API, published prices × usage). |
| Alerts | Consistent, de-duplicated user-facing alerts ("limit reached — resets at …", "check your API key"). |

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

[GPL-3.0](LICENSE), in line with Jellyfin and its official plugins.
