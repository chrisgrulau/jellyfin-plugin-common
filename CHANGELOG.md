# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Plugins pin a commit via their submodule; there are no releases.

## [Unreleased]

### Added
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies, CI.
- `Common.props` for compiling the shared source into a plugin as internal code.
- Failure classes (no connection, transient, provider limit, authentication, bad request) and their back-off schedule.

### Fixed
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
