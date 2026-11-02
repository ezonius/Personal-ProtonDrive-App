# pdsync runbook — publish, schedule, operate

Operator guide for shipping `pdsync` and leaving it running unattended.
Design and safety rules live in [`../SPEC.md`](../SPEC.md); day-to-day agent
orientation lives in [`../AGENTS.md`](../AGENTS.md).

Everything below is verified against the code in this repository. Flags and
exit codes are the ones implemented by `src/PdSync/Cli/CliApp.cs` and described
in SPEC §3.1 / SPEC §3.2.

---

## 1. Publish the executable

`pdsync` is a .NET 8 console app. Both a self-contained single-file exe (no
.NET install required on the target machine) and a smaller framework-dependent
"portable" build are supported.

### Self-contained (recommended for a scheduled task)

```powershell
dotnet publish src/PdSync/PdSync.csproj -c Release -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

Or use the wrapper script, which prints the exe path and size and smoke-tests
the result:

```powershell
pwsh -NoProfile -File tools/publish.ps1 -Runtime win-x64
```

```
published: <repo>\dist\win-x64\PdSync.exe
size:      36462232 bytes (34.8 MB)
runtime:   bundled (self-contained); no .NET install needed on the target machine
smoke:     --help ok (exit 0)
```

`dist/win-x64/PdSync.exe` is the only file you need to copy. `PdSync.pdb` is
optional debugging metadata and can be left behind.

### Portable (framework-dependent)

```powershell
pwsh -NoProfile -File tools/publish.ps1 -SelfContained:$false
```

Produces `dist/win-x64-portable/PdSync.exe` (~2.8 MB) and requires the **.NET 8
runtime** on the target machine. There is no native single-file download pack to
install — only the runtime.

### Script behavior

`tools/publish.ps1` parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `-Runtime` | `win-x64` | .NET runtime identifier |
| `-SelfContained` | `$true` | `$false` publishes the portable variant |
| `-OutDir` | `dist` | Output root; the exe lands in `<OutDir>/<Runtime>[-portable]/` |
| `-SkipSmoke` | off | Skip the post-publish `--help` check |

- Compression is only added for the self-contained build; .NET rejects it for
  framework-dependent output (`NETSDK1176`), so the script omits it there.
- The output directory is replaced on every run, which makes repeated publishes
  idempotent. The script refuses to delete a directory that contains anything
  other than known publish artifacts (`PdSync.exe`, `PdSync.pdb`, …), so a
  mistyped `-OutDir` cannot wipe real content.
- `dist/` is gitignored.

### Verify the published exe

```powershell
dist/win-x64/PdSync.exe --help
dist/win-x64/PdSync.exe doctor --help
```

Both exit `0` and print the global usage banner (subcommands accept `--help`
but share the same top-level usage text):

```
pdsync <command> [options]

Commands:
  init                create a config and a default .pdignore
  plan [sync...]      show actions; never writes
  run  [sync...]      apply actions and commit state
  doctor              check CLI, auth, roots, state
  ...
```

Running `pdsync` with no command also prints usage, but exits `1`.

---

## 2. Prerequisites for an unattended run

1. **`proton-drive.exe`** — the official Proton Drive CLI. pdsync has no network
   client of its own; every remote operation spawns this binary. Point
   `proton.cli` in the config at it (absolute path recommended).
2. **An authenticated session for the account the task runs as.** Authenticate
   interactively *as that account*:

   ```powershell
   proton-drive auth login
   proton-drive auth status
   ```

   Session/cache state lives in `%LOCALAPPDATA%\proton-drive-cli` for the
   logged-in user. A task running as a *different* account will not see it and
   will fail with exit `3`.
3. **A validated config** (`pdsync.yaml`) and a `.pdignore` at each sync root.
4. **`stateDir` and `logDir` on a local disk**, outside every sync root. The
   validator rejects a `stateDir`/`logDir` inside a local root (exit `4`).
5. **The published exe** on local disk (not a network share).

`proton-drive` needs network access at run time; nothing else does. The task has
no start-order dependency other than "the network and the Proton CLI session are
available", and it does not need the Proton Drive desktop app.

---

## 3. First run, in order

```powershell
# 1. create a config + default ignore file (interactive; --yes takes defaults)
dist/win-x64/PdSync.exe init --config C:\pdsync\pdsync.yaml

# 2. preflight: CLI present, version supported, auth valid, roots readable,
#    state health, staged leftovers, desktop-app mapping overlaps
dist/win-x64/PdSync.exe doctor --config C:\pdsync\pdsync.yaml

# 3. dry run: read-only preview of exactly what `run` would attempt
dist/win-x64/PdSync.exe plan --config C:\pdsync\pdsync.yaml

# 4. apply
dist/win-x64/PdSync.exe run --config C:\pdsync\pdsync.yaml

# 5. confirm what actually happened
dist/win-x64/PdSync.exe history --config C:\pdsync\pdsync.yaml --limit 5
dist/win-x64/PdSync.exe status  --config C:\pdsync\pdsync.yaml
```

Notes:

- `plan` never prompts. `run` prompts only for `conflict: ask` on a TTY without
  `--json`/`--yes` (SPEC §5.2); a scheduled task has no TTY, so scheduled runs
  keep the keep-both fallback (exit `2`). `--yes` therefore also matters for
  `run`: it suppresses that prompt.
- `plan` is strictly read-only: no state DB creation/migration, no lock, no fast
  counter, no log file, no Proton write.
- The first `run` creates the state DB under `stateDir`. Deleting that DB is
  safe but turns the next run into an initial adoption — see SPEC §5.3 and
  `rebuild` before doing it.

### Why run `doctor` first

`doctor` is the only command that checks the whole chain before anything is
modified:

- the configured `proton.cli` exists and starts;
- the detected `--version` is at least `proton.minVersion` (older → `FAIL`, exit
  `1`; a malformed version fails closed the same way);
- the session is authenticated (auth failure → exit `3`);
- every configured local root exists and every remote root is listable;
- the state DB is readable and its schema is not from the future (older schema
  is a `warn`; newer schema is a `FAIL`);
- staged-upload leftovers are reported (read-only);
- local roots are checked against the Proton Drive desktop app's
  `Mappings.json` for exact/ancestor/descendant overlap — the desktop app
  writing into a synced tree can fight pdsync.

`doctor` never creates, migrates, or backs up the state DB, and takes no run
lock. Output lines are prefixed `ok  `, `warn`, or `FAIL`, and the last line is
`doctor: ok` or `doctor: problems found`. Warnings alone keep exit `0`.
`doctor --json` emits `{command, ok, version, desktopMappings, checks}`.

---

## 4. Windows Task Scheduler setup

### 4.1 Account choice

- **Current user (default, simplest).** The Proton CLI session already exists
  for this account, so auth keeps working.
- **Dedicated user (stronger isolation).** Create a standard, non-admin local
  user, log in interactively once, run `proton-drive auth login` and
  `proton-drive auth status`, and run `doctor` as that user before scheduling.
  The user needs read access to every local sync root, write access to those
  roots, `stateDir`, and `logDir`.

Do **not** run as `SYSTEM` or as a service account: the Proton CLI session lives
in a *user* profile (`%LOCALAPPDATA%\proton-drive-cli`) and `~`/`%LOCALAPPDATA%`
expansion in the config resolves against the task's profile.

### 4.2 Task settings

| Setting | Value | Why |
|---|---|---|
| Run only when user is logged on | **Off** — use "Run whether user is logged on or not" | The point is unattended operation |
| Run with highest privileges | **Off** | Least privilege; pdsync needs no elevation |
| Start in (working directory) | the config directory, e.g. `C:\pdsync` | Config discovery (`./pdsync.yaml`) and relative config paths |
| Trigger | daily at a quiet hour (e.g. 03:00) | One run per schedule is enough; runs converge |
| Multiple instances | Do not start a new instance (or "Queue") | pdsync has its own lock; overlapping runs exit `5` |
| Stop task if it runs longer than | off, or a generous bound (e.g. 6 h) | A large first sync can legitimately run long |
| Wake the computer to run | off unless the machine sleeps nightly | Otherwise the schedule silently skips |

Interactive logon of the task account during a run is not required.

### 4.3 `schtasks` example

```powershell
schtasks /create /tn "pdsync nightly" `
  /tr '"C:\pdsync\PdSync.exe" run --config "C:\pdsync\pdsync.yaml"' `
  /sc daily /st 03:00 `
  /ru "$env:USERDOMAIN\$env:USERNAME" /rl LIMITED /f
```

The `/tr` value is a **single-quoted** PowerShell string containing the inner
double quotes, so `schtasks` receives exactly one argument:

```text
/tr = "C:\pdsync\PdSync.exe" run --config "C:\pdsync\pdsync.yaml"     (one argv entry)
```

Notes:

- `schtasks`-style `\"` escaping and `%USERDOMAIN%` expansion are `cmd.exe`
  syntax, **not** PowerShell. Pasted into `pwsh` they pass a literal `\` token
  (splitting `/tr`) and a literal `%USERDOMAIN%`; keep the single-quoted form
  above. If you deliberately run the command from `cmd.exe`, use `\"` for the
  inner quotes and `%USERDOMAIN%\%USERNAME%` for the account instead.
- Without `/rp`, `schtasks` prompts for the account password and creates a
  "run whether user is logged on or not" task.
- `schtasks` cannot set *Start in*. Either set it once in Task Scheduler
  (`Task Properties → Actions → Edit → Start in`) or rely on absolute paths in
  the config: relative paths inside the YAML are resolved against the **config
  file's directory**, not the working directory.
- Export/import via XML if you need the working directory, multiple triggers, or
  restart-on-failure. Example of the action element:

  ```xml
  <Actions Context="Author">
    <Exec>
      <Command>C:\pdsync\PdSync.exe</Command>
      <Arguments>run --config "C:\pdsync\pdsync.yaml"</Arguments>
      <WorkingDirectory>C:\pdsync</WorkingDirectory>
    </Exec>
  </Actions>
  ```

### 4.4 Verify the scheduled context

Before trusting the schedule, run the exact command once from a *non-interactive*
context:

```powershell
schtasks /run /tn "pdsync nightly"
schtasks /query /tn "pdsync nightly" /v /fo LIST | Select-String "Last Result"
```

Expected `Last Result`: `0` (or `2` if conflicts are unresolved, see §5). To debug a
different exit code, temporarily point the action at a wrapper:

```bat
C:\pdsync\PdSync.exe run --config C:\pdsync\pdsync.yaml > C:\pdsync\last-run.out 2>&1
```

pdsync writes nothing to stdout in `--json` mode except the JSON document, so
`--json` is safe to redirect too. Prefer the JSONL run log (§7) over stdout for
diagnosis.

---

## 5. Exit codes

These are the codes SPEC §3.1 defines and the CLI implements. For a scheduled
task, `0` is success; every other value is a signal you should look at.

| Code | Meaning | What a scheduled task should conclude | Action |
|---|---|---|---|
| 0 | Success, including "nothing to do" | Healthy | None |
| 1 | Runtime/CLI failure | Something failed for at least one sync | Read the JSONL log and stderr; fix and rerun |
| 2 | Unresolved conflicts (a blocking conflict aborts before any action; the `ask` fallback and deferred paths commit the safe subset) | Data is safe but the tree is not fully converged | Run `status`/`plan`, resolve or choose a policy, rerun |
| 3 | Authentication required or expired | Nothing ran; session gone | Log in as the task account, then rerun |
| 4 | Config/schema error | The task will keep failing until the config is fixed | Fix `pdsync.yaml` / `.pdignore` / paths |
| 5 | Another run holds the lock | A previous (or overlapping) run is still active | Wait; check `<stateDir>/locks/<syncId>.lock` |
| 6 | Safety guard tripped (too many deletions) | Nothing destructive was done | Investigate the deletion spike, then act deliberately |

Details that matter to an operator:

- **Exit 2 is not a crash, but its two paths differ.** A blocking conflict
  (kind change, case collision, or an abort at the `ask` prompt) aborts the sync
  before any action and records no run row. The non-interactive `ask` keep-both
  fallback and paths deferred with `s` at the prompt do apply every
  non-conflicting action, commit that subset, and leave the conflicting paths
  listed. `plan` exits `2` too when blocking conflicts exist. Conflicts are
  resolved by answering the prompt for `conflict: ask`, by editing/keeping a
  file and rerunning, by setting `defaults.conflict` / per-sync `conflict`, or
  as a last resort by `rebuild --policy=...`.
- **Exit 3** is raised before any state change when the session is missing or
  expired at the start, and can also abort mid-run. A mid-run auth abort does
  not retry, does not commit the failed action, and stops later actions;
  already-committed actions stay committed. The next run reconciles.
- **Exit 5** means a **live** process holds `<stateDir>/locks/<syncId>.lock`.
  The message that accompanies it prints the owner recorded in the file
  (`pid=`, `host=`, `sync=`, `started=`). Wait for that process, or verify the
  PID on that host; the lock clears when the holder exits. A *leftover* lock
  file from a finished or crashed run does **not** block `run` — the lock is
  opened with `FileMode.OpenOrCreate` and is simply re-acquired — so deleting it
  fixes nothing there. Only `trash purge` treats any existing lock file as an
  active run and skips that sync (`File.Exists`); once no pdsync process is
  running, delete the file to unblock purge. Never delete a lock that another
  process still holds (Windows refuses it anyway).
- **Exit 6** means `maxDeletes` or `maxDeletePercent` was exceeded, and it is
  checked **before** any deletion. `run --force` bypasses the budget — do not put
  `--force` in a scheduled command.
- A single `run` may cover several syncs. The process exit code is the highest
  code across them, so one sync with conflicts (`2`) and another with a runtime
  failure (`1`) exits `2`. Auth (`3`), safety (`6`), lock (`5`), and config (`4`)
  failures propagate from the top level and override the per-sync result.

---

## 6. Where everything lives

| Path | Contents |
|---|---|
| `<stateDir>/state/<syncId>.sqlite` | Sync baseline, pending journal, run history |
| `<stateDir>/state/backup-<yyyyMMdd-HHmmss>-<syncId>.sqlite` | Copy taken before a schema migration |
| `<stateDir>/state/backups/<syncId>.pre-rebuild-<UTC>.sqlite` | Copy taken by `rebuild` |
| `<stateDir>/locks/<syncId>.lock` | Per-sync run lock (removed on clean exit) |
| `<stateDir>/trash/<syncId>/<runId>/` | Local trash for remote-origin deletions, plus `manifest.json` |
| `<stateDir>/state/<syncId>.fast.json` | Fast-mode run counter (read-only under `plan`) |
| `<logDir>/<sync-name>/<runId:D8>-<yyyyMMddTHHmmssfffZ>.jsonl` | JSONL run log: `run_start`, `action`, `verification`, `run_finish` |

Defaults: `stateDir` = `%LOCALAPPDATA%\pdsync`, `logDir` = `<stateDir>\logs`.
`logDir` may be set independently, but must not be inside a sync root.

`runs.stats_json` in the state DB stores the same counts as `run_finish` plus
the `logFile` path, and records `error` for a failed run. Remote deletions go to
**Proton Trash**, never a hard delete; Proton's own retention policy applies
there.

---

## 7. Retention and cleanup

Retention is manual in v1. Nothing is pruned automatically by `plan`, `run`, or
`doctor`.

**Local trash** — inspect, then purge whole run directories:

```powershell
# inventory (read-only; never opens the state DB)
dist/win-x64/PdSync.exe trash list --config C:\pdsync\pdsync.yaml
dist/win-x64/PdSync.exe trash list --config C:\pdsync\pdsync.yaml --json

# preview
dist/win-x64/PdSync.exe trash purge --older-than 30 --dry-run --config C:\pdsync\pdsync.yaml

# delete run directories older than 30 days
dist/win-x64/PdSync.exe trash purge --older-than 30 --yes --config C:\pdsync\pdsync.yaml
```

`trash purge` deletes **whole run directories only**, never individual files,
never anything outside `<stateDir>/trash`, and skips any sync with an active run
lock (`active-run-lock`). It aborts with exit `6` if a run tree contains a
reparse point (junction/symlink) or an unreadable entry. Without `--yes` it asks
for an interactive confirmation; end-of-input cancels with exit `0`.

**Run logs** — no rotation, no size cap, no automatic pruning. Rotate or delete
`<logDir>/<sync-name>/*.jsonl` yourself (e.g. a monthly cleanup task). Keep at
least the logs for any run you are still investigating.

**State backups** — `<stateDir>/state/backup-*.sqlite` and
`<stateDir>/state/backups/*.sqlite` accumulate one file per migration or
`rebuild`; delete old ones once a run has succeeded on the new state.

**Proton Trash** — remote deletions are recoverable only until Proton's
retention removes them. Empty it deliberately from the Proton client when you no
longer need the remote copies.

---

## 8. Operator visibility: `status` and `history`

Both are read-only. Neither creates or migrates the state DB, takes a run lock,
writes logs, or calls the Proton CLI.

### `status` — "what is the current state of each sync?"

```powershell
dist/win-x64/PdSync.exe status --config C:\pdsync\pdsync.yaml
```

```
documents: C:\Users\me\AppData\Local\pdsync\state\3f2a....sqlite
  run 42: ok       2026-11-02 03:00:12Z -> 2026-11-02 03:00:19Z
  run 41: conflict 2026-11-01 03:00:11Z -> 2026-11-01 03:00:24Z
  pending: 1
```

- Shows the **5 most recent runs** per sync (status + start/finish time) and the
  pending-journal count. A non-zero `pending` count after a crash means the next
  run will reconcile before planning — it is not necessarily data loss.
- `no state DB` means no run has completed yet (or the DB was deleted).
- `--json` emits `{command, syncs: [{sync, state, pending, runs}]}`.
- `--limit` is accepted but has no effect on `status`; the window is fixed at 5.

### `history` — "which run failed, and what did it do?"

```powershell
dist/win-x64/PdSync.exe history --config C:\pdsync\pdsync.yaml --limit 20
```

```
documents:
  run 42  ok        2026-11-02 03:00:12Z -> 2026-11-02 03:00:19Z  planned 4 (up 2, down 1, del 1, conflict 0, adopt 0)  ok 4  fail 0  skip 0
  run 41  conflict  2026-11-01 03:00:11Z -> 2026-11-01 03:00:24Z  planned 7 (up 3, down 2, del 2, conflict 1, adopt 0)  ok 5  fail 0  skip 2
```

- Newest first, default 20 rows, `--limit` 1..500, positional sync names or
  `--sync` to filter.
- `planned` is what the planner intended (`up`/`down`/`del`/`conflict`/`adopt`);
  `ok`/`fail`/`skip` are what the executor actually did. `planned 7` with
  `ok 5` + `skip 2` is a converged-with-skips run; `planned 7` + `fail 2` means
  transfers failed.
- `status` values are `ok`, `conflict`, `partial`, `failed`.
- A row with no summary (`stats_json` missing or malformed) still shows its id,
  status, and timestamps — the history row is never dropped.
- `history` prints `no runs recorded` and exits `0` when there is no DB yet.
- `--json` emits `{command, limit, syncs: [{sync, state, pending, runs:
  [{id, status, startedAt, finishedAt, planned, succeeded, failed, skipped}]}]}`.

`history` shows counts, not error text. To see *why* a run failed, read its
JSONL log — `<logDir>/<sync-name>/<runId:D8>-<timestamp>.jsonl`, path also recorded
as `logFile` in `stats_json`:

- `run_finish` → `status`, `error`, `succeeded`/`failed`/`skipped`;
- `action` → per-path `outcome` and `error`;
- `verification` → which upload/download failed verification and by what method.

```powershell
Get-ChildItem C:\Users\me\AppData\Local\pdsync\logs\documents\*.jsonl |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 |
  Get-Content | Select-String '"event":"run_finish"'
```

### Spotting a stale sync

There is no built-in "stale" flag. Compare the newest run timestamp in `status`
or `history` against the schedule: if the newest run is older than the trigger
(or the newest row is `failed`/`conflict`), the sync is stale. A sync that has
not produced a new run for several cycles usually means the task stopped
working — check the Task Scheduler "Last Result" column (§4.4) and the exit code
table (§5).

---

## 9. 7-day unattended soak checklist

> **Status: not yet executed.** Deliverable 1–4 of #14 ship the publish path and
> this procedure; the soak itself is a separate follow-up and the checklist below
> must be completed before calling the Task Scheduler recipe "proven".

The live smoke (`tools/live-smoke.ps1`) is the fast companion check for this
procedure. It also exercises a `keep-both` conflict, including a near-limit
240-character stem whose conflict name is truncated to 255 characters. On the
live account this step added ~3 min to an ~8 min run: it seeds and transfers
two conflicts, uploads two remote edits, downloads two originals, uploads two
conflict copies, and downloads both copies back to verify their contents. The
scratch remote root cleanup still covers the conflict files.

Run this on a real machine with a real config, one run per night for 7
consecutive days, with no manual intervention in between.

Daily (every morning):

- [ ] **Day N: `doctor` exits 0.** Run
      `PdSync.exe doctor --config <cfg> --json` and confirm exit `0`; note any
      `warn` lines and whether they changed since the previous day.
- [ ] **Day N: no unexpected non-zero exit.** `history --limit 5` — every run for
      the last day is `ok`. A `conflict` or `failed` row is a finding; record the
      run id and the `run_finish` line from its JSONL log.
- [ ] **Day N: trash growth is sane.** `trash list --json` — compare `fileCount`
      and `bytes` against yesterday. Growth should reflect real remote deletions
      only; a jump with no matching remote change is a bug.
- [ ] **Day N: canary file synced.** Write a uniquely named file into a synced
      tree (e.g. `soak-canary-dayN.txt`), let the next scheduled run pick it up,
      and confirm it appears in remote. On day N+1 delete it locally and confirm
      it lands in Proton Trash; or edit it remotely and confirm it is pulled
      down. Record the run id that carried the action.
- [ ] **Day N: no stranded state.** `status` prints **no** `pending:` line (it
      only prints one when the count is non-zero; `status --json` reports
      `"pending": 0` instead). A printed `pending: N` after a completed run
      means crash recovery did not settle.
- [ ] **Day N: lock is not stale.** `<stateDir>/locks/` contains no `.lock` file
      between runs.

At the end of day 7:

- [ ] Seven consecutive days with a run each day, no manual triggers.
- [ ] Zero exit `3` (auth stayed valid for the whole week) and zero exit `5`.
- [ ] Every non-zero exit is explained in this checklist and has a JSONL log
      reference.
- [ ] `plan` immediately after the final run reports zero actions (converged).
- [ ] Trash, logs, and state backups are within the retention plan in §7.
- [ ] The scheduled command in Task Scheduler is still the exact command you
      validated, and "Last Result" is `0`.

Record the results (dates, run ids, exit codes, anomalies) on the issue before
closing the soak item.
