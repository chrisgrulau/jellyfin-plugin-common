# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Plugins pin a commit via their submodule; there are no releases.

## [Unreleased]

### Added

- **FAM-01:** `IsoLanguages`, which maps ISO 639 codes and names without the server's culture data.
  - It maps two-letter, three-letter (terminology and bibliographic) and English-name forms to each other, for 137
    languages.
  - Before, .NET knew no languages with invariant globalization or minimal ICU data (Alpine, some containers), so
    language matching silently found nothing.
  - The table was generated from .NET's own culture data.

- **FAM-06:** shared building blocks, so the plugins stop keeping drifting copies.
  - `Storage/JsonFile`: `Read<T>` tells a missing, loaded, damaged or unreadable file apart; `SetAside` moves a damaged
    file to a timestamped name; `WriteAtomic` writes via a flushed temporary file and a rename, optionally owner-only.
    `SpendLedger`, `ExchangeRateStore` and `KeyFile` use it, with their policies unchanged.
  - `Resilience/ProviderException`: failure class, HTTP status, `RetryAfter`, and `Charged` with the usage to record.
  - `Resilience/ProviderHttp`: `SendAsync` and `SendForBytesAsync` classify failures with `HttpFailure`, honour
    `Retry-After`, cap every body (reply or error) at the caller's limit, and remove keys from messages.
  - `Costs/MeteredCall`: reserve, call, then settle at the actual cost, settle a billed failure at what it used, or
    release one that certainly wasn't billed; an unexpected failure is recorded at the estimate. `MeteredCallOptions`
    lets a plugin with its own exception type take part.
  - `Costs/SpendingStore`: a plugin's ledger, shipped prices and exchange rates together, with rates refreshed when due.
  - `CurrencyCode.NormaliseOr(code, fallback)`: only supported codes pass.

### Fixed

- **FAM-02:** the entry-point clients (`AiBridgeClient`, `SpeechBridgeClient`) send letters in every script as they
  are, not as `\uXXXX` escapes. Before, non-English text was six times its size, was often refused as too large, and
  reached the model escaped.
  - `<`, `>` and `&` are still escaped, so text can't close a block such as `</data>`.
  - `BridgeJson.Bytes` measures data as the AI plugin does (UTF-8 bytes), and `AiBridgeClient.MaxDataBytes` is the
    limit, so callers can shrink what they send to fit.
- **COM-06:** a key file this account can't read no longer makes the settings pages fail with errors.
  - `KeyFile.Status` and `Get` answer as if no key were set, and `Problem` says why.
  - Saving a key replaces a file that can't be overwritten, where the folder allows.
- **COM-07:** a spend ledger that can't be read at the moment (locked by a backup or antivirus, a share hiccup)
  refuses that one paid call and is read again next time.
  - It is no longer moved aside and blocked for the rest of the month.
  - A damaged (unparseable) ledger is still set aside and blocks paid use.

### Added
- `SpeechBridgeClient`: asks the family's Subtitles plugin, if installed, to transcribe a short stretch of a video
  (at most 180 seconds) through its in-process entry point, the same way as `AiBridgeClient`.
  - The Subtitles plugin chooses the speech-to-text service and applies its own spending limits.
  - A missing plugin, a refusal, a failure or a bad reply comes back as a reason, never an exception.
- `AiBridgeClient`: asks the family's AI plugin, if installed, through its in-process entry point.
  - It finds the plugin by assembly and type name, sends version-1 JSON and reads the reply, so no types are shared
    between plugins.
  - A missing plugin, a refusal, a failure or a bad reply comes back as a reason (never an exception), so callers fall
    back to review.
- `SpendLedger.SpentSince` adds up what one provider has cost since a moment, in one currency, for counting down a
  prepaid credit.
- **Spending:**
  - `SpendLedger` reserves each paid call's estimated cost before it is made and settles the actual cost afterwards,
    atomically across concurrent calls. It checks an overall monthly limit and per-provider limits in the user's
    currency (0 = no paid use, no limit = explicit).
  - Costs are kept as charged and converted only for checks. A cost that can't be converted refuses the call, and a
    damaged ledger blocks paid use for the rest of the month instead of starting again from zero.
- `PriceTable`: validated published prices (per audio minute or per million tokens) with per-model and per-provider
  lookup. A price that isn't listed is unknown.
- `ExchangeRateStore`: fetches the ECB daily rates from their one fixed address (64 KB cap, at most about once a day),
  saves the last good rates, and keeps them when a fetch fails.

### Added
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies, CI.
- `Common.props` for compiling the shared source into a plugin as internal code.
- Failure classes (no connection, transient, provider limit, authentication, bad request) and their back-off schedule.

### Fixed
- `HttpFailure` (COM-03):
  - Every wait read from a response is bounded to 31 days before it is converted, so absurd `retry-after-ms` or
    duration values can't overflow.
  - A provider limit is recognised by the providers' own wording (`insufficient_quota`, `billing_hard_limit_reached`,
    "credit balance is too low", `RESOURCE_EXHAUSTED` …), not bare words like "insufficient" or "credit", so an
    ordinary rejected request doesn't pause a provider.
  - `Classify(exception, token)` rethrows a cancellation the caller asked for, instead of classing it as a transient
    failure.
- Secrets (COM-04):
  - `Redaction.Redact` removes secrets before cutting the text, so a key straddling the cut can't leave a piece behind.
  - On Windows the key file gets an owner-only access list (the server's account, SYSTEM, Administrators) instead of
    inheriting the folder's.
- `NetworkAddress.IsLocal` (COM-05): one definition of "this machine or the local network" for every plugin (loopback,
  private, link-local and unique-local addresses, IPv4-mapped IPv6, `localhost`, `.local`).
- A retry delay stated by a provider is kept between 1 second and 31 days, so a buggy or hostile `Retry-After` (or a
  reset time years away) can't park a queue indefinitely or overflow date arithmetic. Up to 10 % is added (never
  less than the provider asked for), so everything waiting on the same reset doesn't retry at the same instant. A NaN
  jitter is refused (COM-01).

### Added
- Currencies: `Money` (a `decimal` amount with its ISO 4217 code), the choosable currencies, the European Central
  Bank's daily reference rates (validated remote input), and conversion of a provider's charge into the user's currency
  with an optional extra percentage for taxes or card fees. Missing or stale rates make a cost unknown, never zero.

### Changed
- Builds: SDK pinned in `global.json`, lock files with locked-mode restores in CI, checkout without persisted
  credentials and a job timeout; Dependabot watches the SDK. `.gitignore` covers test audio and speech models, and its
  header no longer says no keys are handled (BLD-01, BLD-03, DOC-01).
- `global.json` accepts any .NET 10 SDK (10.0.100 and later), so the SDKs shipped by Linux distributions (10.0.1xx)
  build it; CI uses the newest .NET 10 SDK, and Dependabot no longer raises the minimum. Package versions stay locked.
- The plugin family is now called **Shoal** (Shoal Ingest, Shoal Subtitles, Shoal AI).
- Secrets: `KeyFile`, a write-only store for API keys in an owner-only (0600) file separate from the plugin
  configuration (moved here from Shoal AI so every plugin uses the same implementation), and `Redaction`, which strips
  known keys (whole or partial), bearer tokens and common key shapes from text before it is logged.
- `HttpFailure`: sorts HTTP statuses and network exceptions into the failure classes (an exhausted quota reported as
  400, 403 or 429 counts as a provider limit), and reads `Retry-After`, `retry-after-ms` and rate-limit reset headers.
