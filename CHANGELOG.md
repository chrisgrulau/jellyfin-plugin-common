# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Plugins pin a commit via their submodule; there are no releases.

## [Unreleased]

### Added
- Repository scaffolding: licence (GPL-3.0), README, design notes, contribution and security policies, CI.
- `Common.props` for compiling the shared source into a plugin as internal code.
- Failure classes (no connection, transient, provider limit, authentication, bad request) and their back-off schedule.
