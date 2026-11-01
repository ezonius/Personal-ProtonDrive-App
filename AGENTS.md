# AGENTS.md

Orientation for coding agents working on **pdsync**, a personal two-way sync tool
for Proton Drive built on top of the official `proton-drive` CLI.

**Read `SPEC.md` before changing behavior.** It is the source of truth for the
design, decision table, state schema, safety rules, and milestones. This file is
the fast orientation layer; when the two disagree, inspect the code and update
`SPEC.md` deliberately rather than silently diverging.

---

## 1. Project at a glance

- **Goal:** declarative local↔remote sync pairs, `.pdignore` semantics, safe
  two-way sync, `plan`/`run`/`doctor`/`status` commands.
- **Stack:** C# 12, .NET 8 (LTS), WPF-free console app, single-file publish.
- **Platform:** Windows-first; path handling is intended to work on Unix too.
- **Remote API:** only the official `proton-drive.exe` CLI. There is **no**
  `pdsync` network client and no Proton SDK dependency.
- **State:** SQLite under `<stateDir>/state/<syncId>.sqlite`.
- **Current status:** M1/M2/M3/M4 core engine is implemented and green against
  the fake CLI; a live Proton smoke test passed on 2026-10-31 (see §7).

### Commands

```powershell
# Build (must be clean: 0 warnings, 0 errors)
dotnet build pdsync.sln -v quiet --nologo

# Unit + fake-backed end-to-end tests
dotnet test tests/PdSync.Tests/PdSync.Tests.csproj --no-build --nologo `
  --logger "console;verbosity=minimal"

# Debug executable
src/PdSync/bin/Debug/net8.0/PdSync.exe plan --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe run  --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe doctor --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe status --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe ignore path/to/file --config .\pdsync.yaml

# Live smoke test against the real account (creates and cleans a scratch folder)
pwsh -NoProfile -File tools/live-smoke.ps1
# Add -KeepRemote to inspect the remote tree after the run.
```

`--fast`, `--full`, `--force`, `--json`, `--verbose`, `--interactive`,
`--yes`, `--no-color`, `--config`, and `--sync` are recognized. `plan`, `run`,
`doctor`, `status`, and `ignore` are implemented; `init`, `history`, and
`rebuild` are not. `ignore` reports the deciding rule for a path and is
read-only.

### Agent role skills

Project-local role overlays live under `.agents/skills/`:

- `pdsync-orchestrator` — Orca Run/Task/Dispatch workflow, review/fix gates,
  and final merge ownership.
- `pdsync-reviewer` — independent read-only review, severity format, verdict.
- `pdsync-security` — pdsync-specific threat model and security review; loads
  the global `security-audit` skill in guidance mode.
- `pdsync-merger` — pre-merge checklist, merge-commit convention, and
  post-merge verification.
- `pdsync-escalation` — classify and package out-of-scope tasks, SPEC gaps, and
  uncovered design decisions for the orchestrator.

These are workflow overlays, not substitutes for this file or `SPEC.md`. Read
both first; use `/skill:<name>` to force a role. The implementer role is this
file plus `SPEC.md`, so there is no separate implementation skill.

**Escalation policy.** Workers must not silently expand the current PR or invent
behavior that `SPEC.md` does not cover. A concrete follow-up becomes a GitHub
issue; a design decision becomes a `Decision: ...` issue labeled `decision` and
is routed to the user through the orchestrator. Blocking decisions stop the
affected task. Only after the user decides is `SPEC.md` updated and the decision
issue closed.

---

## 2. Architecture and file map

```
src/PdSync/
  Program.cs                 entry point -> CliApp.Run
  Cli/CliApp.cs              argument parsing, command handlers, human/JSON output
  Diagnostics/
    DesktopMappingDetector.cs  read-only Proton Drive desktop-app Mappings.json probe
  Config/
    ConfigModel.cs           raw YAML DTOs + normalized records/enums
    ConfigLoader.cs          discovery, strict YAML, duplicate/unknown-key rejection
    ConfigValidator.cs       defaults, invariants, overlap checks
    PathUtil.cs              env/~/absolute path handling and comparisons
    PathKey.cs               NFC + case-folded identity
  Proton/
    ProtonCli.cs             ONLY process-spawning class: list/info/upload/...
    RemotePath.cs            POSIX remote path codec + validation
    RemoteSnapshot.cs        remote tree snapshot, uid/path lookups
  Remote/
    RemoteIndex.cs           BFS remote listing; fast-mode baseline synthesis
    FastRemoteScan.cs        subtree skip predicate + fast/full run counter
  Scan/
    LocalScanner.cs          iterative local walk, settle check, collisions
    IgnoreRules.cs           .pdignore + inline excludes; explanation decisions
    ContentHash.cs           SHA-1 helpers + FIFO hash cache
    LocalPathGlob.cs         escape CLI glob metacharacters; brace staging flag
  Plan/
    PlanModel.cs             actions/conflicts/skips/adoptions/stats
    Planner.cs               pure decision engine; no I/O
  Exec/
    SyncService.cs           shared prepare for plan/run; run orchestration
    Executor.cs              applies actions, pending journal, state commit
    RunLogger.cs             JSONL run/action/verification log (NFR-10)
    Verifier.cs              size/SHA-1 upload verify; sample/full download-back
    SafetyGuard.cs           empty-tree guard + deletion budget
    LocalTrashStore.cs       local trash moves + per-run manifest
    RunLock.cs               per-sync exclusive lock
    FileSystemUtil.cs        cross-volume move fallback
  State/StateStore.cs        SQLite schema, transactions, read-only mode
tests/PdSync.Tests/          all current tests, including FakeProtonCli E2E
tools/FakeProtonCli/         deterministic disk-backed fake CLI
tools/Spikes/                S1-S14 probe harness
tools/live-smoke.ps1         live real-account smoke test
docs/spikes.md               spike findings and CLI behavior notes
SPEC.md                      authoritative design
```

Deep seams:

- `ProtonCli` hides all process/JSON/path-escaping pain.
- `Planner` is a pure function of baseline + snapshots + config.
- `SyncService` prepares the same snapshots for `plan` and `run`, so plan output
  is a projection of what run would attempt.
- `Executor` owns ordering, crash safety, verification, and state commits.

---

## 3. Non-negotiable behavior

These are the easiest invariants to break. Check `SPEC.md` §2 for the full list.

1. **Two-way sync.** Push and pull are directions of one engine.
2. **Remote deletions → Proton Trash** (`fs trash`). Never permanent delete.
3. **Local deletions caused by remote deletions → local trash** under
   `<stateDir>/trash/<syncId>/<runId>/`, with a manifest. Never bare unlink.
4. **`plan` is pure and read-only.** It must not create/migrate/back up the state
   DB, write a fast counter, acquire a run lock, or modify local/remote files.
   If the DB is absent, use an in-memory empty baseline.
5. **All snapshot paths are relative to the sync root.** The absolute remote
   root is used only when constructing CLI calls.
6. **Identity is case-folded**: `PathKey.Fold = lowercase(NFC(path))` on every
   platform. Case is never synced; case-only renames are no-ops.
7. **Case collisions abort before writes.** If two paths fold to the same key,
   list both and exit 2. The same applies to kind changes (file↔dir).
8. **Ignore rules apply in both directions.** An ignored path is neither
   uploaded nor downloaded, and it must not be used to infer a deletion.
   Previously synced paths that become ignored are marked `frozen`.
9. **`--fast` may only skip remote listing for locally-unchanged subtrees.**
   A skipped subtree produces **no actions**. Run a full listing at least every
   `fastFullEveryRuns` runs (default 20). `plan --fast` reads but does not update
   the counter.
10. **Deletion budgets are safety, not suggestions.** `maxDeletes` or
    `maxDeletePercent` exceeded → exit 6 before any deletion. `--force` bypasses.
11. **One action = one pending journal row.** The pending row is written before
    the CLI call and removed inside the same transaction as the state commit.
12. **Verification uses `claimedDigests.sha1` as a fingerprint, never as a
    security boundary.** Missing digest downgrades to size. `full` downloads
    uploads back; `sample` does so deterministically for missing digests.
13. **One account, serialized CLI calls.** Never run two live CLI operations in
    parallel; caches and rate limits are shared. `ProtonCli` locks internally.
14. **Config is strict.** Unknown keys, duplicate YAML keys, invalid enum values,
    invalid ranges, overlapping sync roots, and a state/log dir inside a sync
    root are config errors (exit 4).

Exit codes: `0` OK, `1` runtime/partial failure, `2` blocking conflict,
`3` auth, `4` config, `5` lock held, `6` safety guard.

---

## 4. Tests and the fake CLI

### FakeProtonCli

`tools/FakeProtonCli` is a process-per-invocation, disk-backed stub. It supports
the real CLI shapes needed by pdsync and adds control commands:

```text
__fake reset
__fake seed <manifest.json>
__fake dump
__fake log
__fake path
__fake set-auth on|off
__fake scenario <scenario.json>
__fake clear-scenario
```

- State directory: `PDSYNC_FAKE_STATE` (default `.pdsync-fake-state/`, gitignored).
- Seed manifest nodes use `path`, `type` (`folder`/`file`), and `content`,
  `contentBase64`, or `sourceFile`; `mtime` optional.
- Scenarios can inject latency, exit codes, auth expiry, and truncated downloads.
- Invocation log: `<stateDir>/invocations.jsonl`.
- `__fake` commands are intentionally excluded from the invocation log.

`tests/PdSync.Tests/PdSync.Tests.csproj` has a `ProjectReference` to
FakeProtonCli with `ReferenceOutputAssembly="false"` to force build ordering.
Tests that share fake state use `[Collection("fake-proton-cli")]`; test
parallelism is otherwise disabled in the assembly/test setup.

### What to test

- Planner truth-table changes: add cases in `PlannerTests.cs`.
- End-to-end CLI/executor behavior: add cases in `CliFakeTests.cs`.
- Remote-index/fake integration: `PlannerFakeTests.cs`.
- Ignore/MAB behavior: `IgnoreRulesTests.cs`.
- Ignore explanation and path resolution: `IgnoreExplainTests.cs`.
- Path folding/validation: `ConfigTests.cs`, `StateStoreTests.cs`.
- Cross-volume moves or staging changes: add a focused unit/integration test.

Prefer behavior assertions over implementation details. The current suite is
**217 tests**.

---

## 5. Live testing rules

- The repo-root `proton-drive.exe` is the real CLI and is gitignored. It is
  authenticated in this environment; `%LOCALAPPDATA%\proton-drive-cli` holds
  session/cache state.
- Never point pdsync at an existing important folder for a smoke test.
- Use `tools/live-smoke.ps1`. It:
  - creates a unique `/my-files/pdsync-live-smoke-<timestamp>` remote root;
  - uses `tmp/<name>` for local roots, state, logs, and artifacts (`tmp/` is
    gitignored);
  - exercises initial push, no-op convergence, local edit, remote-origin pull,
    remote delete → local trash, local delete → Proton Trash, ignored files,
    and fast/full mode;
  - trashes and permanently deletes the scratch remote root on success unless
    `-KeepRemote` is passed.
- If a live run fails, leave the scratch root and inspect
  `tmp/<name>/state`, the run report, and the CLI invocation output before
  cleaning up manually.
- Live CLI operations are slow (often 2–6 s per call). Do not add unbounded
  sleeps; rely on process exit and explicit assertions.

---

## 6. Practical pitfalls

- **`plan` must not touch writable state.** A convenient `StateStore.Open` in a
  plan path is a bug; use `OpenReadOnly`.
- **Folder mtimes do not change when children change** (spike S4). Detect folder
  changes via remote UID mismatch and baseline presence, not mtime.
- **CLI paths are glob-expanded.** Escape `[`, `*`, `?` with
  `LocalPathGlob.Escape`. Names with `{a,b}` brace-expand; upload under a safe
  temporary name and `fs rename`.
- **Remote names are case-sensitive** even though local identity is folded.
  Never create a second remote node that differs only by case.
- **Remote names must be path-safe.** Skip empty names, names containing `/`,
  `.`/`..`, or control characters; mark the snapshot incomplete.
- **Backslashes are literal filename characters** on Unix canonical paths. The
  public `IgnoreRules.IsIgnored` accepts native separators for CLI input; the
  scanner's `IsIgnoredNormalized` does not.
- **MAB.DotIgnore gaps:** no reliable backslash un-escaping (`\!important`),
  regex-flavored character classes, and invalid `[`/`foo[` throw
  `ArgumentException`. Keep behavior compatible with MAB rather than
  hand-rolling a second matcher.
- **Staged uploads/downloads matter.** Upload staging protects against CLI glob
  expansion and casing mismatches; download staging plus verification prevents a
  truncated remote download from corrupting a local file.
- **State commit and pending row share a transaction.** If you add a new action,
  journal it, execute it, then commit state in one transaction.
- **`--fast` and `--full` are mutually exclusive.** Combine either with config
  `fast: true` correctly; a fast decision still needs a full run every N runs.
- **Cross-volume moves:** use `FileSystemUtil`, not raw `File.Move` /
  `Directory.Move`, when moving between `stateDir` and the sync root.
- **Respect the retry classification.** `ProtonCli` retries transient failures
  (timeout, 429, 5xx, network reset) with bounded exponential backoff, but only
  for idempotent calls. Non-replayable calls (`create-folder`, `upload`, `move`,
  `rename`, `trash`) retry only on spawn failure or a 429 rejection; a timeout
  or 5xx is reported and reconciled next run. Never retry auth (`exit 3`) or
  permanent failures. When adding a new CLI call, pass the correct
  `ProtonCallKind`; do not add a second retry loop at the executor layer.

---

## 7. Current status and open work

Implemented and fake-tested:

- Config loader/validator, strict YAML, path identity.
- SQLite state store, read-only plan mode, migrations/backups.
- Local scanner, `.pdignore` + inline excludes, hash cache.
- Remote index, case/collision detection, fast-subtree synthesis.
- Pure planner: creates, updates, deletes, conflicts, renames, adoption,
  directory ordering, incomplete scans.
- `plan`, `run`, `doctor`, `status` CLI.
- `ignore` CLI (SPEC §3.3 rule 7): resolves a relative or rooted path against a
  sync root and explains the deciding rule via `IgnoreRules.Decide` — verdict,
  pattern, rule number, ignore-file line or inline `exclude[i]`, and matched
  ancestor. A trailing separator asks for directory intent; `--json` is stable.
  Read-only: no state DB open/create, run lock, fast counter, or log files.
- Executor: uploads/downloads, staging, verification, state commits, pending
  journal, local trash, Proton Trash, pruning, safety budgets, lock.
- Fast/full mode and tracker.
- `doctor` desktop-app mapping detection: reads `Mappings.json` best-effort,
  reports every distinct exact/ancestor/descendant overlap with configured
  local roots, and treats missing, malformed, or schema-drifted files as
  non-fatal warnings (the mapping probe itself does not touch the state DB).

Live smoke: **passed** on 2026-10-31 against the real account using the script
in §5. It covered push, pull, both deletion directions, ignore behavior, and
fast/full mode.

Known gaps / likely next work:

- No live test in the normal test suite; live smoke remains a manual script.
- Transient retry/backoff is implemented in `ProtonCli`
  (`ProtonRetryOptions`); FakeProtonCli scenario tests cover 429/5xx,
  exhaustion, permanent failures, auth, and timeout recovery.
- `logDir` JSONL run/action/verification logging is implemented; `history`
  does not read it yet.
- `init`, `history`, and `rebuild` are not implemented.
- Interactive conflict prompting is not implemented; `ask` currently uses the
  non-interactive fallback.
- S15 restore-by-UID is not wired (restore is not used by the sync engine yet).
- Directory rename detection is not implemented; file renames are.
- Desktop-app mapping detection is implemented for the Windows desktop app's
  known per-user/machine-wide `Mappings.json` locations; other platforms are a
  no-op because the app does not publish that file there.

When adding a feature, update `SPEC.md` if it changes a contract, then add or
adjust focused tests and rerun the build/test commands above. If CLI/executor
behavior changes, rerun the live smoke script.
