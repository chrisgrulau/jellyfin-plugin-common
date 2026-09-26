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

Budgets are per service (and optionally per purpose, such as `subtitles.sync` or `ingest.match`): per request, per day
or per month. A limit of 0 means no paid usage; unlimited is a separate, explicit choice with a warning, and still
honours limits enforced by the provider. The default is a small cap. Bulk runs are estimated first; scheduled runs
either wait for approval of the estimate or run automatically within the budget.

## Spend tracking

Cost is recorded per call from the most authoritative source available:

1. A cost figure in the response itself.
2. The provider's cost or usage API (some need an administrator key, which is optional).
3. Usage from the response (tokens, audio seconds) × a published price table. The table is a versioned file in this
   repository, fetched periodically by the plugins; user overrides always win.

### Ledger

`SpendLedger` implements the budget rules above. It holds one entry per paid call: provider, purpose, amount as charged,
time, and whether it has been settled.
- **Before a call:** `TryReserve` converts the estimate and the month's entries so far to the user's currency, checks
  the overall and per-provider limits, and records a reservation, all under one lock.
- **After a call:** `Settle` replaces the estimate with the actual cost, or `Release` drops it if nothing was charged.
- **Interrupted calls:** a reservation that is never settled keeps counting at its estimate.
- **Storage:** the file is replaced atomically on every change. A damaged file is set aside, and paid use stops until
  the month ends.
- **Ownership:** while each plugin owns its own budget, each keeps its own ledger. Once the AI plugin owns shared
  budgets, it keeps this ledger for all of them.

## Currencies

People set and see budgets in their own currency (for example AUD); providers charge in theirs. Deepgram, OpenAI,
Anthropic and AssemblyAI price and bill in US dollars; Google Cloud and Azure can bill in the billing account's local
currency. So:

- Every price and every recorded cost is **`Money`: a `decimal` amount with the ISO 4217 code it was charged in**. Costs
  are stored as charged and only converted for display and for checking a budget.
- Conversion uses the **European Central Bank's daily euro reference rates** (free, no key, about 30 currencies). The
  file is remote input: one fixed HTTPS URL, at most 64 KB, parsed without DTDs, every value checked; anything odd
  means no new rates, and the last good ones are kept.
- Rates more than **7 days** old count as unknown (the ECB publishes on working days only). A cost that can't be
  converted is **unknown, never zero**: paid calls in another currency stop, with an alert, until rates are available
  again. A charge already in the user's currency needs no rates.
- An optional **extra percentage** is added to every converted cost, for taxes charged on overseas services (such as
  GST) or a card's foreign-transaction fee. It defaults to 0.
- When a budget is shared across plugins, its owner (see *Budgets*) also owns its currency.

## Keys and errors

API keys live in an owner-only file per plugin (`KeyFile`), never in the plugin configuration, so the settings API can
only report whether a key is set, replace it or clear it. Anything from a provider that is logged or shown goes through
`Redaction` first. `HttpFailure` turns a failed call into a failure class, reading the provider's own wait time where it
gives one; `BackoffSchedule` then bounds and spreads it.
