# Microsoft Store submission (EXE/MSI app type)

What to put in Partner Center, and why the shape of the submission changed.

## What failed, and why it was one cause

The 08/03/2026 review (Product ID `1fab64a5-4d87-4cff-9df1-db83143fef01`) returned three findings.
All three came from submitting `RavensPort-<tag>.exe` — the **application** — where the Store
expected an **installer**.

| Policy | Finding | Cause |
|---|---|---|
| 10.3.4 | "failed to install through the Store" | The Store runs the submitted file with the declared silent switches and then looks for the product on the machine. Nothing installed, so nothing was found. |
| 10.2.7 | No Add or Remove Programs entry | Nothing wrote an uninstall key, because nothing installed. |
| 10.1.2.10 | "no accessible method of being launched" | No Start menu shortcut, and the app started hidden in the tray. After tray → Exit there was no way back short of finding the exe on disk. |

Fixed by `installer/RavensPort.iss`, which produces a real per-user installer, and by two app
changes: the main window is now shown on launch, and a second launch brings the running instance's
window to the front instead of showing a "look in the tray" message box.

## Submit this file

`RavensPort-Setup-<version>.exe`, built by the release workflow and attached to the GitHub release.
**Not** `RavensPort-<tag>.exe` — that is still published for people who want to run the app without
installing it, and it is still not an installer.

The Store requires a redirect-free download URL. GitHub *release* asset URLs redirect to
`objects.githubusercontent.com`, so the installer has to be committed as a blob in this repository
and served from `raw.githubusercontent.com`, exactly as `dist/RavensPort-v3.0.2.exe` was before:

```
https://raw.githubusercontent.com/abishekvupputur/ravensPort/main/dist/RavensPort-Setup-<version>.exe
```

Commit the new installer to `dist/` and delete the superseded one in the same commit. Note that
`dist/RavensPort-v3.0.2.exe` is currently three minor versions behind `Directory.Build.props`
(4.1.3) — whatever is submitted must be the version the listing claims.

## Partner Center answers

| Field | Value |
|---|---|
| Installer type | EXE |
| Silent install | `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-` |
| Silent uninstall | `"%LOCALAPPDATA%\Programs\RavensPort\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART` |
| Successful install exit code | `0` |
| Requires elevation | No |
| Install scope | Per-user |
| Install location | `%LOCALAPPDATA%\Programs\RavensPort` |
| ARP display name | `RavensPort` |
| ARP registry key | `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C47DF74F-150F-4AD3-9B12-46A8BF02BE9C}_is1` |
| Minimum OS | Windows 10 1809 (10.0.17763), x64 |

`PrivilegesRequired=lowest` is deliberate. A per-user install never raises UAC, and a silent
install that cannot raise UAC is a silent install that cannot fail on it. Nothing in RavensPort
needs machine scope: the configuration lives in the user's password manager and the session key is
already bound to the Windows account.

## What a reviewer will now see

1. Install runs unattended and returns 0.
2. **RavensPort** appears in Settings → Apps → Installed apps, with a working Uninstall.
3. A **RavensPort** shortcut appears in the Start menu.
4. Launching shows the app window — not just a tray icon.
5. Tray → Exit quits. Launching from the Start menu again brings it straight back.
6. Launching while it is already running brings the existing window forward.

## Re-verifying before resubmission

The installer is compiled in CI, so the first tag push after this change is what proves the script
compiles. To check locally, install Inno Setup 6 and run:

```powershell
dotnet publish src/RavensPort.App/RavensPort.App.csproj -p:PublishProfile=win-x64-selfcontained -c Release
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer\RavensPort.iss /DAppVersion=4.1.3
```

Then walk points 1–6 above on a machine that has never had RavensPort installed. Point 1 in
particular cannot be checked by running the installer interactively — use the silent switches.

## Not addressed here

The listing still has no screenshot meeting Partner Center's 1366x768 minimum; see the Screenshots
section of [STORE-LISTING.md](STORE-LISTING.md). That was not among the three findings, but it will
block the listing separately.
