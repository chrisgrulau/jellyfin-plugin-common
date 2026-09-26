# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's
[private vulnerability reporting](https://github.com/chrisgrulau/jellyfin-plugin-common/security/advisories/new) instead.

## Secrets

This code handles API keys supplied by plugin users at run time. It never logs them, never includes them in alerts or
error messages, and nothing secret belongs in this repository.

Keys are kept in a file of their own, never in the plugin configuration. On Linux and macOS the file is created with
mode 0600; on Windows it gets an access list that grants only the server's account, SYSTEM and Administrators and
doesn't inherit the folder's permissions. Text that may contain a key (provider error bodies) is redacted before it is
cut to length, logged or shown.
