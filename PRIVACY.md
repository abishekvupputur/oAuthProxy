# Privacy Policy

**Applies to:** RavensPort for Windows, v3.0.0 and later
**Last updated:** 31 July 2026

RavensPort is a desktop application that runs entirely on your own machine. It is not a service,
it has no backend, and it has no account. The developer does not receive, store, or have access to
any of your data at any point.

This document describes what the application does with data on your computer, what it sends over
the network and where, and how to remove everything it leaves behind.

---

## 1. The short version

- **No telemetry, no analytics, no crash reporting, no update checks.** The application contains no
  code that contacts the developer or any third-party analytics service. Nothing about your usage
  is collected or transmitted.
- **No accounts.** There is nothing to sign up for and no identifier is issued to you.
- **Your secrets go to your password manager, not to disk.** OAuth client secrets, access and
  refresh tokens, API keys, and per-endpoint proxy keys are stored in a vault in 1Password or
  Proton Pass that you control.
- **The only files written to this PC are logs**, under `%AppData%\RavensPort\`, and they are
  redacted so that credential values do not appear in them.
- **The only network connections are ones you configure** — the OAuth providers and upstream APIs
  you set up — plus a listener bound to `127.0.0.1` that never leaves your machine.

---

## 2. What data the application handles

RavensPort processes the following, all of which you supply:

| Data | Why it exists |
|---|---|
| OAuth2 client ids and client secrets | To run the authorization flow against a provider you choose |
| OAuth2 access and refresh tokens | To attach a live token to requests forwarded to an upstream |
| Static API keys | The same purpose, for services that do not offer OAuth |
| Per-route and per-funnel proxy keys | So only a client you gave the key to can use an endpoint |
| Topology — upstream URLs, route prefixes, MCP sources, funnels, tool filters, settings | The configuration that makes the proxy do anything |
| Request and response metadata passing through the proxy | Forwarded to the upstream you configured; summarised in the activity log |

The developer sees none of it. There is no mechanism by which any of it could reach the developer.

---

## 3. Where it is stored

### 3.1 Your password manager (all configuration and all secrets)

Everything in the table above, other than log entries, is stored in a vault in **1Password** or
**Proton Pass** — whichever you connect, in a vault you nominate. There is no local cache and no
fallback file; the proxy does not start until your password manager is unlocked or a service
account token is available.

Within that vault, the topology lives in a `RavensPort Config` item with no secrets in it, and each
secret gets its own item so your password manager can conceal it independently.

Your password manager is a third party with its own privacy policy, its own storage locations, and
its own sync behaviour across your devices. RavensPort invokes its command-line tool (`op` or
`pass-cli`) as a local subprocess; the vendor's terms govern what happens to data once it is in
their vault. See [1Password's privacy policy](https://1password.com/legal/privacy) and
[Proton's privacy policy](https://proton.me/legal/privacy).

### 3.2 This PC

Only three things touch local storage:

| Location | Contents | Written when |
|---|---|---|
| `%AppData%\RavensPort\logs\activity-YYYYMMDD.log` | Proxied requests and responses, connects, token refreshes, route reloads, vault operations | Always, while running |
| `%AppData%\RavensPort\logs\errors.log` | Unhandled exceptions and provider errors, with stack traces | On error |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` | A single value pointing at the executable | Only if you enable **Start with Windows** |

Activity logs rotate every 2 days and older files are deleted automatically after roughly 10 days.
Nothing else about your configuration is persisted locally — a change made while the vault is
locked is held in memory only, and is lost if the application exits before the vault becomes
reachable.

If you upgraded from a version before 2.0, an old `%AppData%\RavensPort\store.dat` may still exist
from that version. RavensPort never reads it and never deletes it on its own; the setup page offers
to delete it for you.

### 3.3 What the logs deliberately do not contain

- Credential values are never logged. Query parameter **names** are recorded; their **values** are
  replaced with `<redacted>`.
- Tokens, API keys, and proxy keys are never written to a log.
- Vault operations record the command, exit code, and duration — never the command output, which
  for a read would be the item's contents.
- Control characters in request paths are escaped, so a caller cannot inject fabricated log lines.

Logs still contain request paths, upstream hostnames, timestamps, and status codes. Treat them as
sensitive: they describe which services you use and when, even though they do not contain the
credentials for them.

---

## 4. Network connections

RavensPort makes no connection you have not configured. Specifically:

1. **A local listener** on `http://127.0.0.1:5559` by default (the port is configurable). It is
   bound to the loopback interface and is not reachable from your network.
2. **OAuth2 authorization and token endpoints** for providers you add — for example Google,
   a Nextcloud instance, or any custom provider whose URLs you enter. Contacted when you connect a
   credential and when a token is refreshed, which happens automatically about 10 minutes before
   expiry.
3. **Upstream APIs and MCP servers you configure**, when a request is routed to them. RavensPort is
   a proxy: the request content is yours and the destination is the one you set.
4. **Your password manager's CLI**, which talks to the vendor's service on its own terms.

Each of these third parties receives whatever the interaction requires — an upstream sees your
requests and the credential attached to them, an OAuth provider sees an authorization attempt — and
handles it under its own privacy policy. Choosing them is your decision; RavensPort adds no
destination of its own.

Downloading a release from GitHub, or verifying its build attestation with the GitHub CLI, is an
action you take outside the application, under
[GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-privacy-statement).

---

## 5. Other processes on your machine

The local endpoint is reachable by anything running as you on this PC. That is why every route and
every funnel carries its own proxy key with its own expiry: a client can only spend the grants for
the endpoint it was given a key to, and a key leaked from one client does not reach the rest.
Choosing which local clients receive which key is a decision you make.

---

## 6. Retention and deletion

Nothing is retained by the developer, because nothing is ever received. To remove RavensPort's data
from your own systems:

| To remove | Do this |
|---|---|
| All configuration and secrets | Delete the RavensPort items — or the whole vault — in your password manager. **Settings → Disconnect** stops using a vault but deliberately deletes nothing from it |
| Logs | **Settings** → prune logs, or delete `%AppData%\RavensPort\logs\` |
| Autostart entry | Turn off **Start with Windows**, or delete the `RavensPort` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |
| Legacy pre-2.0 store | Delete `%AppData%\RavensPort\store.dat` |
| The application | Delete the executable. It is self-contained and has no installer |

Revoking an OAuth grant is separate: **Disconnect** on a credential clears the local token but does
not revoke anything at the provider. Revoke it in the provider's own account settings.

---

## 7. How to check that any of this is true

A privacy policy is a claim. This one is checkable, because the source and the build that produces
the download are both public.

**The source is the whole application.** RavensPort is open source under the MIT License. Every
statement above — that there is no telemetry, that secrets go to the vault rather than to disk,
that log values are redacted — is a property of code you can read in this repository.

**Released binaries are built by GitHub, not by the developer.** No release is ever built on a
personal machine and uploaded. Pushing a version tag runs
[`.github/workflows/release.yml`](.github/workflows/release.yml) on a GitHub-hosted Windows runner,
which checks out this repository, restores dependencies, runs the test suite, and only then
publishes the self-contained `RavensPort-<tag>.exe` attached to the release. That workflow file is
in the repository and is subject to the same public history as the rest of it. Its own safeguards
are visible there: third-party actions pinned to commit SHAs rather than mutable tags, dependency
restore in `--locked-mode` against the committed `packages.lock.json`, and write permissions
granted to one job rather than inherited by default.

**Each release carries a build provenance attestation**, signed with the workflow's OIDC identity,
recording which workflow, commit, and runner produced that exact file. The binary is not
Authenticode-signed, so Windows will still warn about an unknown publisher — but you can verify the
download against the build that claims to have produced it, using the
[GitHub CLI](https://cli.github.com/):

```bash
gh attestation verify RavensPort-v3.0.0.exe --repo abishekvupputur/oAuthProxy
```

A pass means the file is byte-for-byte what CI built from this repository — so what you run is what
the published source says it is, and this document can be held to that source rather than taken on
trust.

**Building it yourself** produces the same application from the same source; see
[README.md](README.md#building).

---

## 8. Legal position

Because no personal data reaches the developer, the developer is neither a controller nor a
processor of your data under the GDPR or comparable laws. You operate the software on your own
equipment and decide, alone, what data it handles and which third parties it contacts — for the
data described here, that role is yours.

The software is provided under the MIT License, without warranty of any kind. See
[LICENSE](LICENSE).

## 9. Children

RavensPort is a developer tool. It is not directed at children and collects nothing from anyone.

## 10. Changes to this policy

This file is versioned in the repository. Material changes will accompany a release, and the "last
updated" date above will change. The history of this file is the full record of any revisions.

## 11. Contact

Questions about this policy, or a claim that it misdescribes what the software does, belong in the
[issue tracker](../../issues) — where the answer is public and checkable against the source.

---

*This document describes the behaviour of the software as published. It is not legal advice. If you
deploy RavensPort inside an organisation, your own obligations toward the data flowing through it
are unaffected by anything stated here.*
