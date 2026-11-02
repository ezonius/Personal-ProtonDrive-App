#requires -Version 7.0
<#
.SYNOPSIS
  Runs pdsync against a real Proton Drive account using a unique scratch folder.

.DESCRIPTION
  Exercises upload, remote-origin download, local deletion -> Proton Trash,
  remote deletion -> local trash, ignored files, fast/full runs, keep-both
  conflicts (including a near-limit 240-character stem), and verifies a
  pushed file by downloading it back with the official CLI.

  The keep-both step seeds and conflicts two files (one with a near-limit
  stem), makes two remote edits with proton-drive.exe, and downloads both
  produced conflict copies back: measured ~3 min on the live account
  (11 min total), because this account's transfers are slow. It also creates
  a local file whose full Windows path exceeds MAX_PATH, so the machine needs
  long-path support.

  The remote root is created as /my-files/<Name>. If the run succeeds and
  -KeepRemote is not specified, the scratch root is trashed and then permanently
  deleted from /trash by its UID. Local artifacts stay under tmp/ for inspection.

.PARAMETER Name
  Unique scratch folder name. Defaults to pdsync-live-smoke-<timestamp>.

.PARAMETER RemoteParent
  Proton path under which to create the scratch folder. Default: /my-files.

.PARAMETER KeepRemote
  Leave the remote scratch folder in place (useful for debugging).

.PARAMETER SkipConflict
  Skip the keep-both conflict step (including the near-limit stem case).

.EXAMPLE
  pwsh -File tools/live-smoke.ps1
#>
param(
    [string]$Name = "pdsync-live-smoke-$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [string]$RemoteParent = '/my-files',
    [switch]$KeepRemote,
    [switch]$SkipConflict
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
  conflict: keep-both
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

    if (-not $SkipConflict) {
        Write-Step 'keep-both conflict keeps both copies (including a near-limit stem)'
        $conflictName = 'conflict.txt'
        $longStem = 'n' * 240
        $longName = "$longStem.txt"
        $localConflict = Join-Path $local $conflictName
        $localLong = Join-Path $local $longName

        Set-Content -Path $localConflict -Value "baseline conflict`n" -NoNewline -Encoding utf8
        Set-Content -Path $localLong -Value "baseline long stem`n" -NoNewline -Encoding utf8
        $conflictSeed = Invoke-PdSyncJson @('run', '--config', $config, '--json')
        Assert ($conflictSeed.exitCode -eq 0) 'conflict seed run exits 0'
        Assert ($conflictSeed.syncs[0].failed -eq 0) 'conflict seed run has zero failed actions'

        # Change both sides: local files first, then the same remote names through the CLI.
        Set-Content -Path $localConflict -Value "local conflict edit`n" -NoNewline -Encoding utf8
        Set-Content -Path $localLong -Value "local long-stem edit`n" -NoNewline -Encoding utf8
        $remoteStaging = Join-Path $work 'remote-edit'
        New-Item -ItemType Directory -Force -Path $remoteStaging | Out-Null
        $remoteConflict = Join-Path $remoteStaging $conflictName
        $remoteLong = Join-Path $remoteStaging $longName
        Set-Content -Path $remoteConflict -Value "remote conflict edit`n" -NoNewline -Encoding utf8
        Set-Content -Path $remoteLong -Value "remote long-stem edit`n" -NoNewline -Encoding utf8
        Invoke-Cli @('fs', 'upload', '--json', '--file-conflict-strategy', 'create-new-revision', $remoteConflict, $remoteRoot) | Out-Null
        Invoke-Cli @('fs', 'upload', '--json', '--file-conflict-strategy', 'create-new-revision', $remoteLong, $remoteRoot) | Out-Null

        $conflictPlan = Invoke-PdSyncJson @('plan', '--config', $config, '--json')
        $conflictActions = @($conflictPlan.syncs[0].actions)
        $shortRename = @($conflictActions | Where-Object { $_.kind -eq 'LocalConflictRename' -and $_.path -eq $conflictName })
        Assert ($shortRename.Count -eq 1) 'plan renames the conflicting local copy'
        Assert ($shortRename[0].secondaryPath -match '^conflict \(conflict \d{4}-\d{2}-\d{2} \d{6}\)\.txt$') 'conflict name matches the documented format'
        Assert (@($conflictActions | Where-Object { $_.kind -eq 'KeepBothUpload' -and $_.path -eq $shortRename[0].secondaryPath }).Count -eq 1) 'plan uploads the conflict copy'

        $longRename = @($conflictActions | Where-Object { $_.kind -eq 'LocalConflictRename' -and $_.path -eq $longName })
        Assert ($longRename.Count -eq 1) 'plan renames the long-stem conflicting copy'
        $plannedLongName = $longRename[0].secondaryPath
        Assert ($plannedLongName.Length -le 255) 'planned long-stem conflict name fits 255 characters'
        Assert ($plannedLongName.EndsWith('.txt')) 'planned long-stem conflict name keeps its extension'
        Assert ($plannedLongName.Contains(' (conflict ')) 'planned long-stem conflict name contains the conflict marker'

        $conflictRun = Invoke-PdSyncJson @('run', '--config', $config, '--json')
        Assert ($conflictRun.exitCode -eq 0) 'keep-both conflict run exits 0'
        Assert ($conflictRun.syncs[0].failed -eq 0) 'keep-both conflict run has zero failed actions'

        $localConflictCopy = @(Get-ChildItem -Path $local -File |
            Where-Object { $_.Name -match '^conflict \(conflict \d{4}-\d{2}-\d{2} \d{6}\)\.txt$' })
        Assert ($localConflictCopy.Count -eq 1) 'local directory gained the conflict-named copy'
        Assert ((Get-Content -Raw $localConflict) -eq "remote conflict edit`n") 'conflict.txt holds the remote content locally'
        Assert ((Get-Content -Raw $localConflictCopy[0].FullName) -eq "local conflict edit`n") 'conflict copy holds the local content'

        $localLongCopy = @(Get-ChildItem -Path $local -File |
            Where-Object { $_.Name -match '^n+ \(conflict \d{4}-\d{2}-\d{2} \d{6}\)\.txt$' })
        Assert ($localLongCopy.Count -eq 1) 'local directory gained the long-stem conflict copy'
        $longConflictName = $localLongCopy[0].Name
        Assert ($longConflictName.Length -le 255) 'long-stem conflict name is at most 255 characters'
        Assert ($longConflictName.EndsWith('.txt')) 'long-stem conflict name keeps its extension'
        Assert ($longConflictName.Contains(' (conflict ')) 'long-stem conflict name contains the conflict marker'
        Assert ((Get-Content -Raw $localLong) -eq "remote long-stem edit`n") 'long-stem original holds the remote content locally'
        Assert ((Get-Content -Raw $localLongCopy[0].FullName) -eq "local long-stem edit`n") 'long-stem conflict copy holds the local content'

        $conflictNodes = Convert-CliJson (Invoke-Cli @('fs', 'list', $remoteRoot, '--json'))
        $conflictNodeNames = @($conflictNodes | ForEach-Object { $_.name.value })
        Assert ($conflictNodeNames -contains $conflictName) 'remote still has conflict.txt'
        Assert ($conflictNodeNames -contains $longName) 'remote still has the long-stem original'
        Assert ($conflictNodeNames -contains $localConflictCopy[0].Name) 'remote has the conflict-named copy'
        Assert ($conflictNodeNames -contains $longConflictName) 'remote has the long-stem conflict copy'

        # Remote originals keep the CLI-uploaded content; compare the CLI's claimed SHA-1.
        $remoteConflictNode = @($conflictNodes | Where-Object { $_.name.value -eq $conflictName })[0]
        $remoteLongNode = @($conflictNodes | Where-Object { $_.name.value -eq $longName })[0]
        Assert ($remoteConflictNode.activeRevision.claimedDigests.sha1 -eq (Get-FileHash -Algorithm SHA1 $remoteConflict).Hash.ToLowerInvariant()) 'remote conflict.txt has the remote content'
        Assert ($remoteLongNode.activeRevision.claimedDigests.sha1 -eq (Get-FileHash -Algorithm SHA1 $remoteLong).Hash.ToLowerInvariant()) 'remote long-stem original has the remote content'

        # Download the conflict copies back to prove the remote copies hold the local edits.
        $verifyConflicts = Join-Path $verify 'conflicts'
        New-Item -ItemType Directory -Force -Path $verifyConflicts | Out-Null
        Invoke-Cli @('fs', 'download', "$remoteRoot/$($localConflictCopy[0].Name)", $verifyConflicts) | Out-Null
        Invoke-Cli @('fs', 'download', "$remoteRoot/$longConflictName", $verifyConflicts) | Out-Null
        Assert ((Get-Content -Raw (Join-Path $verifyConflicts $localConflictCopy[0].Name)) -eq "local conflict edit`n") 'remote conflict copy holds the local content'
        Assert ((Get-Content -Raw (Join-Path $verifyConflicts $longConflictName)) -eq "local long-stem edit`n") 'remote long-stem conflict copy holds the local content'
    }
    else {
        Write-Host 'Skipped keep-both conflict step (-SkipConflict).' -ForegroundColor Yellow
    }

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
