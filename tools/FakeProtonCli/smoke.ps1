# Smoke test for tools/FakeProtonCli.
# Run after: dotnet build tools/FakeProtonCli/FakeProtonCli.csproj

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'bin\Debug\net8.0\FakeProtonCli.exe'
if (-not (Test-Path $exe)) {
    throw "Build the fake first: dotnet build tools/FakeProtonCli/FakeProtonCli.csproj"
}

$state = Join-Path $env:TEMP ("fake-smoke-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$local = Join-Path $env:TEMP ("fake-local-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $state, $local | Out-Null
$env:PDSYNC_FAKE_STATE = $state
Remove-Item Env:PDSYNC_FAKE_SCENARIO -ErrorAction SilentlyContinue

$script:failures = 0
$script:checks = 0

function Check([bool]$Condition, [string]$Message) {
    $script:checks++
    if ($Condition) {
        Write-Host "  PASS  $Message"
    } else {
        Write-Host "  FAIL  $Message" -ForegroundColor Red
        $script:failures++
    }
}

function Run([string[]]$Arguments) {
    $err = [IO.Path]::GetTempFileName()
    try {
        $out = & $exe @Arguments 2>$err
        $code = $LASTEXITCODE
        $text = ($out | Out-String).Trim()
        $stderr = (Get-Content $err -Raw -ErrorAction SilentlyContinue)
        return [pscustomobject]@{
            Code = $code
            Out = $text
            Err = ($stderr ?? '').Trim()
        }
    } finally {
        Remove-Item $err -ErrorAction SilentlyContinue
    }
}

function Json([string]$Text) { return $Text | ConvertFrom-Json }

Write-Host "Fake CLI smoke test"
Write-Host "  state: $state"
Write-Host "  local: $local"
Write-Host ""

# --- reset and basic folders ------------------------------------------------
$r = Run @('__fake', 'reset')
Check ($r.Code -eq 0) '__fake reset'

$r = Run @('fs', 'create-folder', '/my-files', 'docs')
Check ($r.Code -eq 0) 'create-folder /my-files/docs'

$r = Run @('fs', 'create-folder', '/my-files', 'docs')
Check ($r.Code -eq 1 -and $r.Err -match 'already exists') 'duplicate create-folder fails'

$r = Run @('fs', 'create-folder', '/my-files/missing', 'child')
Check ($r.Code -eq 1) 'create-folder with missing parent fails'

# --- upload / list / info ---------------------------------------------------
$alpha = Join-Path $local 'alpha.txt'
Set-Content -LiteralPath $alpha -Value 'alpha v1' -NoNewline
$mtime = [datetime]::SpecifyKind([datetime]'2023-04-05T06:07:08.123', 'Utc')
[IO.File]::SetLastWriteTimeUtc($alpha, $mtime)

$r = Run @('fs', 'upload', '--json', '--file-conflict-strategy', 'create-new-revision', $alpha, '/my-files/docs')
Check ($r.Code -eq 0) 'upload alpha.txt'
$up = Json $r.Out
Check ($up.transferredItems -eq 1) 'upload reports 1 transferred item'
Check ($up.transferredBytes -eq 8) 'upload reports plaintext byte count'
Check ($up.skippedItems -eq 0) 'upload reports 0 skipped'

$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check ($r.Code -eq 0) 'list /my-files/docs'
$list = @(Json $r.Out)
Check ($list.Count -eq 1) 'list has one node'
$node = $list[0]
Check ($node.name.value -eq 'alpha.txt') 'list name'
Check ($node.type -eq 'file') 'list type'
Check ($node.activeRevision.claimedSize -eq 8) 'list claimedSize'
$expectedMtime = [datetime]::SpecifyKind([datetime]'2023-04-05T06:07:08.123', 'Utc')
Check (([datetime]$node.activeRevision.claimedModificationTime).ToUniversalTime() -eq $expectedMtime) 'list claimedModificationTime'
$expectedSha = (Get-FileHash -Algorithm SHA1 $alpha).Hash.ToLowerInvariant()
Check ($node.activeRevision.claimedDigests.sha1 -eq $expectedSha) 'list plaintext SHA-1'
Check ($node.activeRevision.claimedDigests.sha1Verified -eq $false) 'sha1Verified is false'

$r = Run @('fs', 'info', '--json', '/my-files/docs/alpha.txt')
Check ($r.Code -eq 0) 'info alpha.txt'
$info = Json $r.Out
Check ($info.uid -eq $node.uid) 'info uid matches list'

# --- same-content skip ------------------------------------------------------
$r = Run @('fs', 'upload', '--json', $alpha, '/my-files/docs')
$again = Json $r.Out
Check ($r.Code -eq 0 -and $again.skippedItems -eq 1 -and $again.transferredItems -eq 0) 'same content is skipped'

# --- folder mtime does not move --------------------------------------------
$r = Run @('fs', 'info', '--json', '/my-files/docs')
$before = Json $r.Out
$child = Join-Path $local 'child.txt'
Set-Content -LiteralPath $child -Value 'child' -NoNewline
$r = Run @('fs', 'upload', '--json', $child, '/my-files/docs')
Check ($r.Code -eq 0) 'upload child.txt'
$r = Run @('fs', 'info', '--json', '/my-files/docs')
$after = Json $r.Out
Check ($before.modificationTime -eq $after.modificationTime) 'folder mtime unchanged after child upload'

# --- download ---------------------------------------------------------------
$dl = Join-Path $local 'download'
New-Item -ItemType Directory -Force $dl | Out-Null
$r = Run @('fs', 'download', '--json', '/my-files/docs/alpha.txt', $dl)
Check ($r.Code -eq 0) 'download alpha.txt'
$down = Json $r.Out
Check ($down.transferredItems -eq 1) 'download reports 1 item'
$downloaded = Join-Path $dl 'alpha.txt'
Check ((Test-Path $downloaded) -and ((Get-Content -LiteralPath $downloaded -Raw) -eq 'alpha v1')) 'download content'
Check ([IO.File]::GetLastWriteTimeUtc($downloaded) -eq $mtime) 'download preserves claimed mtime'

# --- update creates a new revision -----------------------------------------
Set-Content -LiteralPath $alpha -Value 'alpha v2 longer' -NoNewline
$r = Run @('fs', 'upload', '--json', $alpha, '/my-files/docs')
$upd = Json $r.Out
Check ($r.Code -eq 0 -and $upd.transferredItems -eq 1) 'modified file uploads'
$r = Run @('fs', 'info', '--json', '/my-files/docs/alpha.txt')
$info2 = Json $r.Out
Check ($info2.activeRevision.uid -ne $node.activeRevision.uid) 'revision uid changed'
Check ($info2.activeRevision.claimedSize -eq 15) 'new claimedSize'

# --- escaped glob path ------------------------------------------------------
$bracket = Join-Path $local 'brackets[1].txt'
Set-Content -LiteralPath $bracket -Value 'bracket' -NoNewline
$escaped = $bracket.Replace('[', '[[]')
$r = Run @('fs', 'upload', '--json', $escaped, '/my-files/docs')
Check ($r.Code -eq 0) 'escaped [ path uploads'
$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check ((Json $r.Out | Where-Object { $_.name.value -eq 'brackets[1].txt' }).Count -eq 1) 'escaped name listed literally'

$r = Run @('fs', 'upload', '--json', (Join-Path $local 'nope[1].txt'), '/my-files/docs')
Check ($r.Code -eq 1 -and $r.Err -match 'No paths matched') 'missing glob path fails the batch'

# --- trash / restore --------------------------------------------------------
$r = Run @('fs', 'list', '--json', '/my-files/docs')
$beforeTrash = @(Json $r.Out)
$r = Run @('fs', 'trash', '/my-files/docs/child.txt')
Check ($r.Code -eq 0) 'trash child.txt'
$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check (@(Json $r.Out).Count -eq $beforeTrash.Count - 1) 'trashed node leaves the folder'
$r = Run @('fs', 'list', '--json', '/trash')
$trashed = @(Json $r.Out)
Check ($trashed.Count -eq 1 -and $trashed[0].name.value -eq 'child.txt') 'trash lists child.txt'
$trashUid = $trashed[0].uid
$r = Run @('fs', 'restore', "/trash/$trashUid")
Check ($r.Code -eq 0) 'restore by /trash/<uid>'
$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check (@(Json $r.Out).Count -eq $beforeTrash.Count) 'restored node returns'

# --- case sensitivity -------------------------------------------------------
$lower = Join-Path $local 'name.txt'
Set-Content -LiteralPath $lower -Value 'lower' -NoNewline
Run @('fs', 'upload', '--json', $lower, '/my-files/docs') | Out-Null
$tmp = Join-Path $local 'name.case-tmp'
Move-Item -LiteralPath $lower -Destination $tmp
Move-Item -LiteralPath $tmp -Destination (Join-Path $local 'Name.txt')
Run @('fs', 'upload', '--json', (Join-Path $local 'Name.txt'), '/my-files/docs') | Out-Null
$r = Run @('fs', 'list', '--json', '/my-files/docs')
$variants = @(Json $r.Out | Where-Object { $_.name.value -ieq 'name.txt' })
Check ($variants.Count -eq 2) 'fake backend is case-sensitive (two nodes)'

# --- scenario: transient failure once --------------------------------------
$scenario = Join-Path $local 'scenario.json'
@'
{
  "rules": [
    { "match": "^fs list", "times": 1, "exitCode": 1, "stderr": "simulated transient failure" }
  ]
}
'@ | Set-Content -LiteralPath $scenario -Encoding UTF8
$r = Run @('__fake', 'scenario', $scenario)
Check ($r.Code -eq 0) 'install scenario'
$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check ($r.Code -eq 1 -and $r.Err -match 'simulated') 'scenario fails the first list'
$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check ($r.Code -eq 0) 'scenario does not fail the second list'
Run @('__fake', 'clear-scenario') | Out-Null

# --- auth -------------------------------------------------------------------
Run @('__fake', 'set-auth', 'off') | Out-Null
$r = Run @('fs', 'list', '--json', '/my-files/docs')
Check ($r.Code -eq 3 -and $r.Err -match 'authenticated') 'unauthenticated command exits 3'
Run @('__fake', 'set-auth', 'on') | Out-Null

# --- invocation log ---------------------------------------------------------
$r = Run @('__fake', 'log')
Check ($r.Out -match '"upload"') 'invocation log records uploads'
Check ($r.Out -match '"download"') 'invocation log records downloads'

# --- seed -------------------------------------------------------------------
$manifest = Join-Path $local 'seed.json'
@'
{
  "auth": true,
  "nodes": [
    { "path": "/my-files/seeded/docs", "type": "folder" },
    { "path": "/my-files/seeded/docs/hello.txt", "type": "file", "content": "hello seeded" }
  ]
}
'@ | Set-Content -LiteralPath $manifest -Encoding UTF8
$r = Run @('__fake', 'seed', $manifest)
Check ($r.Code -eq 0) 'seed manifest'
$r = Run @('fs', 'list', '--json', '/my-files/seeded/docs')
$seeded = @(Json $r.Out)
Check ($seeded.Count -eq 1 -and $seeded[0].name.value -eq 'hello.txt') 'seeded tree lists'
$r = Run @('fs', 'download', '--json', '/my-files/seeded/docs/hello.txt', (Join-Path $local 'seeded-dl'))
Check ($r.Code -eq 0) 'seeded file downloads'
Check ((Get-Content (Join-Path $local 'seeded-dl/hello.txt') -Raw) -eq 'hello seeded') 'seeded content round-trips'

# --- summary ----------------------------------------------------------------
Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "PASS: $script:checks checks" -ForegroundColor Green
    Remove-Item -Recurse -Force $state, $local -ErrorAction SilentlyContinue
    exit 0
}

Write-Host "FAIL: $script:failures of $script:checks checks" -ForegroundColor Red
Write-Host "State kept for inspection: $state"
Write-Host "Local kept for inspection: $local"
exit 1
