# Windows Package Manager (winget) submission

`winget install RavensPort` requires three YAML manifests in the community repository,
[microsoft/winget-pkgs][repo]. They live here in `packaging/winget/` so they are versioned with the
code that produced the installer they describe; submitting means copying them into a fork of that
repository and opening a pull request.

The Store submission (`docs/STORE-SUBMISSION.md`) is a different artifact entirely: that one ships
the MSIX and Microsoft signs it during ingestion. winget points at the **Inno Setup installer**
already attached to the GitHub release, and hosts nothing itself.

## What is in `packaging/winget/`

| File | Holds |
|---|---|
| `AbishekNarasimhan.RavensPort.yaml` | Version manifest — identifier, version, default locale |
| `AbishekNarasimhan.RavensPort.installer.yaml` | Installer URL, SHA256, type, scope, product code |
| `AbishekNarasimhan.RavensPort.locale.en-US.yaml` | Name, publisher, licence, description, tags |

The package identifier is **`AbishekNarasimhan.RavensPort`**. It is permanent: once a version is
merged, the identifier can only be changed by moving the package, which means a new PR and a
deprecation of the old one. The `Publisher.Package` shape is required, and the publisher half has to
match the `Publisher` field in the locale manifest with spaces removed.

Values that were not invented for the manifest, and where they came from:

- `MinimumOSVersion: 10.0.17763.0` — `MinVersion` in `installer/RavensPort.iss`, itself matching
  `SupportedOSPlatformVersion` in `Directory.Build.props`.
- `Scope: user` — `PrivilegesRequired=lowest` in the `.iss`, so `{autopf}` resolves under
  `%LocalAppData%\Programs` and the uninstall entry is written to HKCU. No elevation prompt.
- `ProductCode: {C47DF74F-150F-4AD3-9B12-46A8BF02BE9C}_is1` — Inno appends `_is1` to `AppId` for the
  Add or Remove Programs key. This is what lets `winget list` and `winget upgrade` recognise an
  install that did not come from winget. It has to track `AppId`, which never changes.
- `InstallerType: inno` — winget then supplies the silent switches itself
  (`/VERYSILENT /SP- /SUPPRESSMSGBOXES /NORESTART`); no `InstallerSwitches` block is needed.
- Only an `x64` installer is listed. `ArchitecturesAllowed=x64compatible` means it installs and runs
  on ARM64 Windows under emulation, and winget treats x64 as applicable on ARM64.

## Submitting a version

1. **Verify the manifests locally.**

   ```powershell
   winget validate --manifest packaging\winget
   ```

   Then a real install, which is what the pipeline's smoke test does:

   ```powershell
   winget settings --enable LocalManifestFiles   # once, elevated
   winget install --manifest packaging\winget
   winget uninstall RavensPort
   ```

2. **Fork and copy.** The path in the fork is fixed by the identifier and version — first letter of
   the publisher, lowercased, then the two halves of the identifier, then the version:

   ```
   manifests/a/AbishekNarasimhan/RavensPort/4.1.5/
   ```

3. **Open the PR** against `microsoft/winget-pkgs` `master`, one package version per PR. Azure
   Pipelines validation starts automatically: schema check, URL reachability, hash match, a
   static malware scan, and an unattended install/uninstall on a clean VM. It comments on the PR
   with anything it finds. A moderator reviews after that; a first-time publisher takes longer than
   later versions.

4. **Never edit a merged version.** The installer URL and hash for a published version are treated
   as immutable — a rebuilt 4.1.5 with a different hash has to go in as a new version, not as an
   amendment. Deleting the GitHub release asset for a merged version breaks that version for
   everyone.

`wingetcreate` automates steps 2 and 3, and can regenerate the installer manifest with a fresh hash:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update AbishekNarasimhan.RavensPort `
  --version 4.1.6 `
  --urls https://github.com/abishekvupputur/ravensPort/releases/download/v4.1.6/RavensPort-Setup-4.1.6.exe `
  --submit
```

It downloads the installer, computes the SHA256, and opens the PR. The same command runs
unattended from `release.yml` via the `microsoft/winget-create` action if this is ever worth
automating; it needs a PAT with `public_repo` on the fork.

## Where this package stands against the policies

Checked against the [Windows Package Manager policies][policies]. Nothing below is a blocker, but
two are worth knowing before the PR is open.

**Clear.** The installer is publicly downloadable with no account, login, or paywall. The URL is a
permanent GitHub release asset, not a "latest" redirect that changes content under a fixed address.
It installs unattended and requires no reboot. The submission is first-party, so the rights to
distribute are not in question, and the MIT licence is declared with a link. No telemetry, no
bundled offers, no separately-installed third-party components. Nothing in the name or description
implies a relationship with Microsoft.

**Worth knowing:**

- *The installer is not Authenticode-signed.* winget does not require code signing, and the
  validation pipeline accepts unsigned installers, but its static scan and SmartScreen's lack of
  reputation for a new unsigned binary can send the PR to manual review rather than straight
  through. This is the same gap that got the 4.1.5 Store submission rejected under policy 10.2.9 —
  see `docs/STORE-SUBMISSION.md` — with the difference that winget will still take it.
- *`winget upgrade` while RavensPort is running.* The installer refuses to replace a locked exe,
  which is deliberate, so an upgrade attempted while the tray app is up has to fail. What it must
  not do is fail silently. `AppMutex` in `[Setup]` aborted with exit code 1, indistinguishable from
  a corrupt download or the wrong architecture, so winget could only print a bare number. That
  check now lives in `[Code]` instead and aborts from `PrepareToInstall`, which exits with **7** —
  a code nothing else in Setup produces — and `ExpectedReturnCodes` maps 7 to `packageInUse`, so
  winget tells the user to close the application and try again. Measured on the real script:

  | Run | Mutex held | Exit |
  |---|---|---|
  | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` | yes | 7, nothing written, no ARP entry |
  | same | no | 0 |
  | silent uninstall | yes | 1, install left in place |
  | silent uninstall | no | 0 |

  An interactive run is turned away by `InitializeSetup` with a Retry/Cancel box instead, so the
  user is not made to click through the whole wizard first. **This landed after 4.1.5**, whose
  installer still returns 1, so the mapping only does anything from the next release onward —
  submit that one rather than 4.1.5, and refresh `PackageVersion`, `InstallerUrl`,
  `InstallerSha256`, `ReleaseDate` and `ReleaseNotesUrl` for it.

- *Do not submit prerelease tags.* `v4.1.4 - beta` and anything like it stays out; winget has no
  prerelease channel, so a beta would need its own identifier.

[repo]: https://github.com/microsoft/winget-pkgs
[policies]: https://learn.microsoft.com/en-us/windows/package-manager/package/windows-package-manager-policies
