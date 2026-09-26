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
- **Storage:** the file is replaced atomically on every change (`JsonFile`). A damaged file is set aside, and paid use
  stops until the month ends. A file that can't be read right now (locked, a share hiccup) refuses that one call and is
  read again next time; it is neither moved nor overwritten.
- **Ownership:** while each plugin owns its own budget, each keeps its own ledger. Once the AI plugin owns shared
  budgets, it keeps this ledger for all of them.

### Metered calls

`MeteredCall.RunAsync` is the one place a paid call is run:
- It reserves the estimate first; if the limits refuse, the call isn't made and the refusal is thrown (by default a
  `ProviderException` of class *provider limit*).
- A successful call is settled at its actual cost, from its result (or the estimate if that can't be worked out).
- A call that failed but was billed anyway (a refusal, a cut-off or unreadable answer) is settled at what it used: by
  default a `ProviderException` with `Charged` set, at its `ChargedCost` or else the estimate.
- A failure that certainly wasn't billed (a provider refusal, no answer, or a cancellation) releases the reservation.
- An unexpected failure is recorded at the estimate, since it may have been billed.

A plugin whose exception type is public (so it can't derive from the internal `ProviderException`) passes its own
`MeteredCallOptions` (`IsCharged`, `ChargedCost`, `Refuse`).

### Spending store

`SpendingStore` keeps what a plugin needs for spending in its data folder: the ledger (`spend.json`), the exchange
rates (`rates.json`) and the shipped price table (`ShippedPrices`). `CurrentRatesAsync` refreshes the rates when due
(about daily; at most every 30 minutes while they are missing or stale), so calling it before each reservation keeps
them current without asking an unreachable source on every call. `Currencies` is the list a settings page offers. Each
plugin still decides its own limits from its own settings.

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
- A currency setting is read with `CurrencyCode.NormaliseOr(code, "USD")`: only a supported code passes; anything else
  is the fallback.

## Keys and errors

API keys live in an owner-only file per plugin (`KeyFile`), never in the plugin configuration, so the settings API can
only report whether a key is set, replace it or clear it. Anything from a provider that is logged or shown goes through
`Redaction` first. `HttpFailure` turns a failed call into a failure class, reading the provider's own wait time where it
gives one; `BackoffSchedule` then bounds and spreads it.

`ProviderHttp.SendAsync` (text) and `SendForBytesAsync` (downloads) send a provider request in one place:
- A failure is thrown as a `ProviderException` with its failure class, the HTTP status and the provider's wait
  (`Retry-After` and the common rate-limit headers). The caller's own cancellation is rethrown as a cancellation.
- No body is read beyond the caller's limit: a reply over it is refused (class *bad request*); an error body is read
  only up to it, which is enough to classify and show it.
- Every message has the caller's keys removed.

## Stores

Every JSON store goes through `JsonFile`, and states its own policy for each outcome of `Read`:

| State | Meaning | Policy of the stores here |
|---|---|---|
| Missing | Nothing saved yet | Start empty. |
| Loaded | Read and parsed | Use it. |
| Damaged | Not valid JSON: it won't get better | Ledger: `SetAside` (`file.damaged-UNIXSECONDS`) and block paid use this month. Keys: enter them again. Rates: fetch again. |
| Unreadable | Locked, a share hiccup, permissions: it may be fine | Never overwrite or set aside. Ledger: refuse this call, read again next time. Keys: report `Problem`. Rates: fetch again. |

`WriteAtomic` writes a temporary file in the same folder, flushes it to disk and renames it over the old one. With
`ownerOnly` (the key file) the temporary file is created mode 0600 on Linux and macOS, or with an access list for only
the server's account, SYSTEM and Administrators on Windows, before anything is written; a file left by another account
is replaced where the folder allows.
