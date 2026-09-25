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
