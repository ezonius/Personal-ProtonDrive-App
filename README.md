# pdsync

Declarative two-way sync between a local folder tree and Proton Drive, built on
the official [`proton-drive` CLI](https://proton.me/drive).

`pdsync` never talks to Proton's network APIs itself. It plans from three
snapshots — local files, the remote tree, and a SQLite baseline of the last
synced state — and then applies the plan through the Proton CLI. Remote
deletions go to Proton Trash; local deletions caused by remote changes go to a
local trash with a per-run manifest. Nothing is hard-deleted.

- **Design and contracts:** [`SPEC.md`](SPEC.md) — the source of truth.
- **Operator guide:** [`docs/runbook.md`](docs/runbook.md) — publish, Task
  Scheduler, exit codes, logs, retention, `status`/`history`, 7-day soak.
- **Agent orientation:** [`AGENTS.md`](AGENTS.md).
- **CLI behavior notes:** [`docs/spikes.md`](docs/spikes.md).

## Commands

```
pdsync init                create a config and a default .pdignore
pdsync doctor              check CLI, version, auth, roots, state, leftovers
pdsync plan   [sync...]    show actions; never writes
pdsync run    [sync...]    apply actions and commit state
pdsync status [sync...]    recent runs and pending rows
pdsync history [sync...]   recent runs and action totals; never writes
pdsync ignore <path>       explain the ignore rule for a path; never writes
pdsync trash list          list local trash runs; never writes
pdsync trash purge --older-than <days> [--dry-run] [--yes]
pdsync rebuild <sync> --policy=<p> [--yes] [--dry-run]
```

Options: `--config <path>`, `--sync <name>`, `--json`, `--verbose`, `--yes`,
`--fast`, `--full`, `--force`, `--cleanup-staged` (run only) and
`--limit <n>` (listing commands) — the global options printed by `--help`
(`--policy` and the `--` end-of-options marker are listed with their commands).
The parser also accepts `--no-color` and `--interactive`, neither of which
appears in the usage banner. `--interactive` only matters for `conflict: ask`:
it turns the silent keep-both fallback into a blocking conflict (exit 2) unless
the conflict is resolved at an interactive prompt; `plan` and a non-TTY `run`
therefore fail with exit 2, and `--json`/`--yes` never prompt.

Exit codes: `0` success, `1` runtime/CLI failure, `2` blocking conflicts,
`3` auth, `4` config, `5` lock held, `6` safety guard.

## Quick start

```powershell
dotnet build pdsync.sln

# create a config + default ignore file
src/PdSync/bin/Debug/net8.0/PdSync.exe init --config .\pdsync.yaml

# preflight, preview, apply, verify
src/PdSync/bin/Debug/net8.0/PdSync.exe doctor  --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe plan    --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe run     --config .\pdsync.yaml
src/PdSync/bin/Debug/net8.0/PdSync.exe history --config .\pdsync.yaml
```

Config format and defaults are documented in `SPEC.md` §3.2. Paths support `~`
and `${VAR}`/`%VAR%` expansion; relative paths in the YAML are resolved against
the config file's directory. Config is strict: unknown keys, duplicate keys,
invalid values, and overlapping sync roots are errors (exit 4).

## Build, test, publish

```powershell
# build (expected: 0 warnings, 0 errors)
dotnet build pdsync.sln -v quiet --nologo

# unit + fake-CLI end-to-end tests
dotnet test tests/PdSync.Tests/PdSync.Tests.csproj --no-build --nologo `
  --logger "console;verbosity=minimal"

# self-contained single-file exe -> dist/win-x64/PdSync.exe
pwsh -NoProfile -File tools/publish.ps1 -Runtime win-x64

# smaller framework-dependent build -> dist/win-x64-portable/PdSync.exe
pwsh -NoProfile -File tools/publish.ps1 -SelfContained:$false
```

`tools/publish.ps1` smoke-tests the produced exe (`--help`) and prints the path
and size. `dist/` is gitignored. See
[`docs/runbook.md` §1](docs/runbook.md#1-publish-the-executable) for the raw
`dotnet publish` command and the size/runtime of each variant.

## Scheduling

[`docs/runbook.md` §4](docs/runbook.md#4-windows-task-scheduler-setup) covers
Windows Task Scheduler setup: least-privilege account choice, "run whether the
user is logged on or not", start-in, a `schtasks` example, and why `doctor`
should run before the first scheduled sync.

## Live testing

`tools/live-smoke.ps1` exercises the real account against a scratch remote root
(push, pull, both deletion directions, ignore rules, fast/full mode) and cleans
itself up unless `-KeepRemote` is passed:

```powershell
pwsh -NoProfile -File tools/live-smoke.ps1
```

`tools/FakeProtonCli/README.md` documents the deterministic fake CLI used by the
test suite, including its `__fake` seed/scenario/log control commands.
