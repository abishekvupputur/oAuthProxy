<#
.SYNOPSIS
    Scans a built installer with Microsoft Defender and fails if anything is detected.

.DESCRIPTION
    Stands in for the winget validation pipeline's antimalware step, which is Defender-based and
    is the one check most likely to reject this package: an unsigned, self-contained single-file
    .NET executable that wraps a Go c-shared DLL, opens listening sockets on 127.0.0.1, and shells
    out to password manager CLIs. Every part of that is legitimate and the combination is exactly
    what heuristics are built to notice. Finding out here costs a build; finding out on a
    winget-pkgs pull request costs a resubmission and a false-positive report to Microsoft.

    Not a substitute for the multi-engine scan winget runs -- Defender is one engine -- but it is
    the engine whose verdict decides the submission.

.OUTPUTS
    Nothing on success. Throws on detection or on being unable to scan at all.
#>
[CmdletBinding()]
param(
    # The installer to scan. Relative to the repository root, or absolute.
    [Parameter(Mandatory)]
    [string] $Path
)

# ASCII only in this file. Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a stray em
# dash here becomes three bytes of mojibake and takes the parser down with it.
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$target = if ([System.IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repoRoot $Path }
if (-not (Test-Path $target)) { throw "Installer not found: $target" }
$target = (Resolve-Path $target).Path

# The copy under Platform\<version> is the one that gets updated; the one in Program Files is a
# stub that can lag several releases behind. Newest version wins, and the stub is only a fallback
# for images that have no Platform directory at all.
#
# The directory name carries a build suffix -- "4.18.25080.5-0" -- which [version] cannot parse, so
# only the part before the dash is used for ordering. Anything unparseable sorts to the bottom
# rather than throwing, because one oddly-named directory should not stop the scan.
$mpCmdRun = Get-ChildItem "$env:ProgramData\Microsoft\Windows Defender\Platform\*\MpCmdRun.exe" -ErrorAction SilentlyContinue |
    Sort-Object { try { [version](($_.Directory.Name -split '-')[0]) } catch { [version]'0.0' } } |
    Select-Object -Last 1 -ExpandProperty FullName
if (-not $mpCmdRun) {
    $fallback = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path $fallback) { $mpCmdRun = $fallback }
}
if (-not $mpCmdRun) {
    throw 'MpCmdRun.exe was not found, so the installer went unscanned. Failing rather than reporting a clean result that was never produced.'
}

Write-Host "MpCmdRun:  $mpCmdRun"
Write-Host ("Target:    {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))

# Definitions on a runner image are as old as the image. Scanning against them would mostly prove
# the image is stale. Non-fatal, because an offline or rate-limited update should not turn into a
# build failure -- but say so loudly, since the scan below is then worth less.
Write-Host 'Updating signatures...'
& $mpCmdRun -SignatureUpdate | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Signature update failed with exit code $LASTEXITCODE. Scanning with whatever definitions the image shipped."
}

# ScanType 3 is a custom scan of one path. -DisableRemediation is what keeps this a report rather
# than an action: without it a detection quarantines the file, and every later step fails on a
# missing artifact instead of on the actual finding.
Write-Host 'Scanning...'
$output = & $mpCmdRun -Scan -ScanType 3 -File $target -DisableRemediation 2>&1
$scanExit = $LASTEXITCODE
$output | Out-Host

# MpCmdRun returns 2 both for "found something" and for "could not run", which are opposite
# results and must not be reported as the same thing. The distinguishing evidence is in the output:
# a scan that failed says so with an hr, and a machine with Defender switched off -- a third-party
# antivirus is the usual reason -- says "Product/Feature disabled".
#
# Both still fail the build. An installer that went unscanned has not been cleared, and passing it
# off as clean is the one outcome this script exists to prevent. But the message has to say which
# happened, because "fix your binary" and "fix your scanner" are not the same instruction.
$text = ($output | Out-String)
$scanFailed = $text -match 'Failed with hr|hr = 0x8|Product/Feature disabled'

if ($scanExit -eq 0 -and -not $scanFailed) {
    Write-Host 'Defender found no threats.'
} elseif ($scanFailed) {
    throw @"
The scan did not run, so $([System.IO.Path]::GetFileName($target)) is unscanned rather than clean.

MpCmdRun exited with $scanExit and reported an error rather than a result. Two causes account for
almost all of these:

  * Defender is disabled on this machine, usually because another antivirus product has taken
    over. The log line is "WARN: Product/Feature disabled".
  * The shell is not elevated. A custom scan needs administrator rights.

See $env:TEMP\MpCmdRun.log for the full output. On a GitHub runner neither applies -- Defender is
enabled and the agent is elevated -- so this is expected to be a local-only failure.
"@
} else {
    throw @"
Microsoft Defender flagged $([System.IO.Path]::GetFileName($target)) (MpCmdRun exit $scanExit).

This is what would come back from winget as Binary-Validation-Error (static scan) or
Validation-Defender-Error (post-install scan). If the detection is a false positive, submit the
installer at https://www.microsoft.com/wdsi/filesubmission before opening or updating the
winget-pkgs pull request.
"@
}
