# Third-Party Notices

OAuthProxy itself is licensed under the MIT License (see [LICENSE](LICENSE)).

It depends on the third-party components listed below. The `win-x64` self-contained
single-file build **bundles these components (and the .NET runtime) into the produced
`OAuthProxy.exe`**, so any redistribution of that binary is a redistribution of these
components and must carry these notices.

All components are under permissive licenses (MIT or Apache-2.0). None are copyleft;
none impose source-disclosure obligations on OAuthProxy.

## Runtime dependencies (bundled in the published executable)

| Component | Version | License |
|---|---|---|
| .NET Runtime / ASP.NET Core / WPF (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`, `Microsoft.WindowsDesktop.App`) | 8.0.x | MIT |
| Yarp.ReverseProxy | 2.3.0 | MIT |
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

## Build/test-only dependencies (not shipped in the executable)

| Component | Version | License |
|---|---|---|
| xunit (and xunit.core / xunit.assert / xunit.runner.visualstudio) | 2.5.3 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT |
| coverlet.collector | 6.0.0 | MIT |

## What the licenses require of you

**MIT** — include the copyright notice and permission notice when redistributing.

**Apache-2.0** — when redistributing, include a copy of the Apache-2.0 license, retain
existing copyright/patent/attribution notices, and state significant changes if you
modified the component. Apache-2.0 also grants an explicit patent licence, which MIT
does not. If an upstream component ships a `NOTICE` file, its contents must be passed
along; OAuthProxy does not modify any of these components.

Full licence texts:

- MIT: https://opensource.org/licenses/MIT
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0
