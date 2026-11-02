<#
.SYNOPSIS
    Produce a reproducible pdsync executable.

.DESCRIPTION
    Publishes src/PdSync/PdSync.csproj and verifies the result answers `--help`.

    Two variants are supported:

      * self-contained (default, .NET runtime bundled) - the exe runs on a
        machine with no .NET 8 install. This is the variant to schedule.
      * portable (-SelfContained:$false, framework-dependent) - smaller output,
        but the target machine must have the .NET 8 runtime installed.

    Each variant publishes into its own subdirectory of -OutDir, so they can
    coexist: <OutDir>/<Runtime>/ and <OutDir>/<Runtime>-portable/.

    The script is idempotent: an existing output directory is replaced, but
    only when it contains nothing besides known publish artifacts.

.PARAMETER Runtime
    .NET runtime identifier to publish for. Default: win-x64.

.PARAMETER SelfContained
    $true (default) bundles the runtime; $false produces the portable build.
    Pass as -SelfContained:$false.

.PARAMETER OutDir
    Output root, relative to the repository root or absolute. Default: dist/.

.PARAMETER SkipSmoke
    Skip running `<exe> --help` after publishing.

.EXAMPLE
    pwsh -NoProfile -File tools/publish.ps1 -Runtime win-x64

.EXAMPLE
    pwsh -NoProfile -File tools/publish.ps1 -SelfContained:$false
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [bool]$SelfContained = $true,
    [string]$OutDir = 'dist',
    [switch]$SkipSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src/PdSync/PdSync.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "project not found: $project"
}

$outRoot = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$variant = if ($SelfContained) { $Runtime } else { "$Runtime-portable" }
$target = Join-Path $outRoot $variant

# --- idempotent clean -------------------------------------------------------
# Only remove a pre-existing output directory when it looks like ours. Anything
# else means the caller pointed -OutDir at real content, and we refuse.
$knownArtifacts = @('PdSync.exe', 'PdSync.pdb', 'PdSync', 'PdSync.dbg')
if (Test-Path -LiteralPath $target) {
    $unknown = @(Get-ChildItem -LiteralPath $target -Force |
        Where-Object { $knownArtifacts -notcontains $_.Name })
    if ($unknown.Count -gt 0) {
        throw "refusing to replace '$target': it contains unexpected entries " +
              "($(($unknown | ForEach-Object Name) -join ', ')). Remove it manually or choose another -OutDir."
    }
    Remove-Item -LiteralPath $target -Recurse -Force
}

New-Item -ItemType Directory -Path $target -Force | Out-Null

# --- publish ---------------------------------------------------------------
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', $Runtime,
    "--self-contained=$($SelfContained.ToString().ToLowerInvariant())",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:GenerateDocumentationFile=false',
    '-o', $target,
    '--nologo'
)

if ($SelfContained) {
    # NETSDK1176: single-file bundle compression is only supported when the
    # application is self-contained, so the portable variant must omit it.
    $publishArgs += '-p:EnableCompressionInSingleFile=true'
}

Write-Host "pdsync publish: $Runtime (self-contained=$SelfContained)" -ForegroundColor Cyan
Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exeCandidates = @('PdSync.exe', 'PdSync') | ForEach-Object { Join-Path $target $_ }
$exe = $exeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) {
    throw "publish succeeded but no executable was produced in $target"
}

$exeItem = Get-Item -LiteralPath $exe
$sizeMb = [math]::Round($exeItem.Length / 1MB, 1)
Write-Host ""
Write-Host "published: $($exeItem.FullName)" -ForegroundColor Green
Write-Host "size:      $($exeItem.Length) bytes ($sizeMb MB)"

if ($SelfContained) {
    Write-Host "runtime:   bundled (self-contained); no .NET install needed on the target machine"
} else {
    Write-Host "runtime:   framework-dependent (portable); the target machine needs the .NET 8 runtime"
}

# --- smoke check -----------------------------------------------------------
if (-not $SkipSmoke) {
    $output = & $exe --help 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        throw "'$exe --help' exited $code"
    }
    if ($output -notmatch 'pdsync <command> \[options\]') {
        throw "'$exe --help' did not print the expected usage banner"
    }
    Write-Host "smoke:     --help ok (exit 0)" -ForegroundColor Green
}

if (-not $SelfContained) {
    Write-Host "note:      portable output needs the .NET 8 runtime; the scheduled task should use the self-contained build." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "exe: $($exeItem.FullName)"
