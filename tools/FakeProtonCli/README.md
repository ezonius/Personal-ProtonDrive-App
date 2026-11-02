# FakeProtonCli

A stateful, scenario-driven stand-in for `proton-drive`, used by integration
tests. It is **not** a Proton client: no network, no crypto, no account. It
mirrors the CLI surface and the behaviors we measured in M0 so the sync engine
can be tested deterministically and offline.

## Build and run

```powershell
dotnet build tools/FakeProtonCli/FakeProtonCli.csproj
$env:PDSYNC_FAKE_STATE = "$env:TEMP\fake-state"
tools/FakeProtonCli/bin/Debug/net8.0/FakeProtonCli.exe __fake reset
tools/FakeProtonCli/bin/Debug/net8.0/FakeProtonCli.exe fs list --json /my-files
```

The built apphost `FakeProtonCli.exe` can be used directly as `proton.cli` in
tests. Each invocation is a separate process; state lives in
`PDSYNC_FAKE_STATE` (default: `./.pdsync-fake-state`).

Smoke test (47 checks):

```powershell
pwsh -NoProfile -File tools/FakeProtonCli/smoke.ps1
```

## Fidelity

Mirrors the real CLI where it matters for the sync engine:

| Behavior | Fake |
|---|---|
| `fs list/info/create-folder/upload/download/move/rename/trash/restore/delete/empty-trash/size` | implemented |
| `--json` output shapes for list/info/upload | match the shapes observed in M0 |
| `claimedDigests.sha1` = plaintext SHA-1 | computed locally, `sha1Verified: false` |
| Same name + same content is skipped | yes |
| Folder `modificationTime` does not change when children change | yes (S4) |
| Remote names are case-sensitive | yes (S11) |
| Local paths are glob-expanded; `[` must be escaped as `[[]` | minimal: unescapes `[[]`, `[]]`, `[*]`, `[?]`; no full globbing |
| `No paths matched` aborts the whole upload batch | yes |
| Download `remove`/`skip`/`rename`, mtime preservation | yes |
| `/trash` lists trashed roots; restore by `/trash/<uid>` | yes |
| Auth failures exit 3 | yes |
| Structurally invalid cases (missing parent, duplicate name, collision) | yes |
| Real encryption, sharing, photos/albums, rate limits, thumbnails | **no** |
| Brace expansion (`{a,b}`) | **no** — the adapter stages those files; the fake never sees the pattern |
| Undecryptable names / UID fallback paths | **no** |

## Control commands

These are not part of the real CLI and never appear in the invocation log.

```text
__fake reset                     # empty tree, auth on
__fake seed <manifest.json>      # replace the tree from a manifest
__fake dump                      # print state.json
__fake log                       # print invocations.jsonl
__fake path                      # print the state dir
__fake set-auth on|off
__fake set-digest on|off         # off = uploads and seeds hide claimedDigests.sha1
__fake set-version <version>     # override the --version line (default 0.9.0)
__fake scenario <scenario.json>  # install a fault-injection scenario
__fake clear-scenario
```

### Seed manifest

```json
{
  "auth": true,
  "nodes": [
    { "path": "/my-files/docs", "type": "folder" },
    { "path": "/my-files/docs/a.txt", "type": "file", "content": "hello" },
    {
      "path": "/my-files/docs/b.txt",
      "type": "file",
      "sourceFile": "C:/fixtures/b.txt",
      "mtime": "2024-01-02T03:04:05.678Z"
    }
  ]
}
```

`content`, `contentBase64`, or `sourceFile` supply bytes. Parent folders are
created automatically. Seeding replaces the entire tree and blob store.

### Scenario

```json
{
  "latencyMs": 0,
  "rules": [
    {
      "match": "^fs upload",
      "times": 1,
      "delayMs": 5000,
      "exitCode": 1,
      "stderr": "simulated transient failure"
    },
    { "match": "^fs list", "times": 1, "authExpired": true },
    { "match": "^fs download", "times": 1, "truncateDownload": true }
  ]
}
```

- `match` is a regex over the joined argv (case-insensitive; falls back to
  substring on invalid regex).
- `times` counts across processes; `0` = unlimited.
- `delayMs` simulates latency/timeouts; `exitCode` + `stderr` simulate failures.
- `authExpired` exits 3; `truncateDownload` writes half the file and sets
  `failedItems` (the adapter's staging must not install it).
- First matching rule wins.

## Invocation log

Every real-CLI invocation is appended to `<state>/invocations.jsonl`:

```json
{"ts":"...","cwd":"...","args":["fs","upload","--json","C:\\x\\a.txt","/my-files/docs"]}
```

Integration tests use this to assert call counts and ordering (for example,
"one parent listing verifies all uploads", "no `fs info` per file").

## Intended test shape

```csharp
Environment.SetEnvironmentVariable("PDSYNC_FAKE_STATE", stateDir);
var cli = new ProtonCli(fakeExePath);
// seed -> run engine -> assert state, local tree, and invocation log
```

Because state is process-shared on disk, run one fake at a time per state
directory. Use a fresh directory per test.
