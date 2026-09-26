# Security

Report vulnerabilities privately via GitHub's "Report a vulnerability" on the Security tab, rather
than opening a public issue.

Please note the [trust model](README.md#where-this-sits) before reporting: this gateway performs
authorization only and trusts its identity headers completely. It is designed to run behind an
authenticating proxy, and being able to set `auth-claim-scopes` on a direct connection to it is
expected behaviour, not a vulnerability.
