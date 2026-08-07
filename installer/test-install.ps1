<#
.SYNOPSIS
    Installs, launches, and uninstalls the built installer, asserting what winget asserts.

.DESCRIPTION
    Reproduces the winget validation pipeline's dynamic test, which is where a package fails with
    Validation-Unattended-Failed, Validation-Executable-Error, Validation-Uninstall-Error, or
    Version-Parameter-Mismatch. A GitHub runner is ephemeral, so it is already the clean machine
    that test wants; Windows Sandbox, which winget's own SandboxTest.ps1 uses, needs nested
    virtualisation and is not available on hosted runners.

    The switches below are the ones winget passes an installer declared as InstallerType: inno.
    They are written out rather than taken from a variable so that a change in winget's defaults
    shows up here as a difference to reconcile, not as a silent divergence.

.OUTPUTS
    Nothing on success. Throws on the first assertion that fails.
#>
[CmdletBinding()]
param(
    # The installer to exercise. Relative to the repository root, or absolute.
    [Parameter(Mandatory)]
    [string] $SetupExe,

    # Digits only, no leading "v". Checked against what the installer writes to the registry,
    # which is the comparison behind winget's Version-Parameter-Mismatch label.
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+(\.\d+){0,3}$')]
    [string] $Version
)

# ASCII only in this file, for the same reason as build.ps1: Windows PowerShell 5.1 reads a
# BOM-less .ps1 as ANSI and a stray em dash becomes mojibake that breaks the parser.
$ErrorActionPreference = 'Stop'

# Inno appends "_is1" to AppId. PrivilegesRequired=lowest puts the entry under HKCU and resolves
# {autopf} to %LocalAppData%\Programs, so every path here is per-user. All four constants track
# RavensPort.iss; nothing checks them against it at build time.
$arpKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C47DF74F-150F-4AD3-9B12-46A8BF02BE9C}_is1'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\RavensPort'
$exePath = Join-Path $installDir 'RavensPort.exe'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\RavensPort.lnk'

$silentArgs = @('/VERYSILENT', '/SP-', '/SUPPRESSMSGBOXES', '/NORESTART')

$repoRoot = Split-Path -Parent $PSScriptRoot
$setup = if ([System.IO.Path]::IsPathRooted($SetupExe)) { $SetupExe } else { Join-Path $repoRoot $SetupExe }
if (-not (Test-Path $setup)) { throw "Installer not found: $setup" }
$setup = (Resolve-Path $setup).Path

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    Write-Host "  ok: $message"
}

# A leftover install from an earlier run would make every assertion below meaningless -- the files
# would already be there whether or not this installer put them there.
if (Test-Path $arpKey) {
    throw "RavensPort is already installed on this machine ($arpKey exists). This test needs a clean machine."
}

# --- install ------------------------------------------------------------------------------------
Write-Host "Installing: $setup $($silentArgs -join ' ')"
$proc = Start-Process -FilePath $setup -ArgumentList $silentArgs -Wait -PassThru
Assert ($proc.ExitCode -eq 0) "silent install exited 0 (got $($proc.ExitCode))"

Assert (Test-Path $exePath) "RavensPort.exe is at $exePath"
Assert (Test-Path $shortcut) 'Start menu shortcut was created'
Assert (Test-Path (Join-Path $installDir 'LICENSE')) 'LICENSE was installed alongside the exe'
# Not cosmetic. The exe statically links BSD-3-Clause and Apache-2.0 components -- the Go runtime
# and everything inside onepassword.dll among them -- and both licences require their notices to
# accompany a binary redistribution. This file beside the exe is how that is satisfied, so a
# packaging change that quietly dropped it would put the installer out of compliance.
Assert (Test-Path (Join-Path $installDir 'THIRD-PARTY-NOTICES.md')) 'THIRD-PARTY-NOTICES.md was installed alongside the exe'
Assert (Test-Path $arpKey) 'Add or Remove Programs entry was written'

# The version winget compares against PackageVersion. A mismatch here is the whole of
# Version-Parameter-Mismatch.
$arp = Get-ItemProperty $arpKey
Write-Host "  ARP DisplayName='$($arp.DisplayName)' DisplayVersion='$($arp.DisplayVersion)' Publisher='$($arp.Publisher)'"
Assert ($arp.DisplayName -eq 'RavensPort') "ARP DisplayName is RavensPort (got '$($arp.DisplayName)')"
Assert ($arp.DisplayVersion -eq $Version) "ARP DisplayVersion is $Version (got '$($arp.DisplayVersion)')"

# --- launch -------------------------------------------------------------------------------------
# winget runs the installed application to be sure nothing suspicious starts and that it does not
# fall over immediately. RavensPort is tray-resident and needs a password manager CLI it will not
# find here, so the bar is that it stays up and shows its setup UI rather than exiting.
Write-Host 'Launching...'
$app = Start-Process -FilePath $exePath -PassThru
Start-Sleep -Seconds 15
$app.Refresh()
if ($app.HasExited) {
    throw "RavensPort exited on its own with code $($app.ExitCode) within 15 seconds of launch. winget reports this as Validation-Executable-Error."
}
Write-Host '  ok: still running after 15s'

# Stopped, not asked to close: the tray app has no console to signal and CloseMainWindow would
# race the setup window. The uninstall below is what actually has to succeed.
Stop-Process -Id $app.Id -Force
# The single-instance mutex is released when the process dies, but the handle close is not
# instantaneous, and the uninstaller refuses while it is held -- see the [Code] section in
# RavensPort.iss. Waiting here keeps that from looking like an uninstall failure.
Start-Sleep -Seconds 5

# --- uninstall ----------------------------------------------------------------------------------
$uninstaller = Join-Path $installDir 'unins000.exe'
Assert (Test-Path $uninstaller) 'uninstaller is present'

Write-Host 'Uninstalling...'
$un = Start-Process -FilePath $uninstaller -ArgumentList $silentArgs -Wait -PassThru
Assert ($un.ExitCode -eq 0) "silent uninstall exited 0 (got $($un.ExitCode))"

# Inno's uninstaller copies itself to temp and relaunches, so the process this waited on can exit
# before the work is finished. Poll rather than assume.
$deadline = (Get-Date).AddSeconds(60)
while ((Test-Path $arpKey) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }

Assert (-not (Test-Path $arpKey)) 'Add or Remove Programs entry was removed'
Assert (-not (Test-Path $exePath)) 'RavensPort.exe was removed'
Assert (-not (Test-Path $shortcut)) 'Start menu shortcut was removed'

Write-Host ''
Write-Host 'Install, launch, and uninstall all behaved as winget expects.'
