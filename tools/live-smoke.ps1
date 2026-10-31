#requires -Version 7.0
<#
.SYNOPSIS
  Runs pdsync against a real Proton Drive account using a unique scratch folder.

.DESCRIPTION
  Exercises upload, remote-origin download, local deletion -> Proton Trash,
  remote deletion -> local trash, ignored files, fast/full runs, and verifies
  a pushed file by downloading it back with the official CLI.

  The remote root is created as /my-files/<Name>. If the run succeeds and
  -KeepRemote is not specified, the scratch root is trashed and then permanently
  deleted from /trash by its UID. Local artifacts stay under tmp/ for inspection.

.PARAMETER Name
  Unique scratch folder name. Defaults to pdsync-live-smoke-<timestamp>.

.PARAMETER RemoteParent
  Proton path under which to create the scratch folder. Default: /my-files.

.PARAMETER KeepRemote
  Leave the remote scratch folder in place (useful for debugging).

.EXAMPLE
  pwsh -File tools/live-smoke.ps1
#>
param(
    [string]$Name = "pdsync-live-smoke-$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [string]$RemoteParent = '/my-files',
    [switch]$KeepRemote
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $repo 'proton-drive.exe'
$pdsync = Join-Path $repo 'src/PdSync/bin/Debug/net8.0/PdSync.exe'

if (-not (Test-Path $cli)) { throw "proton-drive.exe not found at $cli" }
if (-not (Test-Path $pdsync)) { throw "Build pdsync first: dotnet build pdsync.sln" }

$remoteRoot = "$($RemoteParent.TrimEnd('/'))/$Name"
$work = Join-Path $repo "tmp/$Name"
$local = Join-Path $work 'local'
$state = Join-Path $work 'state'
$logs = Join-Path $work 'logs'
$verify = Join-Path $work 'verify'
$config = Join-Path $work 'pdsync.yaml'
New-Item -ItemType Directory -Force -Path $local, $state, $logs, $verify | Out-Null

function Write-Step([string]$Text) {
    Write-Host ""
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
    Write-Host "PASS  $Message" -ForegroundColor Green
}

function Convert-CliJson([object[]]$Lines) {
    $text = ($Lines | Out-String)
    $start = $text.IndexOf('[')
    $end = $text.LastIndexOf(']')
    if ($start -lt 0 -or $end -lt $start) { throw "No JSON array in CLI output:`n$text" }
    return $text.Substring($start, $end - $start + 1) | ConvertFrom-Json
}

function Invoke-Cli([string[]]$Arguments) {
    $text = (& $cli @Arguments 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "proton-drive $($Arguments -join ' ') failed ($LASTEXITCODE):`n$text"
    }
    return $text
}

function Invoke-PdSync([string[]]$Arguments) {
    $text = (& $pdsync @Arguments 2>&1 | Out-String)
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text.Trim() }
}

function Invoke-PdSyncJson([string[]]$Arguments) {
    $result = Invoke-PdSync $Arguments
    if ($result.Code -ne 0) {
        throw "pdsync $($Arguments -join ' ') failed ($($result.Code)):`n$($result.Text)"
    }
    return $result.Text | ConvertFrom-Json
}

Write-Step "Creating remote scratch root $remoteRoot"
$existing = Convert-CliJson (Invoke-Cli @('fs', 'list', $RemoteParent, '--json'))
$conflict = $existing | Where-Object { $_.name.ok -and $_.name.value -eq $Name }
Assert ($null -eq $conflict) "scratch root name is unique"
Invoke-Cli @('fs', 'create-folder', $RemoteParent, $Name) | Out-Null

try {
    $yaml = @"
version: 1
stateDir: '$state'
logDir: '$logs'
proton:
  cli: '$cli'
defaults:
  settleSeconds: 0
  verify:
    mode: hash
  safety:
    maxDeletes: 100
    maxDeletePercent: 100
syncs:
  smoke:
    local: '$local'
    remote: '$remoteRoot'
"@
    Set-Content -Path $config -Value $yaml -Encoding utf8

    Write-Step 'Seeding local tree'
    Set-Content -Path (Join-Path $local 'alpha.txt') -Value "alpha v1`n" -NoNewline -Encoding utf8
    New-Item -ItemType Directory -Force -Path (Join-Path $local 'nested') | Out-Null
    Set-Content -Path (Join-Path $local 'nested/beta.txt') -Value "beta`n" -NoNewline -Encoding utf8
    Set-Content -Path (Join-Path $local 'ignored.txt') -Value "must stay local`n" -NoNewline -Encoding utf8
    Set-Content -Path (Join-Path $local '.pdignore') -Value "ignored.txt`n" -Encoding utf8

    Write-Step 'doctor'
    $doctor = Invoke-PdSync @('doctor', '--config', $config, '--json')
    if ($doctor.Code -ne 0) { throw "doctor failed ($($doctor.Code)):`n$($doctor.Text)" }
    $doctorJson = $doctor.Text | ConvertFrom-Json
    Assert ($doctorJson.ok -eq $true) 'doctor reports healthy'

    Write-Step 'plan initial push'
    $plan = Invoke-PdSyncJson @('plan', '--config', $config, '--json')
    $actions = @($plan.syncs[0].actions)
    Assert ($plan.exitCode -eq 0) 'initial plan exits 0'
    Assert (@($actions | Where-Object { $_.kind -eq 'UploadCreate' -and $_.path -eq 'alpha.txt' }).Count -eq 1) 'plan includes alpha.txt upload'
    Assert (@($actions | Where-Object { $_.path -eq 'ignored.txt' }).Count -eq 0) 'ignored.txt is not planned'
    Assert (-not (Test-Path (Join-Path $state 'pdsync.db'))) 'plan did not create the state DB'

    Write-Step 'run initial push'
    $run = Invoke-PdSyncJson @('run', '--config', $config, '--json')
    Assert ($run.exitCode -eq 0) 'initial push exits 0'
    Assert ($run.syncs[0].failed -eq 0) 'initial push has zero failed actions'

    $rootNodes = Convert-CliJson (Invoke-Cli @('fs', 'list', $remoteRoot, '--json'))
    $names = @($rootNodes | ForEach-Object { $_.name.value })
    Assert ($names -contains 'alpha.txt') 'alpha.txt exists remotely'
    Assert ($names -contains 'nested') 'nested/ exists remotely'
    Assert ($names -notcontains 'ignored.txt') 'ignored.txt was not uploaded'
    $nestedNodes = Convert-CliJson (Invoke-Cli @('fs', 'list', "$remoteRoot/nested", '--json'))
    Assert (@($nestedNodes | Where-Object { $_.name.value -eq 'beta.txt' }).Count -eq 1) 'nested/beta.txt exists remotely'

    Write-Step 'second run is a no-op'
    $noop = Invoke-PdSyncJson @('plan', '--config', $config, '--json')
    Assert (@($noop.syncs[0].actions).Count -eq 0) 'converged plan has zero actions'

    Write-Step 'local edit pushes a new revision'
    Set-Content -Path (Join-Path $local 'alpha.txt') -Value "alpha v2 is longer`n" -NoNewline -Encoding utf8
    $edit = Invoke-PdSyncJson @('run', '--config', $config, '--json')
    Assert ($edit.exitCode -eq 0) 'local edit run exits 0'
    Invoke-Cli @('fs', 'download', "$remoteRoot/alpha.txt", $verify) | Out-Null
    $downloadedAlpha = Get-Content -Raw -Path (Join-Path $verify 'alpha.txt')
    Assert ($downloadedAlpha -eq "alpha v2 is longer`n") 'remote alpha.txt has the edited content'

    Write-Step 'remote-origin upload downloads locally'
    $origin = Join-Path $work 'remote-origin.txt'
    Set-Content -Path $origin -Value "from proton`n" -NoNewline -Encoding utf8
    Invoke-Cli @('fs', 'upload', $origin, $remoteRoot) | Out-Null
    $pullPlan = Invoke-PdSyncJson @('plan', '--config', $config, '--json')
    Assert (@($pullPlan.syncs[0].actions | Where-Object { $_.kind -eq 'DownloadCreate' -and $_.path -eq 'remote-origin.txt' }).Count -eq 1) 'plan detects remote-origin file'
    $pull = Invoke-PdSyncJson @('run', '--config', $config, '--json')
    Assert ($pull.exitCode -eq 0) 'pull run exits 0'
    Assert (Test-Path (Join-Path $local 'remote-origin.txt')) 'remote-origin.txt arrived locally'
    Assert ((Get-Content -Raw (Join-Path $local 'remote-origin.txt')) -eq "from proton`n") 'downloaded content matches'

    Write-Step 'remote deletion moves the local file to local trash'
    Invoke-Cli @('fs', 'trash', "$remoteRoot/remote-origin.txt") | Out-Null
    $remoteDelete = Invoke-PdSyncJson @('run', '--config', $config, '--json')
    Assert ($remoteDelete.exitCode -eq 0) 'remote-delete run exits 0'
    Assert (-not (Test-Path (Join-Path $local 'remote-origin.txt'))) 'remote-deleted file is gone locally'
    $trashed = @(Get-ChildItem -Path (Join-Path $state 'trash') -Recurse -File -Filter 'remote-origin.txt')
    Assert ($trashed.Count -ge 1) 'remote-deleted file is in local trash'

    Write-Step 'local deletion moves the remote file to Proton Trash'
    Remove-Item -Path (Join-Path $local 'nested/beta.txt')
    $localDelete = Invoke-PdSyncJson @('run', '--config', $config, '--json')
    Assert ($localDelete.exitCode -eq 0) 'local-delete run exits 0'
    $nestedAfter = Convert-CliJson (Invoke-Cli @('fs', 'list', "$remoteRoot/nested", '--json'))
    Assert (@($nestedAfter | Where-Object { $_.name.value -eq 'beta.txt' }).Count -eq 0) 'beta.txt is gone from the remote folder'

    Write-Step 'fast/full mode'
    $fast = Invoke-PdSyncJson @('run', '--config', $config, '--fast', '--json')
    Assert ($fast.exitCode -eq 0) 'fast run exits 0'
    Assert ($fast.syncs[0].fast -eq $true) 'fast run used a fast listing'
    $full = Invoke-PdSyncJson @('run', '--config', $config, '--full', '--json')
    Assert ($full.exitCode -eq 0) 'full run exits 0'
    Assert ($full.syncs[0].fast -eq $false) 'full run forced a full listing'

    Write-Host ""
    Write-Host "LIVE SMOKE PASSED" -ForegroundColor Green
    Write-Host "local artifacts: $work"
    Write-Host "remote root:     $remoteRoot"
}
finally {
    if (-not $KeepRemote) {
        Write-Step 'Cleaning up remote scratch folder'
        try {
            Invoke-Cli @('fs', 'trash', $remoteRoot) | Out-Null
            Invoke-Cli @('fs', 'delete', "/trash/$Name") | Out-Null
            Write-Host "Removed $remoteRoot from trash." -ForegroundColor Green
        }
        catch {
            Write-Warning "Remote cleanup failed: $($_.Exception.Message)"
            Write-Warning "Scratch root may remain at $remoteRoot"
        }
    }
    else {
        Write-Host "Kept remote root: $remoteRoot" -ForegroundColor Yellow
    }
}
