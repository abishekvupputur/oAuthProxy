# OAuthProxy

An always-on Windows tray app that acts as a local OAuth2 reverse proxy for MCP and REST
endpoints. It exposes routes under `http://127.0.0.1:<port>/app/...`, injects each route's
credential (a bearer token by default), and refreshes tokens automatically before they expire
— so tools like an MCP
client can talk to an OAuth-protected API without handling auth themselves.

## Screenshots

| Credentials | Routes | Settings |
|---|---|---|
| [![Credentials tab](media/credentialsScreen.png)](media/credentialsScreen.png) | [![Routes tab](media/routesScreen.png)](media/routesScreen.png) | [![Settings tab](media/settingsScreen.png)](media/settingsScreen.png) |

*(Credential/upstream names blacked out — that's redaction, not a UI bug.)*

## Why

MCP servers and REST APIs are often behind OAuth2, but most MCP clients expect a bare HTTP
endpoint. OAuthProxy sits in between: it owns the OAuth flow and token lifecycle, and forwards
already-authenticated requests to whatever's actually behind the route.

## Features

- **Tray-resident**, starts hidden, no window shown until you open it from the tray icon
- **Multiple credentials, upstreams, and routes** — any credential can back any route; not a
  fixed 1:1 mapping
- **Per-route credential placement** — `Authorization: Bearer <token>` by default, or any
  header name, query parameter, or request-body field, with a custom value prefix
- **[MCP Funnel](#mcp-funnel)** (opt-in) — pool several MCP servers behind one local endpoint
  per AI agent, and choose exactly which of their tools, resources, and prompts that agent sees
- **Multi-provider OAuth2**:
  - **Google** — via Google's own official library (`Google.Apis.Auth`), fixed loopback
    redirect port so it can be registered in Google Cloud Console if needed
  - **Nextcloud / any custom OAuth2 provider** — via `IdentityModel.OidcClient`, works with
    plain OAuth2 (no OIDC discovery, no `userinfo` endpoint required)
- **Full credential lifecycle** — Connect, Refresh, Disconnect (clears the local token
  without revoking the grant at the provider), Edit, Delete
- **Tokens auto-refresh** 10 minutes before expiry, in the background
- **Live status per credential** — a colored dot plus expiry time, refreshed every 15s
- **DPAPI-encrypted** credential store — nothing is ever written in plaintext
- **Activity log** with 2-day rotation, viewable from the Settings tab (every proxied
  request, token refresh, and route reload is logged)
- **Single-instance guard** — a second launch refuses to start rather than fighting the first
  over ports
- Survives provider/network errors without crashing — this is meant to run unattended
- **CI**: pushing a version tag runs the test suite, and only on success builds and publishes
  a downloadable release — see [Releases](#releases)

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer (built against
  `net8.0-windows`; a `net9`/`net10` SDK can build it too — no need for an exact match)

## Quick start

```
clean-build.bat
```

Stops any running instance, wipes `bin`/`obj`, rebuilds, and launches the app. Look for the
padlock icon in the system tray — left-click opens the settings window, right-click gives a
menu (Open Settings / Start with Windows / Exit).

The app listens on **`http://127.0.0.1:5559`** by default (changeable in Settings; requires a
restart to take effect).

## Releases

Prebuilt, self-contained `OAuthProxy.exe` — no .NET install needed — is published on the
[Releases page](../../releases) whenever a version tag (`v*`) is pushed. `.github/workflows/release.yml`
runs the test suite first and only builds/publishes if it passes; nothing is released off a
failing build. Download `OAuthProxy-<version>.exe` and run it — no extraction needed.

Windows will warn that the exe is from an unknown publisher; it is not Authenticode-signed.
Instead, every release carries a **build provenance attestation** recording the workflow,
commit, and runner that produced that exact binary. Verify a download with the
[GitHub CLI](https://cli.github.com/):

```bash
gh attestation verify OAuthProxy-v1.0.3.exe --repo abishekvupputur/oAuthProxy
```

A pass means the file is byte-for-byte what CI built from this repo. A failure means it was
modified after the build, or did not come from here at all.

## Setting up a credential

Credentials tab → pick a provider preset → fill in Client ID/Secret and scopes → **Add
credential** → **Connect** (opens your browser for the consent screen).

### Google

1. In [Google Cloud Console](https://console.cloud.google.com/), create an OAuth client.
   - **Desktop app** type is recommended — Google accepts any loopback port automatically,
     nothing to register.
   - If you use **Web application** type instead, you must add the exact redirect URI shown
     in the Credentials tab (`http://127.0.0.1:51004/authorize/`) to the client's allowed
     redirect URIs.
2. Paste the Client ID/Secret, set your scopes, Connect.

Every Google authorization forces the consent screen (`prompt=consent`) so a refresh token
is issued every time, not just on first-ever consent — otherwise the credential silently
can't auto-refresh later.

### Nextcloud / custom OAuth2

1. Create an OAuth2 client under **Nextcloud Settings → Security → OAuth2** (or your
   provider's equivalent).
2. Pick the **Nextcloud** or **Custom** preset, fill in the Authorization/Token endpoints
   (or an Authority for OIDC discovery), Client ID/Secret, scopes.
3. Register `http://127.0.0.1:51005/callback/` as the redirect URI if your provider requires
   pre-registration (it's shown in the UI, fixed and copyable).

## Setting up a route

Routes tab → add an **Upstream** (a name + base URL, e.g. your MCP server or API) → fill in
a **path prefix** (e.g. `/app/my-service`), pick the upstream and credential → **Add route**.

- **Strip prefix** (recommended, on by default): `/app/my-service/foo` is forwarded upstream
  as `/foo` — the prefix is just a local label. Turn it off only if the upstream genuinely
  expects that prefix in its own path.
- The full local endpoint (e.g. `http://127.0.0.1:5559/app/my-service`) is shown ready to
  paste into an MCP client config.
- A route can be turned off (**ENABLED** checkbox) without deleting it.
- Path prefixes must be unique — two routes can't share one; the UI rejects the duplicate up
  front rather than letting every request to it fail at runtime.
- Upstream base URLs must be `https`, except on localhost. The access token is attached to
  every request forwarded there, so plain `http` would put it on the wire in cleartext.

### How the credential is sent

By default the token goes out as `Authorization: Bearer <token>`, which is what nearly every
OAuth API expects. For the ones that want it elsewhere, each route has a **placement**, a
**name**, and a **value prefix** — set on the add-route form, and editable afterwards by
selecting the route in the grid.

| Placement | Name means | Result with prefix `Bearer ` / empty prefix |
|---|---|---|
| **Header** (default) | header name | `Authorization: Bearer <token>` / `X-Api-Key: <token>` |
| **Query** | query parameter name | `?access_token=<token>` |
| **Body** | field name in the request body | `{"access_token": "<token>"}` |

- The value prefix is literal text placed before the token — `Bearer ` **with its trailing
  space**. Leave it empty to send the bare token.
- A caller-supplied header, query parameter, or body field of the same name is replaced, never
  duplicated, so the upstream is never shown two candidate credentials.
- Body injection only applies to a JSON object or `application/x-www-form-urlencoded` body
  that is under 1 MB and declares a `Content-Length`. Anything else (streamed, compressed, or
  another content type) is forwarded untouched and the activity log records why the credential
  could not be attached.
- Names the proxy owns are rejected: `Host`, `Content-Length`, `Transfer-Encoding`,
  `Connection`, `Upgrade` as header names, and `proxy_key` as a query parameter name.

## MCP Funnel

A route solves auth for one MCP server. The funnel solves the next problem: an agent pointed at
three servers sees all ninety of their tools, and there is no way to hand it only the six it
should have.

A **funnel** is a local MCP endpoint — `http://127.0.0.1:5559/mcp/<slug>` — that pools several
MCP servers and exposes a chosen subset of what they offer. Point one funnel at each agent:

- **Pooling** — one endpoint fronting Notion + GitHub + an internal server, instead of three.
- **Limiting** — 6 tools instead of 90. Smaller prompt, smaller blast radius.

Off by default. Turn it on with **Enable MCP funnel** on the MCP Funnel tab; while off, every
path under `/mcp` answers `404`.

### Sources

A **source** is one MCP server the funnel can draw from. Two kinds:

| Kind | What it is |
|---|---|
| **Route (credentialed)** | An MCP server reached through one of your routes. The route's OAuth token is attached automatically — this is the point of having the funnel inside this app rather than beside it. |
| **URL (no auth)** | Any MCP server that needs no credential. |

Press **Refresh** on a source to connect and read what it offers; the ticklists are populated
from that.

### Naming

Every name a source contributes is prefixed with that source's **alias**: a tool called
`create_issue` from a source aliased `gh` reaches the agent as `gh__create_issue`. Resources are
rewritten to `funnel://gh/<original-uri>` and mapped back on read.

Prefixing is always on, not only when two sources collide. Conditional prefixing would mean a
tool silently renames itself the day you add an unrelated source, breaking every agent prompt
that referred to it.

### Choosing what an agent sees

Per source, per kind (tools / resources / prompts):

| Mode | Behaviour |
|---|---|
| **All** | Everything, including anything the server gains later. |
| **Include** | Only what is ticked. A tool the server adds later stays hidden until you pick it. |
| **Exclude** | Everything except what is ticked. A tool the server adds later is exposed immediately. |

Include to grant a known set; Exclude to revoke a few from an otherwise trusted server.

Filtering is enforced on the **call** path as well as the listing — an agent that learned a name
before you unticked it, or simply guessed one, is refused, and the call never reaches the
upstream.

Edits apply on the agent's **next call**. No reconnect, no restart.

### Pointing an agent at a funnel

Same local API key as everything else:

```bash
curl -H "X-Proxy-Key: <your-key>" http://127.0.0.1:5559/mcp/coding-agent
```

The endpoint URL is shown on the MCP Funnel tab, selectable, ready to paste into an agent's
config.

### How it behaves

- **Each endpoint is independent.** Two funnels drawing on the same upstream get their own MCP
  session to it, so one agent's activity cannot perturb another's, and one expired session does
  not take both endpoints down.
- **Calls run in parallel**, both across endpoints and within one.
- **One dead source degrades only itself** — the other sources' tools still list, and the
  failure is shown on the source's row and in the activity log.
- **Arguments are never logged.** Tool names and outcomes are; the values an agent passes are
  not, because they routinely carry your data.
- `/mcp` is **reserved** — a route cannot claim it, and a request that already passed through a
  funnel is refused rather than allowed to loop.

### Limits

- Sources must be HTTP MCP servers. Local **stdio** servers (`npx …`) are not supported.
- Sampling, elicitation, and resource subscriptions are not offered on a funnel endpoint. The
  endpoint runs stateless, which is what makes a GUI edit take effect on the next call.
- Two agents connected to the *same* funnel share that funnel's upstream sessions. Give each
  agent its own funnel if they must be isolated from each other.
- A route-backed source whose server keys sessions on a **cookie** rather than the standard
  `Mcp-Session-Id` header cannot hold a session: the proxy strips `Cookie` on the way upstream,
  deliberately, so a caller cannot launder its own credentials through it.

## Calling the proxy (local API key)

Every proxied request must present the **local API key** shown in the Settings tab:

```bash
curl -H "X-Proxy-Key: <your-key>" http://127.0.0.1:5559/app/my-service/foo
```

For clients that cannot set headers (browser `EventSource`, used by some MCP SSE transports),
the key may instead be passed as a query parameter:

```
http://127.0.0.1:5559/app/my-service?token=abc&proxy_key=<your-key>
```

The key is removed before the request is forwarded — in **both** forms, header and query
parameter — so it never reaches the upstream's access log and never appears in the activity
log. Your own headers and query parameters (`token=abc` above) are passed through untouched.

Requests without a valid key get `403`.

**Why this exists.** Listening on `127.0.0.1` keeps other machines out, but it is not an
authorization boundary: every process on this computer, under any user account, can reach
loopback. Since the proxy attaches your live OAuth token to whatever it forwards, an
unguarded listener would hand your Google or Nextcloud session to any local program that
knew the port. The key also blocks *DNS rebinding*, where a page on an attacker's domain
re-resolves that name to `127.0.0.1` so the browser treats proxied responses as same-origin
and lets its JavaScript read them.

Alongside the key, the proxy refuses requests whose `Host` header is not loopback, refuses
requests carrying an `Origin` header (only browsers send one), and strips `Access-Control-*`
headers from upstream responses so a permissive upstream cannot re-open the same hole.

Use **Regenerate key** in Settings if the key is ever exposed; every client still using the
old one starts getting `403` immediately.

## Project layout

```
src/OAuthProxy.Core/     OAuth flows, encrypted storage, YARP proxy config, MCP funnel,
                          activity log — no WPF dependency, just the engine
src/OAuthProxy.App/      WPF tray app: hosts Kestrel+YARP in-process, tray icon, UI
tests/OAuthProxy.Core.Tests/   xunit tests for the Core project
```

`OAuthProxy.App` owns the process: it starts a Kestrel/YARP host on a background thread pool
task (not the WPF dispatcher thread — avoids a sync-over-async deadlock), maps the reverse
proxy, then initializes the tray icon. The proxy and the UI share one DI container.

## Building

```
dotnet build OAuthProxy.slnx -m:1
```

`-m:1` (no parallel MSBuild) avoids an intermittent WPF markup-compile race on a freshly
cleaned `obj/` that otherwise produces spurious `CS2001`/`MC1000` errors. `clean-build.bat`
already retries once if the first pass fails, for the same reason.

### Publishing a standalone exe

```
dotnet publish src/OAuthProxy.App/OAuthProxy.App.csproj -p:PublishProfile=win-x64-selfcontained -c Release
```

Produces a single self-contained `OAuthProxy.exe` (~180 MB, .NET runtime bundled) at
`src/OAuthProxy.App/bin/Release/net8.0-windows/publish/win-x64/`. No .NET install required on
the target machine. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) before redistributing
it — it bundles third-party components whose licenses require their notices travel along.

### Tests

```
dotnet test tests/OAuthProxy.Core.Tests/OAuthProxy.Core.Tests.csproj
```

## Data & logs

Everything lives under `%AppData%\OAuthProxy\`:

| Path | Contents |
|---|---|
| `store.dat` | DPAPI-encrypted credentials, upstreams, routes, MCP sources and funnels, settings |
| `store.dat.corrupt-<timestamp>` | Only if a store could not be decrypted or parsed at startup — see below |
| `logs\activity-YYYYMMDD.log` | Every proxied request/response, connect/refresh/disconnect, route reloads. Rotates every 2 days, auto-deletes after ~10 |
| `logs\errors.log` | Unhandled exceptions and provider errors, with full stack traces |

Settings tab has buttons to open either log, open the folder, or prune old logs. Activity
logs record request **paths** and query parameter **names**; parameter values are redacted,
and tokens are never logged. Control characters in a logged value are escaped (`\n`, `\x1b`)
so that one event can only ever produce one line — request paths reach the log percent-decoded,
so without this a caller could write whole fabricated entries.

At startup the log also flags any stored upstream or token endpoint that uses plain `http`
off-machine, as `STARTUP WARNING`. New entries are rejected at the point they are added, but
anything saved by an older build was never re-checked, and those put access tokens and client
secrets on the wire in cleartext.

If `store.dat` cannot be read — a truncated file after a hard power loss, or a profile copied
to another machine or user account, which DPAPI cannot decrypt — it is renamed aside as
`store.dat.corrupt-<timestamp>` and the app starts with empty config rather than failing to
start. The old file is kept, not deleted, in case it can still be recovered by the account
that wrote it. This is reported both in the activity log and in a dialog on launch: it means
every credential, upstream, and route is gone and a **new local API key** has been generated,
so every configured client will get 403 until it is updated.

### Encryption scope

`store.dat` is encrypted with DPAPI at `CurrentUser` scope. That protects it from other user
accounts, from backups, and from being read on another machine — but **not** from code running
as you. Any program in your own Windows session can ask DPAPI to decrypt it. That is the
ceiling for a desktop app with no master password; adding entropy would not raise it, since
the entropy would have to live in the binary.

## Autostart

Settings tab → **Start with Windows**. Writes an `HKCU\...\Run` registry entry pointing at
the current exe; never set automatically.

## License

MIT — see [LICENSE](LICENSE). Third-party dependencies (all MIT or Apache-2.0) are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which also covers what redistributing the
published exe requires.
