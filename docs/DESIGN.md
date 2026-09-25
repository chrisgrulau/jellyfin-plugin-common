# Design notes

## Consumption model

Plugins compile this source directly (`Common.props`), as internal types. Jellyfin loads each plugin's assemblies side by
side; a shared DLL name would force every plugin onto the same version. Compiling in avoids that entirely. Types here are
therefore `internal`, and tests reach them through `InternalsVisibleTo`.

**Never share these types across plugins.** When one plugin needs another (for example Ingest asking the AI plugin for a
decision), it goes through that plugin's own JSON-in/JSON-out entry point using only .NET base types.

## Failure classes and retries

Every failed call to an external service is classified before anything is retried:

| Class | Typical signals | Retry | Alert |
|---|---|---|---|
| **No connection** | DNS failure, network unreachable, connection refused to every host | Re-check connectivity cheaply every minute; resume when it returns | Immediately |
| **Transient** | Timeout, HTTP 5xx, short-window 429 | Exponential from 2 s, doubling to a 5-minute cap, 50–100 % jitter | Only if it persists past 30 minutes |
| **Provider limit** | Quota / credit / spending limit reached (provider-specific 402/429/400 bodies) | The provider's stated reset (`Retry-After`, rate-limit reset headers, billing-period end) first; otherwise 1 h → 1 day → 3 days → weekly, ±10 % jitter | "Limit reached — resets at …" |
| **Authentication** | 401 / 403, invalid or revoked key | Never automatically | "Check your API key" |
| **Bad request** | 400 / 422 for this input (unsupported file, too long …) | Never; only this item fails and the queue carries on | Shown against the item |

While a provider is limited, only a single cheap probe is sent at each retry time; queued work stays *pending*, never
*failed*, and a "Retry now" action lets the user resume immediately (for example after raising a limit).

## Budgets

Budgets are per service (and optionally per purpose, such as `subtitles.sync` or `ingest.match`): per request, per day,
per month, or none. "None" still honours limits enforced by the provider. Bulk runs are estimated first; scheduled runs
either wait for approval of the estimate or run automatically within the budget.

## Spend tracking

Cost is recorded per call from the most authoritative source available:

1. A cost figure in the response itself.
2. The provider's cost or usage API (some need an administrator key, which is optional).
3. Usage from the response (tokens, audio seconds) × a published price table. The table is a versioned file in this
   repository, fetched periodically by the plugins; user overrides always win.
