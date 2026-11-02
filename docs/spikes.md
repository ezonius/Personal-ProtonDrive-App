# M0 spike results

Date: 2026-10-31
CLI: `proton-drive` 0.9.0+75612299 (SDK js 0.22.2)
Host: Windows, PowerShell 7, .NET 8
Scratch remote: `/my-files/pdsync-test/spike-<timestamp>` (trashed after each run)
Harness: `dotnet run --project tools/Spikes -c Release`
Raw output: `docs/spikes-results.json` / `docs/spikes-results.md` (regenerated, gitignored)

## Summary

| ID | Question | Result |
|---|---|---|
| S1 | Does `create-new-revision` create a new file, and does upload support `--json`? | Yes, it creates and updates. `--json` returns **counters only**, no per-file data. |
| S2 | Is local mtime/size preserved? | `claimedModificationTime` is the local mtime **to the millisecond**. `claimedSize` is plaintext bytes; `storageSize` adds encryption overhead. |
| S3 | What does "same content skipped" mean? | Same name + same content → `Skipped: 1`, revision unchanged. Same content under a **different name** → uploaded as a new file. |
| S4 | Does folder `modificationTime` change when a child changes? | **No.** Root and subfolder mtimes stayed identical after adding children. |
| S5 | CLI call latency | `list` 1.7–3.5 s, `info` 1.8–4.2 s, small upload 3.9–4.0 s, small download 4.4–6.6 s. |
| S6 | Download conflict semantics | `remove` replaces the local file; `skip` leaves it; download into an empty dir works. |
| S7 | Weird names round-trip | Spaces, Unicode, `#`, `+`, `%`, `&`, `$`, `[1]` all upload and list correctly **after escaping `[`**. |
| S8 | Auth expiry | Live expiry still **deferred** (it would break the rest of the suite). The pdsync contract is covered deterministically through FakeProtonCli fault injection — see §7. |
| S9 | Trash and restore | `fs trash a b` works (multi-path). `/trash/name` restore works. `/trash` is account-global and **names can repeat**, so name-based restore is ambiguous. |
| S10 | Folder creation | `create-folder` fails if the folder exists or the parent is missing. Uploading a folder twice with `merge` is idempotent. |
| S11 | Is the remote case-sensitive? | **Yes.** Uploading `Name.txt` where `name.txt` exists creates **two nodes**. |
| S12 | Is `claimedModificationTime` accurate? | Millisecond-exact, matching the local mtime (`delta = 0 ms`). |
| S13 | Case-collision download + hardlink upload | `download -f remove` replaced local `case.txt` with remote `Case.txt` content. Hardlink staging uploads normally. |
| S14 | Local path globbing + upload JSON | Paths are **glob-expanded**. `[` → `[[]`; `{a,b}` breaks and needs temp-name + `fs rename`. Upload JSON is `{transferredItems,transferredBytes,skippedItems,failedItems,failures[]}`. |

## Findings that changed the design

### 1. Plaintext SHA-1 is available remotely

`activeRevision.claimedDigests.sha1` is the **plaintext SHA-1** supplied by the
uploading client:

- `alpha.txt` v2 (`"alpha v2 is longer\n"`, 19 bytes) → `9928bcafa36c996df03d214d08d2bb1f423a01e7`
- locally computed SHA-1 → `9928bcafa36c996df03d214d08d2bb1f423a01e7`
- `sha1Verified` is `false`; it is a claim, not a server attestation.

Consequences:

- Verification is no longer size-only: compare local SHA-1 to the remote claim.
- Change detection can prove convergence even when a revision UID changed.
- Adoption on first run can match by hash, not just size+mtime.
- Rename detection matches by SHA-1.
- Files uploaded by older/third-party clients may omit the digest; fall back to
  size + mtime + revision UID.

Caveat: `sha1Verified:false` means this is a fingerprint for change detection,
not a security guarantee. We never use it for authentication or integrity
against an adversary.

### 2. Folder mtime cannot drive `--fast`

Adding children to the root and to a subfolder left `modificationTime` unchanged
in both cases. Therefore `--fast` cannot skip a listing because a folder "looks
unchanged". The revised algorithm skips a subtree only when the **local** subtree
is unchanged against the state DB, and a skipped subtree produces **no actions at
all** (so a remote-only change is deferred, never acted on stale). A full scan is
forced every `fastFullEveryRuns`.

### 3. Local paths are glob patterns

`fs upload` expands local path arguments. A single unmatched path aborts the
whole batch with `No paths matched` and uploads nothing.

- `brackets[1].txt` fails literally; `brackets[[]1].txt` succeeds.
- `braces{a,b}.txt` fails literally; a safe temp name + `fs rename` succeeds.
- `]`, `{`, `}` alone are literal; `{...,...}` is brace expansion.
- `fs rename` treats `newName` literally, including braces.
- Uploading a brace name via temp+rename **replaces** the node on update, losing
  that node's revision history. Accepted and reported.

### 4. Trash is account-global and ambiguous by name

`fs list --json /trash` returned duplicate `trash-b.txt` entries from different
runs. This is S15; the answer below was probed on the real account on
2026-11-02 with `proton-drive.exe` 0.9.0.

### 4.1 S15 answer: restore is name-only, and duplicate names are arbitrary

With two trashed files both named `x.txt`:

- `fs restore /trash/x.txt` **works** and restores an arbitrary one (no
  ambiguity error).
- `fs restore /trash/<full-node-uid>` -> `Trashed node not found`, even when
  that uid exists exactly once in `fs list --json /trash`.
- `fs restore /my-files/<root>/c/y.txt` -> `Path "..." is not supported`.
- `fs restore /trash/<uid>/y.txt` -> `Browsing trashed folders is not supported`.

**Conclusion:** UID-addressed restore is **not available** in this CLI version.
`fs restore` is name-only, and a duplicated name restores an arbitrary node
without warning. pdsync therefore refuses to guess: `pdsync trash restore`
restores only when the recorded name matches exactly one trashed node **and**
that node's uid matches the uid persisted by the executor. Ambiguity or a uid
mismatch exits 2 and points the operator at the Proton web app. The fake CLI
mirrors the name-only resolution (including first-match-on-duplicate), while
the pdsync tests drive the ambiguity path off `fs list /trash`.

Evidence is from the coordinator's live probe; the pdsync behavior itself is
covered by fake-CLI tests (no live restore in the automated suite).

### 5. Per-call cost dominates

Each CLI call costs seconds. A 100-folder remote scan is minutes. Consequences
already reflected in the spec:

- serialized calls, no parallelism;
- one parent listing verifies all uploads into that parent (never `fs info`
  per file);
- progress output and a per-call timeout;
- `--fast` is valuable but must be conservative.

### 6. Case behavior confirmed

- Remote is case-sensitive and will happily hold `name.txt` + `Name.txt`.
- Windows cannot represent both; hence the folded-identity + collision-abort
  policy.
- `download -f remove` onto a case-colliding local file replaced it and adopted
  the remote casing — consistent with "casing is local".
- Hardlink staging uploads behave like normal files.

## 7. Auth expiry (S8)

The real CLI was **not** deliberately expired: invalidating the account session
would break the rest of the suite and require a live re-login. The pdsync-side
contract is instead exercised deterministically with the fake CLI, which can
return exit code 3 after an arbitrary invocation.

Expected behavior, covered by `tests/PdSync.Tests/AuthExpiryTests.cs`:

- A mid-run auth failure aborts the run with **exit code 3** (`ProtonAuthException`).
- The failing CLI call is **never retried**; `invocations.jsonl` shows exactly
  one attempt. CLI-level idempotent reads are covered separately by
  `ProtonCliRetryTests.AuthFailure_IsNeverRetried`; this is distinct from the
  transient retry/backoff path in SPEC §6.4.
- No later action runs, and no state is committed for the failed action.
  Actions already committed before the failure stay committed. When auth fails
  before the primary CLI call (the covered cases), no transfer or deletion is
  performed. A **post-transfer** auth failure — after a successful upload but
  before verification or a staged rename — can leave a remote revision or a
  `pdsync-upload-<12 lowercase hex>` node behind. Same-run cleanup is
  deliberately skipped on auth failure because the session is gone; the next
  run's planner trashes the baseline-absent leftover with a
  `cleanup staged upload` action instead of downloading it. Non-auth
  rename/verification failures still attempt same-run best-effort cleanup.
- The failed action is **not committed to state**: its pending journal row is
  removed and the entry transaction rolls back. The next run re-plans it from
  the local and remote snapshots.
- Stderr includes the recovery command `proton-drive auth login`.

These tests run automatically in the normal `dotnet test` suite under
`Category=AuthExpiry`. They are **not opt-in** and do not require a real Proton
account, because the fault is injected inside the fake process. A live
auth-expiry probe remains out of scope until it can restore the session safely.

**Recovery:** run `proton-drive auth login` (or `proton-drive auth status` to
confirm), then rerun `pdsync run --config <config>`. `pdsync plan` is read-only
and will show the remaining work before the rerun.

```powershell
dotnet test tests/PdSync.Tests/PdSync.Tests.csproj --no-build --nologo `
  --filter "Category=AuthExpiry"
```

## Not tested

| Item | Why | Plan |
|---|---|---|
| S8 live auth expiry | Would break the authenticated session for the suite | pdsync's exit-3/abort/no-retry/no-commit contract is covered by fake-CLI tests (§7); only the real CLI's silent-refresh behavior remains unprobed |
| S15 restore by UID | Answered 2026-11-02: the CLI has no UID-addressed restore and duplicate names are arbitrary | Implemented the name-unique + recorded-uid fallback (§4.1); live smoke still does not exercise a conflict rename or restore |
| 429 / rate limits | Would require hammering the API | Retry with backoff implemented for all calls on 429 (request did not execute); idempotent calls also retry timeout/5xx/network resets. Measure tuning in a long live run. |
| Large files / resume | Time and quota | M2, with a ≥1 GB file |
| Long paths (>260) | Not exercised | M5, Windows-specific test |
| Unicode normalization (NFC/NFD) | Windows host is NFC | Unit tests with crafted names |

## Reproduce

```powershell
dotnet run --project tools/Spikes -c Release
# keep the scratch folder for inspection:
dotnet run --project tools/Spikes -c Release -- --keep
```

The harness creates its own scratch remote folder, runs S1–S14, writes
`docs/spikes-results.{json,md}`, then trashes the remote scratch folder and
deletes the local temp tree.
