<#
.SYNOPSIS
    Packs the published app into the unsigned MSIX submitted to the Microsoft Store.

.DESCRIPTION
    The counterpart to installer/build.ps1, and shared by the PR and release workflows for the same
    reason: so the thing CI builds is the thing that was tested. The PR build passes -SkipPack to
    prove the layout, the assets and the manifest still come together without spending a minute
    compressing 240 MB nobody will install.

    The output is intentionally NOT signed. Store policy 10.2.9 wants every PE signed by a CA in
    the Microsoft Trusted Root Program; for MSIX, Microsoft does that itself at ingestion, free.
    Signing here would instead break the upload, because the certificate subject would no longer
    match the publisher Partner Center has on file. See packaging/AppxManifest.xml.

.OUTPUTS
    The path of the .msix it produced, relative to the repository root.
#>
[CmdletBinding()]
param(
    # Digits only, like installer/build.ps1. Normalised to the four parts MSIX requires.
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+(\.\d+){0,3}$')]
    [string] $Version,

    # The publish *directory* to package -- not a single exe, unlike the installer. Relative to the
    # repository root, or absolute.
    [Parameter(Mandatory)]
    [string] $PublishDir,

    # Identity overrides, for local sideload builds only.
    #
    # The committed manifest carries FILLMEIN placeholders until Partner Center issues the real
    # values, and packing refuses to proceed while they survive -- so a local build needs some
    # identity to use, and editing the committed file to get one invites that edit being committed
    # by accident. These write into the layout's copy instead; the file in packaging/ is never
    # modified.
    #
    # A sideload package must also be signed, and the signing certificate's subject has to match
    # -Publisher exactly or signtool refuses. Never pass these for a Store build: the Store package
    # must carry the identity Partner Center issued, and nothing else.
    [string] $IdentityName,
    [string] $Publisher,
    [string] $PublisherDisplayName,

    # Where the .msix lands. Under packaging/obj by default, which .gitignore's blanket obj/ rule
    # already covers, so a local build never drops a 100 MB untracked blob into a tracked directory.
    # Unlike the installer, this artifact is never committed: it goes to Partner Center, and to the
    # release repository, both of which host it themselves.
    [string] $OutputDir = 'packaging/obj',

    # Builds the layout and stops. Enough to catch a broken manifest or a missing payload.
    [switch] $SkipPack
)

$ErrorActionPreference = 'Stop'

# ASCII only in this file. Windows PowerShell 5.1 reads a BOM-less .ps1 as ANSI, so a stray em dash
# here becomes three bytes of mojibake and takes the parser down with it.

$repoRoot = Split-Path -Parent $PSScriptRoot

# The Store reserves the fourth part of the version for its own use and rejects a package that sets
# it, so the tag's three parts are padded rather than passed through.
$parts = @($Version.Split('.'))
while ($parts.Count -lt 3) { $parts += '0' }
if ($parts.Count -eq 4 -and $parts[3] -ne '0') {
    throw "MSIX version revision must be 0 (the Store reserves it), but -Version was $Version."
}
$packageVersion = ($parts[0..2] -join '.') + '.0'

$payload = if ([System.IO.Path]::IsPathRooted($PublishDir)) { $PublishDir } else { Join-Path $repoRoot $PublishDir }
if (-not (Test-Path $payload -PathType Container)) {
    throw "Publish directory not found: $payload (publish must run first)"
}
$payload = (Resolve-Path $payload).Path
if (-not (Test-Path (Join-Path $payload 'RavensPort.exe'))) {
    throw "RavensPort.exe is not in $payload. Was this published with the win-x64-msix profile?"
}

# --- SDK tools -------------------------------------------------------------------------------
# makeappx/makepri ship in the Windows SDK, which is on the GitHub windows runner images but never
# on PATH. Highest version wins, because an older one predates features newer manifests use.
function Find-SdkTool([string] $name) {
    $tool = (Get-Command $name -ErrorAction SilentlyContinue).Source
    if ($tool) { return $tool }

    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:ProgramFiles\Windows Kits\10\bin")
    $found = foreach ($root in $roots) {
        if (Test-Path $root) {
            Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
                ForEach-Object {
                    # The version is carried alongside rather than parsed back out of the path,
                    # which is two directories up from the tool and easy to get wrong.
                    [pscustomobject]@{ Version = [version]$_.Name; Path = Join-Path $_.FullName "x64\$name" }
                } |
                Where-Object { Test-Path $_.Path }
        }
    }
    # Sort by the SDK version, not lexically: 10.0.9 must not beat 10.0.22621.
    ($found | Sort-Object Version | Select-Object -Last 1).Path
}

$makeappx = Find-SdkTool 'makeappx.exe'
if (-not $makeappx -and -not $SkipPack) {
    throw 'makeappx.exe was not found. Install the Windows 10/11 SDK, or add it to PATH.'
}
$makepri = Find-SdkTool 'makepri.exe'

# --- Layout ----------------------------------------------------------------------------------
$layout = Join-Path $PSScriptRoot 'obj\layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path $layout -Force | Out-Null

Write-Host "Payload:     $payload"
Write-Host "Layout:      $layout"
Write-Host "Version:     $packageVersion"

Copy-Item (Join-Path $payload '*') $layout -Recurse -Force

# The licence and the notices ride along exactly as they do in the installer's [Files] section.
Copy-Item (Join-Path $repoRoot 'LICENSE') $layout -Force
Copy-Item (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') $layout -Force

# --- Visual assets -------------------------------------------------------------------------
# Generated from the 1080x1080 app logo rather than committed as eight more PNGs, so the tile, the
# taskbar icon and the Store listing can never drift from the icon the app itself uses.
Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null

$logoPath = Join-Path $repoRoot 'src\RavensPort.App\Assets\logo.png'
if (-not (Test-Path $logoPath)) { throw "Source logo not found: $logoPath" }
$logo = [System.Drawing.Image]::FromFile($logoPath)

function Write-Asset([string] $fileName, [int] $width, [int] $height) {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

        # The source is square. Fit it inside the target and centre it, so the wide tile gets a
        # centred mark on transparency instead of a stretched one.
        $side = [Math]::Min($width, $height)
        $graphics.DrawImage($script:logo, [int](($width - $side) / 2), [int](($height - $side) / 2), $side, $side)
    }
    finally {
        $graphics.Dispose()
    }
    $bitmap.Save((Join-Path $assetsDir $fileName), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

try {
    # Every logo the manifest references, at the size the manifest names it. Scale-qualified
    # variants (logo.scale-200.png and friends) are deliberately absent: they only resolve through
    # an MRT lookup, and the shell downscales these cleanly enough that the extra machinery is not
    # worth the failure modes it adds.
    Write-Asset 'StoreLogo.png'          50   50
    Write-Asset 'Square44x44Logo.png'    44   44
    Write-Asset 'Square71x71Logo.png'    71   71
    Write-Asset 'Square150x150Logo.png'  150  150
    Write-Asset 'Square310x310Logo.png'  310  310
    Write-Asset 'Wide310x150Logo.png'    310  150
}
finally {
    $logo.Dispose()
}

# --- Manifest --------------------------------------------------------------------------------
$manifestSource = Join-Path $PSScriptRoot 'AppxManifest.xml'
# ReadAllText and not Get-Content -Raw: Windows PowerShell 5.1 reads a BOM-less file as ANSI, so a
# non-ASCII character in the manifest would be mangled on the way into the package. .NET assumes
# UTF-8 here, which is what the XML declaration says it is.
$manifestText = [System.IO.File]::ReadAllText($manifestSource)

# Attribute-scoped substitutions, all of them. Matching the attribute rather than the bare value
# keeps the 0.0.0.0 and the FILLMEIN strings in the file's comments from being rewritten too.
function Set-ManifestValue([string] $text, [string] $pattern, [string] $value) {
    [regex]::Replace(
        $text,
        $pattern,
        { param($m) $m.Groups[1].Value + $value + $m.Groups[2].Value },
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
}

$manifestText = Set-ManifestValue $manifestText '(<Identity\b[^>]*?\bVersion=")[^"]*(")' $packageVersion

if ($IdentityName) {
    $manifestText = Set-ManifestValue $manifestText '(<Identity\b[^>]*?\bName=")[^"]*(")' $IdentityName
}
if ($Publisher) {
    $manifestText = Set-ManifestValue $manifestText '(<Identity\b[^>]*?\bPublisher=")[^"]*(")' $Publisher
}
if ($PublisherDisplayName) {
    $manifestText = Set-ManifestValue $manifestText '(<PublisherDisplayName>)[^<]*(</PublisherDisplayName>)' $PublisherDisplayName
}

# UTF-8 without a BOM: makeappx reads the manifest as XML and a BOM ahead of the declaration is one
# of the ways it fails with an unhelpful message.
$layoutManifest = Join-Path $layout 'AppxManifest.xml'
[System.IO.File]::WriteAllText($layoutManifest, $manifestText, (New-Object System.Text.UTF8Encoding($false)))

# Everything below inspects the parsed result rather than the raw text, so the checks cannot be
# fooled by -- or tripped up by -- the prose in the file's comments.
$written = [xml][System.IO.File]::ReadAllText($layoutManifest)

if ($written.Package.Identity.Version -ne $packageVersion) {
    throw "Manifest version substitution failed: got '$($written.Package.Identity.Version)', expected '$packageVersion'."
}

$placeholders = @($written.Package.Identity.Name, $written.Package.Identity.Publisher, $written.Package.Properties.PublisherDisplayName) |
    Where-Object { $_ -match 'FILLMEIN' }
if ($placeholders) {
    $identityMessage = @"
packaging/AppxManifest.xml still has placeholder identity values:
  $($placeholders -join "`n  ")

Fill in Name, Publisher and PublisherDisplayName from Partner Center -> your app ->
Product management -> "View app identity details". The values are case-sensitive, and spaces and
punctuation must match too. Ingestion rejects the upload if they disagree, and the identity cannot
be changed after the first accepted submission. See docs/STORE-MSIX.md.
"@
    # Fatal only when a real package would come out the other end. -SkipPack exists to check that
    # the layout, the assets and the manifest still hold together, which is worth doing long before
    # anyone has a Partner Center identity to put in the file.
    if ($SkipPack) { Write-Warning $identityMessage } else { throw $identityMessage }
}

Write-Host "Identity:    $($written.Package.Identity.Name) / $($written.Package.Identity.Publisher)"

# --- Resource index ---------------------------------------------------------------------------
# Not strictly required for a package with no MRT-qualified resources, but VS emits one and
# ingestion is happier with a package that looks like the ones it usually sees. Best effort: a
# missing PRI is not worth failing a release over.
#
# Indexed from a staging directory holding only the manifest and Assets, never from the layout.
# Pointed at the layout, makepri treats the .NET satellite assembly folders (cs, de, ja, ...) as
# language qualifiers and emits a resources.language-*.pri for each, none of which the app uses:
# those satellites are loaded by the CLR from disk, not through MRT. The Store's package
# requirements warn that language codes the app does not really support "may cause delays or
# failures in certification", so the stray indexes are worth not shipping. The manifest declares
# en-us and the package now contains exactly that.
if ($makepri) {
    $priConfig = Join-Path $PSScriptRoot 'obj\priconfig.xml'
    $priStage = Join-Path $PSScriptRoot 'obj\pri'
    if (Test-Path $priStage) { Remove-Item $priStage -Recurse -Force }
    New-Item -ItemType Directory -Path $priStage -Force | Out-Null
    Copy-Item $layoutManifest $priStage -Force
    Copy-Item $assetsDir $priStage -Recurse -Force

    & $makepri createconfig /cf $priConfig /dq en-US /o | Out-Null
    if ($LASTEXITCODE -eq 0) {
        & $makepri new /pr $priStage /cf $priConfig /of (Join-Path $layout 'resources.pri') /o | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "makepri new failed ($LASTEXITCODE); packaging without resources.pri." }
    }
    else {
        Write-Warning "makepri createconfig failed ($LASTEXITCODE); packaging without resources.pri."
    }
    Remove-Item $priStage -Recurse -Force -ErrorAction SilentlyContinue
    # priconfig.xml lives in obj/, never in the layout: makeappx rejects a package containing a
    # file the manifest does not account for being there by accident.
    Remove-Item $priConfig -Force -ErrorAction SilentlyContinue
}
else {
    Write-Warning 'makepri.exe was not found; packaging without resources.pri.'
}

if ($SkipPack) {
    Write-Host 'SkipPack: layout built, not packed.'
    return
}

# --- Pack ------------------------------------------------------------------------------------
$outRoot = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null
$fileName = "RavensPort-$Version.msix"
$package = Join-Path $outRoot $fileName

Write-Host "MakeAppx:    $makeappx"
& $makeappx pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE." }
if (-not (Test-Path $package)) { throw "makeappx reported success but $package is missing." }

$size = (Get-Item $package).Length
Write-Host ("Package:     {0} ({1:N1} MB)" -f $package, ($size / 1MB))

# No size gate here, deliberately. The installer needs one because it is committed to dist/ and
# GitHub refuses a file over 100 MB in a repository; this is uploaded as a release asset and to
# Partner Center, neither of which cares.
#
# Forward slashes, so the path this prints can be pasted into a workflow step as-is.
(Join-Path $OutputDir $fileName) -replace '\\', '/'
