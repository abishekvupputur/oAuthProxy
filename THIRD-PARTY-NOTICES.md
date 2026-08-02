# Third-Party Notices

RavensPort itself is licensed under the MIT License (see [LICENSE](LICENSE)).

It depends on the third-party components listed below. The `win-x64` self-contained
single-file build **bundles these components (and the .NET runtime) into the produced
`RavensPort.exe`**, so any redistribution of that binary is a redistribution of these
components and must carry these notices.

Everything **bundled** in that binary is under a permissive license (MIT or Apache-2.0),
and none of it imposes source-disclosure obligations on RavensPort.

One component is **not** bundled and **is** copyleft — the Proton Pass CLI, which
RavensPort can download at your request. See [Optional external
tools](#optional-external-tools-not-bundled) below.

## Runtime dependencies (bundled in the published executable)

| Component | Version | License |
|---|---|---|
| .NET Runtime / ASP.NET Core / WPF (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) | 8.0.x | MIT |
| Yarp.ReverseProxy | 2.3.0 | MIT |
| ModelContextProtocol | 2.0.0 | Apache-2.0 |
| ModelContextProtocol.Core | 2.0.0 | Apache-2.0 |
| ModelContextProtocol.AspNetCore | 2.0.0 | Apache-2.0 |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT |
| System.Net.ServerSentEvents | 10.0.10 | MIT |
| Google.Apis.Auth | 1.75.0 | Apache-2.0 |
| Google.Apis | 1.75.0 | Apache-2.0 |
| Google.Apis.Core | 1.75.0 | Apache-2.0 |
| IdentityModel.OidcClient | 6.0.0 | Apache-2.0 |
| IdentityModel | 7.0.0 | Apache-2.0 |
| Newtonsoft.Json | 13.0.4 | MIT |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| H.NotifyIcon.Wpf | 2.4.1 | MIT |
| H.NotifyIcon | 2.4.1 | MIT |
| H.GeneratedIcons.System.Drawing | 2.4.1 | MIT |
| System.Security.Cryptography.ProtectedData | 10.0.0 | MIT |
| System.Drawing.Common | MIT |
| Microsoft.Win32.SystemEvents | MIT |
| System.CodeDom | MIT |
| System.Management | MIT |
| 1Password SDK (onepassword-sdk-go) | 0.4.1 | MIT |

## Build/test-only dependencies (not shipped in the executable)

| Component | Version | License |
|---|---|---|
| xunit (and xunit.core / xunit.assert / xunit.runner.visualstudio) | 2.5.3 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT |
| coverlet.collector | 6.0.0 | MIT |

## Optional external tools (not bundled)

RavensPort stores its configuration in a password manager. For Proton Pass, it reaches it by running the
`pass-cli` command-line tool as a separate child process. This tool is **not** part of
`RavensPort.exe` and is **not** redistributed with it.

| Component | License | How it is obtained |
|---|---|---|
| Proton Pass CLI (`pass-cli`) | **GPL-3.0-or-later** | Installed by you, or downloaded from Proton's official release on your explicit request |

### Proton Pass CLI and the GPL

If you use the "Download it for me" button, RavensPort fetches this exact release:

- Version: **2.2.4**
- File: `pass-cli-windows-x86_64.zip`
- SHA-256: `8077bbfed54842305dbdef2744bddaa368fd36b349ce9e2c406a598c82e38d77`
- From: <https://github.com/protonpass/pass-cli/releases/tag/2.2.4>

Corresponding source for that exact version is the tag above, at
<https://github.com/protonpass/pass-cli>. RavensPort does **not** modify pass-cli, link
against it, or incorporate any part of it — it is executed as an independent program over
a process boundary, which is aggregation rather than a combined work. RavensPort itself
therefore remains under the MIT License.

The archive contains `pass-cli.exe` and `libcrypto-3-x64.dll`; both are extracted
unmodified, and the download is rejected outright if its SHA-256 does not match the value
above.

## What the licenses require of you

**MIT** — include the copyright notice and permission notice when redistributing.

**Apache-2.0** — when redistributing, include a copy of the Apache-2.0 license, retain
existing copyright/patent/attribution notices, and state significant changes if you
modified the component. Apache-2.0 also grants an explicit patent licence, which MIT
does not. If an upstream component ships a `NOTICE` file, its contents must be passed
along; RavensPort does not modify any of these components.

Full licence texts:

- MIT: https://opensource.org/licenses/MIT
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
