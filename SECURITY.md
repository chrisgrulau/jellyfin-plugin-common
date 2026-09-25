# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's
[private vulnerability reporting](https://github.com/chrisgrulau/jellyfin-plugin-common/security/advisories/new) instead.

## Secrets

This code handles API keys supplied by plugin users at run time. It never logs them, never includes them in alerts or
error messages, and nothing secret belongs in this repository.
