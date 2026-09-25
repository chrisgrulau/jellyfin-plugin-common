# Contributing

- Target `main`; keep pull requests focused. Remember every change here ships inside several plugins.
- Types are `internal`; add tests under `tests/` (they see internals via `InternalsVisibleTo`).
- Match `.editorconfig`; the build treats warnings as errors with all analysers enabled, exactly as the plugins do.
- No credentials, server addresses or personal data in code, tests or history.

```bash
dotnet test --solution Jellyfin.Plugin.Common.sln
```
