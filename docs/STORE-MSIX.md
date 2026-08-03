# The Store MSIX package

How the Microsoft Store package is built and where it is published. The EXE/MSI submission is
documented separately in [STORE-SUBMISSION.md](STORE-SUBMISSION.md); nothing about the installer,
`release.yml`, or the `dist/` blob changes because of any of this.

## Why there is an MSIX at all

The EXE submission was rejected under policy 10.2.9:

> The binary and all of its Portable Executable (PE) files must be digitally signed with a code sign
> certificate that chains up to a certificate issued by a Certificate Authority (CA) that is part of
> the Microsoft Trusted Root Program.

That covers `RavensPort-Setup-<version>.exe` **and every PE inside it** - `RavensPort.exe` and the
bundled `onepassword.dll` - so signing the outer installer alone would not have satisfied it. The
three ways to comply:

| Route | Cost | What it means here |
|---|---|---|
| Azure Trusted Signing | ~$10/month plus identity validation | Keeps the EXE submission exactly as it is |
| OV certificate from a CA | ~$200-400/year, key on a hardware token or cloud HSM | Same, with more moving parts in CI |
| **Ship MSIX instead** | **free** | **Microsoft signs the package at ingestion** |

MSIX is what is implemented, and it is the route Microsoft's own rejection notice offers.

## The package is deliberately unsigned

Do not sign it. Partner Center accepts an unsigned `.msix` and signs it during ingestion with a
certificate chaining to the Microsoft Trusted Root Program - which is the entire point. A package
signed locally is *rejected*, because the certificate subject would not match the publisher Partner
Center has on file.

## One-time setup

Three things, in this order. The third is effectively irreversible.

### 1. Partner Center

MSIX and EXE/MSI are different app types and cannot share a reserved name.

1. Delete the app name from the existing Win32 (EXE/MSI) app. Microsoft's notice calls this out
   explicitly: the name cannot be reserved twice.
2. Create a new app of the MSIX type and reserve `RavensPort` for it.

### 2. The release repository

The workflow publishes to [`abishekvupputur/ravensPort-release`](https://github.com/abishekvupputur/ravensPort-release),
not to this repository.

- That repository needs **at least one commit on `main`**. A GitHub release must point at a
  commitish, and no commit from this repository exists over there, so the workflow creates the tag
  on the release repository's default branch instead. A repository with no commits has no branch to
  create it on and the step fails.
- `GITHUB_TOKEN` is scoped to the repository a workflow runs in and cannot write to another one.
  Create a **fine-grained personal access token** with `Contents: read and write`, scoped to
  `ravensPort-release` **only**, and add it to this repository as the secret
  **`RELEASE_REPO_TOKEN`**. Nothing else in this repository reads that secret, and no other workflow
  needs it.

### 3. Package identity

Open **Product management -> Product identity** in Partner Center and copy three values into
[../packaging/AppxManifest.xml](../packaging/AppxManifest.xml), replacing the `FILLMEIN` ones:

| Partner Center | Manifest |
|---|---|
| `Package/Identity/Name` | `<Identity Name="...">` |
| `Package/Identity/Publisher` | `<Identity Publisher="CN=...">` |
| `Package/Properties/PublisherDisplayName` | `<PublisherDisplayName>` |

All three must match exactly or the upload is rejected, and **the identity cannot be changed after
the first accepted submission**. `build-msix.ps1` checks the parsed values, not the file text, and
refuses to pack while any placeholder survives.

## Cutting a release

```
git tag MSIX-v4.1.5
git push origin MSIX-v4.1.5
```

Tags are case-sensitive: `MSIX-v4.1.5`, not `msix-v4.1.5`. The workflow runs the test suite first
and stops on failure, then publishes, packs, attests, and creates the release in
`ravensPort-release` with `RavensPort-4.1.5.msix` attached. Download that asset and upload it to
Partner Center.

The version comes off the tag, so `MSIX-v4.1.5` produces a `4.1.5.0` package. MSIX wants four parts
and the Store reserves the fourth, so `build-msix.ps1` pads rather than letting you set it.

Separate from the `v*` installer tag on purpose: a Store resubmission no longer needs a new
installer release, and an ordinary release no longer builds a 100 MB package nobody asked for.

### Provenance

The attestation is recorded against **this** repository even though the asset lives in the release
repository, so verification names this one:

```bash
gh attestation verify RavensPort-4.1.5.msix --repo abishekvupputur/ravensPort
```

## Building locally

```powershell
dotnet publish src/RavensPort.App/RavensPort.App.csproj -p:PublishProfile=win-x64-msix -c Release

./packaging/build-msix.ps1 -Version 4.1.5 `
  -PublishDir 'src/RavensPort.App/bin/Release/net8.0-windows/publish/win-x64-msix'
```

Measured against Windows SDK 10.0.22621: a 249 MB layout of 644 files packs to a **99.3 MB** package
in about a minute. Output goes to `packaging/obj/`, which the existing blanket `obj/` ignore rule
already covers, so a local build never leaves an untracked blob in a tracked directory. `-SkipPack`
builds the layout and stops, and downgrades the placeholder-identity error to a warning - enough to
catch a broken manifest or a moved logo without a Partner Center identity or a minute of packing.

What the script does, and why:

- **Visual assets are generated, not committed.** All six logos are resized from the existing
  1080x1080 `src/RavensPort.App/Assets/logo.png`, so the tile, the taskbar icon and the Store
  listing cannot drift from the icon the app itself uses.
- **The version is injected** into a copy of the manifest in the layout. The committed file keeps
  `0.0.0.0`.
- **`resources.pri` is generated** by `makepri`, best effort. Not strictly required for a package
  with no MRT-qualified resources, but ingestion is happier with a package shaped like the ones it
  usually sees.

### Why this publish is not single-file

Every other publish here uses `PublishSingleFile`; the MSIX one deliberately does not. MSIX
compresses and block-dedupes its own payload, so a Store update downloads only the blocks that
changed - which relinking a single-file bundle defeats, because it rewrites all of them. It also
drops the extract-to-temp step single-file performs on every cold start. Self-contained is not
optional: there is no MSIX framework package for the .NET desktop runtime, so a framework-dependent
package would install and then fail to start with no way for the Store to satisfy the dependency.

## What running under MSIX changes

The package declares `EntryPoint="Windows.FullTrustApplication"` with the `runFullTrust` capability,
so the app runs outside AppContainer with the user's own rights. Everything that would otherwise be
a problem keeps working unchanged:

- Kestrel and `HttpListener` bind to `127.0.0.1` - loopback is only blocked for AppContainer apps.
- `pass-cli` still downloads and runs as a child process.
- The Go `onepassword.dll` still P/Invokes.
- The `RavensPort_SingleInstance` mutex still works.

One real difference: **the MSIX container redirects `%APPDATA%` and `%LOCALAPPDATA%` writes** into
`%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\`. A user moving from the Inno installer to
the Store build therefore starts with fresh local state - settings, activity log, and the downloaded
`pass-cli`, which is fetched again once. Nothing irreplaceable lives there, because the
configuration itself is in the user's password manager, but it belongs in the release notes.

Worth knowing before review: `ProtonPassInstaller` downloads pass-cli from GitHub on the user's
say-so, pinned by SHA-256. That is user-initiated installation of an optional dependency rather than
remote code that changes app behaviour, and it passed the earlier reviews, but MSIX review looks at
this more closely.

## What a reviewer sees

The findings that produced the installer (10.1.2.10, 10.2.7, 10.3.4) are satisfied by the package
itself rather than by anything RavensPort does:

1. Install and uninstall are handled by the Store.
2. **RavensPort** appears in Settings -> Apps -> Installed apps.
3. A **RavensPort** Start menu entry comes from the manifest's `<Application>`.
4. Launching shows the app window - not just a tray icon.
5. Tray -> Exit quits; launching again brings it straight back.
6. Launching while it is already running brings the existing window forward.

Points 4-6 are app behaviour, fixed alongside the installer, and still worth walking on a machine
that has never had RavensPort installed.

The listing still has no screenshot meeting Partner Center's 1366x768 minimum; see the Screenshots
section of [STORE-LISTING.md](STORE-LISTING.md). That blocks the listing independently of any of
this.
