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
| S8 | Auth expiry | **Deferred** — expiring the session would break the rest of the suite. |
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
runs. Restore must address the trashed node by **UID** (`/trash/<uid>`) when
names collide; name-based `/trash/name` is only safe when unique. This is S15,
to verify in M1.

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

## Not tested

| Item | Why | Plan |
|---|---|---|
| S8 auth expiry | Would break the authenticated session for the suite | Detect from stderr; add an opt-in fault-injection test later |
| S15 restore by UID | Not yet probed | M1, using a collision with two same-named trash entries |
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
