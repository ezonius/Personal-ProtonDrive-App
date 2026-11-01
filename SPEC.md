# pdsync — Two-way sync for Proton Drive

Status: Draft v0.2 · Date: 2026-10-31
Changelog: v0.3 — M0 spike results: plaintext SHA-1 is available remotely, folder
mtime is useless for `--fast`, local paths are glob-expanded. v0.2 — C#/.NET 8
stack, case-folded identity, `newer-wins` default, per-sync `.pdignore`.
Target: personal use, Windows-first, cross-platform where free.
Implementation: C# on .NET 8 (LTS), orchestrating the official `proton-drive` CLI.

---

## 1. Problem and constraints

We want git-like, declarative, repeatable syncing between local directories and
Proton Drive folders, with `.gitignore`-style exclusion.

Hard constraints discovered from the CLI (`proton-drive` 0.9.0, SDK js 0.22.2):

| Constraint | Consequence for this project |
|---|---|
| There is **no `sync` command**. Only `filesystem upload/download/list/move/rename/trash`. | We implement the sync engine ourselves on top of per-file primitives. |
| SDK has a **Sync module "coming soon"**, and the SDK has **no auth/session layer**. | Not viable for a personal tool today. CLI is the integration surface. |
| Remote metadata exposes `activeRevision.uid`, `claimedSize`, `claimedModificationTime`, and **`claimedDigests.sha1`** — the plaintext SHA-1 supplied by the uploading client (`sha1Verified: false`). | Content can be compared and verified by hash whenever the digest is present. Older or third-party uploads may omit it; fall back to size + mtime + revision UID. Treat SHA-1 as a fingerprint, not a security guarantee. |
| `upload` has conflict strategies `create-new-revision / rename / replace / skip`, and "files with the same content are automatically skipped". | We plan transfers ourselves; CLI strategies are a safety net, not the sync logic. |
| `download` has no `replace` strategy, only `remove` (delete local, then download). | Download must be **staged to a temp file**, verified, then atomically swapped in, with the old local file moved to a local trash. |
| CLI is single-account per OS user (`%LOCALAPPDATA%\proton-drive-cli`, shared SQLite caches). | Exactly **one account**, all CLI calls **serialized** (no concurrency), one sync at a time. |
| Every CLI invocation is a process spawn with multi-hundred-ms latency; remote listing is per-folder, not recursive. | Full remote scans are expensive → snapshot in the state DB, incremental listing strategy, batched calls where possible. |
| Remote paths are POSIX, `/` inside a node name escaped as `\/`; undecryptable names can fall back to UID paths. | Need a tested remote path **codec**; undecryptable nodes are skipped with a warning. |
| Proton Docs/Sheets are skipped by `download`. | Skip + warn; never treat as a local deletion. |
| The Proton Drive **desktop app** may already sync some of the same folders. | `doctor` warns on overlap; two sync engines on one tree is unsupported. |

Non-goals for v1: sharing/albums/photos, selective sync UI, background daemon,
multiple Proton accounts, conflict-resolution UI, preserving POSIX permissions,
deduplication, or bypassing Proton's encryption.

---

## 2. Decisions (locked)

| # | Decision |
|---|---|
| D1 | **Two-way sync** is the target. Push and pull are directions of the same engine, not separate features. |
| D2 | Remote deletions caused by local deletions go to **Proton Trash** (`fs trash`), never permanent delete. |
| D3 | Local deletions caused by remote deletions go to a **local trash directory**, never `unlink` straight away. |
| D4 | Stack: **C# on .NET 8 (LTS)**; self-contained single-file executable via `dotnet publish` (no AOT/trimming in v1). |
| D5 | Runtime: **one-shot CLI**, scheduler-friendly (Windows Task Scheduler). No daemon, no watch mode in v1. |
| D6 | Config: **YAML**, versioned schema, bound with YamlDotNet and validated by explicit invariants; unknown keys rejected. |
| D7 | Ignore file: `.pdignore`, **gitignore semantics** including `!` negation, via MAB.DotIgnore plus compatibility tests. |
| D8 | One account, **all CLI calls serialized**, one run at a time per machine (lock file). |
| D9 | `--dry-run` is the default mental model: `plan` must be a pure projection of the same planner that `run` uses. |
| D10 | Default conflict policy is **`newer-wins`**, with `keep-both` as the fallback whenever mtimes are too close or untrustworthy. |
| D11 | Local trash is kept **indefinitely** for now; pruning is a later command, not automatic. |
| D12 | **One `.pdignore` per sync root**, analogous to one `.gitignore` at a repo root. No global ignore file. Nested `.pdignore` files are not read in v1. |
| D13 | `--fast` may skip remote listing only for subtrees with **no local changes**; skipped subtrees produce **no actions at all**, and a full scan runs at least every `fastFullEveryRuns`. Folder `modificationTime` is useless for this (S4). |
| D14 | **Case is not synced.** Identity is case-folded on every platform; a case-only local rename is a no-op. (§5.4.1) |
| D15 | Post-transfer verification is **remote plaintext SHA-1 == local SHA-1** when present; fall back to size + revision; sampled download-back only for missing digests (§6.5). |
| D16 | **One entry per case-folded key on each side.** A remote (or local) pair such as `Name.txt`/`name.txt` is a hard collision that aborts the sync before any destructive action. Remote casing is canonical. |
| D17 | Ignore matching is **case-sensitive by default** (`ignoreCase: sensitive`) so Windows and Linux produce the same result. |

---

## 3. User-facing behavior

### 3.1 Commands

```
pdsync init                      # create config + .pdignore interactively
pdsync doctor [--json]           # binary, version, auth, paths, overlaps, state
pdsync plan [sync...] [--json]   # show what would happen; never writes
pdsync run  [sync...] [--json]   # apply
pdsync status [--json]           # last run per sync, pending conflicts
pdsync ignore <path>             # explain which pattern matched (debug)
pdsync history [sync]            # recent runs and actions
pdsync rebuild <sync> --policy=… # rebuild state from local+remote (last resort)
```

Global flags: `--config <path>`, `--verbose`, `--json`, `--no-color`, `--yes`.
Per-run flags: `--fast` (incremental remote listing), `--full` (force a complete
scan, overriding `--fast`), `--force` (override the deletion budget).
`plan` accepts the same scan flags so its output matches the run it predicts.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Success, including "nothing to do" |
| 1 | Runtime/CLI failure |
| 2 | Unresolved conflicts (run completed the safe subset) |
| 3 | Authentication required or expired |
| 4 | Config/schema error |
| 5 | Another run holds the lock |
| 6 | Safety guard tripped (too many deletions) |

### 3.2 Config file

Resolution order: `--config` → `PDSYNC_CONFIG` → `./pdsync.yaml` → `%APPDATA%\pdsync\config.yaml`.
Paths support `~` and `${VAR}` / `%VAR%` expansion; all paths are resolved to absolute.

```yaml
version: 1

proton:
  cli: "C:/Tools/Personal-ProtonDrive-App/proton-drive.exe"
  minVersion: "0.9.0"

stateDir: "%LOCALAPPDATA%/pdsync"
logDir: "%LOCALAPPDATA%/pdsync/logs"

defaults:
  direction: two-way            # two-way | push | pull
  conflict: newer-wins          # newer-wins | keep-both | local-wins | remote-wins | ask
  onLocalDelete: remote-trash   # remote-trash | keep
  onRemoteDelete: local-trash   # local-trash | keep
  initialSync: adopt            # adopt | keep-both | local-wins | remote-wins
  ignoreFile: .pdignore         # one per sync root, no global file
  settleSeconds: 3              # skip files modified more recently than this
  mtimeToleranceMs: 2000        # FAT/exFAT, clock skew, "too close to call"
  detectRenames: true
  ignoreCase: sensitive         # sensitive | insensitive (keep sensitive for cross-platform parity)
  pruneEmptyRemoteDirs: true
  fast: false                   # skip remote listing for locally-unchanged subtrees
  fastFullEveryRuns: 20         # --fast must still do a periodic full scan
  safety:
    maxDeletes: 200             # 0 = unlimited
    maxDeletePercent: 10        # of tracked entries
  verify:
    mode: hash                  # hash | size | sample | full
    samplePercent: 5            # when mode=sample
    minSizeMb: 10               # only sample files at least this big
  localTrash:
    retention: forever          # forever | <days> (manual purge only in v1)

syncs:
  documents:
    local: "~/Documents"
    remote: "/my-files/Backups/Documents"
    enabled: true
    conflict: newer-wins
    fast: true
    exclude: ["*.tmp"]

  projects:
    local: "E:/Projects"
    remote: "/my-files/Backups/Projects"
    direction: push
    ignoreFile: "E:/Projects/.pdignore"
    verify:
      mode: sample
      samplePercent: 10
```

Rules:

- `remote` must be under a real CLI tree (`/my-files`, `/shared-with-me`,
  `/devices`, …). `/`, `/trash`, `/photos`, `/albums` are rejected for v1, and a
  section root such as `/my-files` is rejected as a sync root.
- Local and remote roots are compared with the same folded identity used for
  entries (NFC + lowercase), so roots differing only by case overlap and are
  rejected on every platform.
- `local` must be an existing absolute directory or a drive root is rejected.
- Syncs must not overlap (nested local roots or nested remote roots) — they would
  fight over the same state and produce duplicate transfers.
- `stateDir` must not be inside any `local` root; it is force-excluded regardless.
- Unknown keys and unknown enum values are config errors (fail fast, no guessing).
- One config may define many syncs; `pdsync run` runs them sequentially in
  declaration order.

### 3.3 Ignore file

`.pdignore` lives at the **local sync root** (configurable). Semantics are
`.gitignore`'s:

- blank lines and `#` comments ignored
- `!pattern` re-includes
- trailing `/` matches directories only
- leading `/` anchors to the ignore file's directory
- `*`, `?`, `**`, character classes
- last matching pattern wins

Additional rules specific to sync:

1. Ignored paths are neither uploaded nor downloaded.
2. Ignoring a path **never deletes** anything, on either side. It freezes
   existing content.
3. If a previously synced path becomes ignored, it is dropped from the active
   state with a `frozen` record; it is not trashed.
4. Ignored files do not count as local deletions (and vice versa).
5. The ignore file itself is synced unless explicitly ignored.
6. Patterns are evaluated on the **relative POSIX path** with a trailing `/` for
   directories, matching the `ignore` package's contract.
7. `pdsync ignore <path>` prints the deciding pattern and rule number.

Example `.pdignore`:

```gitignore
node_modules/
dist/
*.tmp
*.log
!keep.log
/scratch/
**/.DS_Store
Thumbs.db
```

Each **sync root has exactly one `.pdignore`**, the way a repo has one
`.gitignore` at its root. There is no global ignore file: two syncs never share
implicit ignore rules. `ignoreFile` may point elsewhere, but it is still one file
per sync.

Nested `.pdignore` files in subdirectories are **not read in v1**; they are
synced as ordinary files. If a subdirectory ever needs its own rules, the sync
root's `.pdignore` can express them with anchored and `**` patterns.

Inline `exclude:` patterns are merged after the file (later rules win).
`include:` is not supported in v1 — use `!` patterns.

---

## 4. Core model

### 4.1 Three snapshots

For every entry (path) the planner compares three inputs:

- **B** — base: what the state DB recorded at the end of the last successful sync
- **L** — local: current filesystem state (exists, kind, size, mtime, hash)
- **R** — remote: current Drive state (exists, kind, uid, revision uid, size, mtime)

Change flags:

```
LΔ = L differs from B        (existence, kind, size, or content SHA-1)
RΔ = R differs from B        (existence, kind, activeRevision.uid,
                              or a claimed SHA-1 mismatch when both exist)
```

`activeRevision.uid` detects any new revision. `claimedDigests.sha1` detects
content convergence even when a revision was re-created (re-upload, restore,
rename). Remote `modificationTime` is not trusted for change detection (it can be
updated by unrelated metadata changes); only `newer-wins` uses
`claimedModificationTime`.

Content hashing is SHA-1 (hardware-accelerated), computed locally and compared to
Proton's plaintext `claimedDigests.sha1`. It is a **fingerprint and change
detector**, not a security primitive: `sha1Verified: false` means Proton has not
attested the uploader's claim. A digest is only trusted when the uploading client
was ours; for third-party files it is advisory. Renames, adoption, convergence,
and verification all use it.

### 4.2 Path identity

- `path` is the display path (relative, POSIX separators, Unicode NFC).
- `path_key = NFC(path).ToLowerInvariant()` is the **identity used everywhere**:
  state DB keys, diffing, planner lookups, ignore-adjacent bookkeeping. This is
  true on every platform, not just Windows, so Windows and Linux clients agree.
- Casing is presentation metadata. It is **never** part of identity and **never**
  synced. A local `name.txt` → `Name.txt` rename produces no remote action.
- **Uniqueness invariant:** at most one entry per `path_key` within a directory,
  on each side. If a scan sees two (`Name.txt`, `name.txt`), that is a
  `CaseCollision` and aborts the affected sync with exit 2 and both paths listed.
  On Windows this is unavoidable; on Linux it is a rare but real user state.
- **Canonical casing** is whatever the remote name is. Downstream operations use
  the remote name when addressing a file. Uploads of a path whose local basename
  differs in case from the remote name go through a staging entry named with the
  canonical casing (§6.2), so we never create a second case variant.
- Names are escaped for the CLI with `/` → `\/` (and `\` → `\\`); the codec is
  round-trip tested, including names containing `\`, `\/`, emoji, and combining
  marks.
- A remote node whose `name.ok === false` (undecryptable) is skipped with a
  warning and never deleted or moved by us.
- Windows-invalid names cannot exist locally and therefore cannot be uploaded;
  a remote node with such a name is downloaded only if a safe local name is
  configured (v1: skip + warn).

### 4.3 Type model

`file`, `dir`, `symlink`, `other`. Symlinks and special files are **skipped**
with a warning (no following, no uploading). A path whose kind changes between
file/dir is a conflict, never a silent replace.

### 4.4 Local scan contract

`LocalScanner` produces the L snapshot and the ignore verdicts:

- Entries include directories and files, sorted by relative POSIX path. The
  sync root itself is not an entry. Display paths are NFC; keys are folded
  (`NFC + lowercase`).
- Symlinks/reparse points, special files, and files modified inside
  `settleSeconds` are never in `Entries`. They are reported in `Skipped` so the
  planner cannot mistake a deliberate skip for a local deletion.
- `IsComplete` is false when enumeration or hashing failed, or when a case
  collision was found. The planner must not infer deletions from an incomplete
  snapshot, and must never delete for a path in `Skipped`.
- `IgnoreRules` is loaded once per scan from the sync's one ignore file, with
  inline `exclude` patterns appended afterwards. Matching is case-sensitive by
  default; parent-directory exclusion wins over a child negation, exactly as in
  git. `Explain` reports the deciding rule and line. The same rules are applied
  to remote-only paths by the planner, so an ignored path is neither downloaded
  nor used to infer a deletion.

---

## 5. Planner (the heart)

The planner is a **pure function**:

```
plan(base, local, remote, config) -> { actions[], conflicts[], skipped[], stats }
```

It performs no I/O, which makes the entire decision matrix unit-testable and
guarantees `plan` and `run` cannot disagree. Implementation:
`src/PdSync/Plan/Planner.cs`. The remote snapshot is built from
`ProtonCli` + `RemoteIndex` (`src/PdSync/Proton`, `src/PdSync/Remote`) and uses
paths relative to the sync root; the absolute remote root is kept only for CLI
calls. File renames are detected per §5.4; directory renames remain normal
subtree operations.

### 5.1 Decision table

| B | L | R | LΔ | RΔ | Action |
|---|---|---|---|---|---|
| – | – | – | – | – | none |
| – | yes | – | – | – | `upload.create` |
| – | – | yes | – | – | `download.create` |
| – | yes | yes | – | – | **initial adoption** (§5.3) |
| yes | yes | yes | 0 | 0 | none |
| yes | yes | yes | 1 | 0 | `upload.revision` |
| yes | yes | yes | 0 | 1 | `download.replace` |
| yes | yes | yes | 1 | 1 | **conflict** (§5.2), unless provably converged |
| yes | – | yes | – | 0 | `remote.trash` |
| yes | – | yes | – | 1 | conflict: delete vs modify → `download.restore` (remote wins), warn |
| yes | yes | – | 0 | – | `local.trash` |
| yes | yes | – | 1 | – | conflict: delete vs modify → `upload.revision` (local wins), warn |
| yes | – | – | – | – | drop base entry |
| yes | yes | yes | kind differs | | **conflict** (keep-both) |

"Provably converged" (both flags set, no conflict): the remote
`claimedDigests.sha1` equals the local SHA-1. When the remote digest is absent,
fall back to `R.claimedSize == L.size` and
`|R.claimedModificationTime − L.mtime| ≤ mtimeToleranceMs`. This catches
re-uploads and clock-skew cases without a transfer. It is an optimization only;
it never invents content.

### 5.2 Conflicts

A conflict is recorded when both sides changed incompatibly, or when a delete
races a modify, or when kinds differ.

Policy (`conflict:`) applies to both-changed files:

| Policy | Behavior |
|---|---|
| `keep-both` | Local file is renamed to `stem (conflict YYYY-MM-DD HHMMSS).ext`; remote original is downloaded to the original path; the conflict-named local file is uploaded. Both contents survive on both sides. |
| `local-wins` | Upload local as the new revision of the remote file. Remote's previous revision remains in history (Proton keeps revisions); the current remote content is overwritten. |
| `remote-wins` | Download remote over local; the replaced local file is moved to local trash first. |
| `newer-wins` **(default)** | Compare local mtime vs remote `claimedModificationTime`. If the difference is ≤ `mtimeToleranceMs`, or either timestamp is in the future by more than the tolerance, fall back to `keep-both`. Otherwise the newer side wins: upload for local, download for remote. The decision and both timestamps are written to the run report. |
| `ask` | Print a diff summary and prompt (only in a TTY). Non-interactive runs downgrade to `keep-both` and exit 2. |

`newer-wins` is a heuristic, not a truth: `claimedModificationTime` is supplied
by whichever client uploaded the revision, so clocks can disagree. The tolerance
window is the guard rail; when it cannot decide, `keep-both` costs a duplicate
but never loses data. A conflict decision is final for that run — the planner
never re-evaluates a path twice in one run.

Delete-vs-modify is always resolved **in favor of the surviving content**, with a
warning, regardless of policy: never destroy a modification because the other
side deleted a file.

`conflict: ask` never blocks a scheduled run: without a TTY it behaves as
`keep-both` and returns exit 2.

### 5.3 Initial adoption (B is absent, both sides exist)

Used on first run and after state loss. Configurable per sync
`initialSync: adopt | keep-both | local-wins | remote-wins` (default `adopt`):

- `adopt`: if both are files with equal SHA-1 (preferred) → record base,
  transfer nothing. If the digest is missing, require equal size and mtimes
  within tolerance. Otherwise fall through to `keep-both`.
- `keep-both`: conflict-rename as above.
- `local-wins` / `remote-wins`: one direction overwrites, loser goes to trash.

`pdsync rebuild --policy=...` exposes the same choice explicitly after state loss.

### 5.4 Renames

- **Remote rename** is detected exactly: a `uid` present in B disappears from its
  old path and appears at a new path. If `L` at the old path is unchanged, apply
  a local move (or, if the local file is also gone, just update state). If local
  changed → conflict.
- **Local rename** is detected when `detectRenames` is true: path P vanished and
  path Q appeared with the same SHA-1+size, Q absent in B, and
  exactly one candidate exists. If R at P is unchanged → `fs rename` + `fs move`
  (or a single move if the parent is unchanged). Ambiguous matches fall back to
  trash + upload.
- Renames are **never** inferred from mtime alone.

#### 5.4.1 Case handling: folded identity, casing is local

Decision: **do not sync case at all.** This is the cheapest correct model and
the only one Windows can represent without fighting another client.

Rules:

1. All lookups use `path_key` (case-folded). A local `name.txt` → `Name.txt`
   rename leaves `path_key` unchanged, so the planner emits **no action**. Remote
   stays `name.txt`; local stays `Name.txt`.
2. The state DB stores both `path_key` and `path`. `path` records the canonical
   remote casing (when remote exists) or the current local casing (local-only).
   It is used for display and for addressing the CLI; it is not an identity.
3. **Case collisions are errors.** If a directory scan yields two entries with
   the same `path_key`, the sync aborts before executing any action, listing the
   colliding paths and suggesting a rename. We never "pick one and delete the
   other". This applies to local and remote scans equally.
4. Directories behave like files: a case-only directory rename is a no-op and
   does not rewrite descendant keys (they are already folded).

Consequence worth spelling out: after a case-only local rename, a later content
edit must still target the existing remote node (`name.txt`). `fs upload` uses
the local basename, which would create a second remote node `Name.txt` and break
the invariant. Therefore, when local and canonical remote casing differ, the
executor uploads **from a staging entry named with the canonical casing**:

- same NTFS volume → create a hardlink to the local file with the canonical name
  in a temp directory, upload it, delete the link (no data copy);
- otherwise → copy to the temp directory, upload, delete (cost is one copy, only
  for the rare case-mismatched changed file).

The user's file is never renamed by the sync engine. The staging entry is created
inside the state directory when possible, otherwise in a `.pdsync-tmp` directory
next to the file (force-ignored and cleaned by the journal on the next run).

Known limitation: case folding is `ToLowerInvariant`, which is not full Unicode
case folding (`İ`, `ß`, final sigma). Windows compares with its own upcase table.
The collision guard uses `OrdinalIgnoreCase` on Windows on top of the folded key,
so the common cases are safe; exotic names are documented and tested, not
silently merged.

### 5.5 Execution order

Actions are emitted in a dependency-safe order:

1. create missing remote directories (top-down)
2. download (staged)
3. upload (create / new revision)
4. remote move + rename
5. local move + rename
6. local trash
7. remote trash
8. prune empty remote dirs (if enabled)
9. drop state entries / commit

Deletions run **last** so a failed transfer never precedes a destructive action.
Actions within a phase are ordered deterministically (path sort) so logs are
reproducible and `plan` output is stable.

---

## 6. Remote interaction layer

### 6.1 The adapter is the only place that spawns the CLI

`ProtonCli` owns: argument construction, path encoding, JSON parsing, exit-code
mapping, stderr classification, timeouts, retry/backoff, and version/auth
preflight. Nothing else in the codebase runs a child process.

### 6.2 Calls used

| Purpose | Command |
|---|---|
| Preflight/auth | `fs list --json /` |
| List folder | `fs list --json <path>` |
| Stat node | `fs info --json <path>` |
| Create dir | `fs create-folder <parent> <name>` |
| Upload file | `fs upload --json --file-conflict-strategy create-new-revision <file> <parent>` |
| Download file | `fs download --json --file-conflict-strategy remove <remoteFile> <stagingDir>` |
| Move | `fs move <source>... <targetParent>` |
| Rename | `fs rename <path> <newName>` |
| Trash | `fs trash <path>...` |
| Restore | `fs restore <path>...` |

Notes:

- `create-new-revision` is our upsert: S1 confirmed it creates when absent and
  adds a revision when present.
- Uploads are batched per destination parent to amortize process startup, but
  only after every path is glob-escaped. If a batch reports `failedItems > 0` or
  `No paths matched`, retry its files individually (§6.2.1).
- If the local basename casing differs from the canonical remote name, upload
  from a staging entry named with the canonical casing (hardlink on the same
  NTFS volume, copy otherwise), then delete the staging entry. Never upload the
  local basename in that case — it would create a case-colliding remote node.
- Downloads are **always per file** into a staging directory, never a recursive
  folder download, so we control replacement and verification.
- All calls are serialized. Parallelism is intentionally not used: the CLI shares
  SQLite caches and one account rate limit.
- A per-call timeout applies (default 10 min, configurable); a hung download is
  killed and retried at most twice.
- `/trash` is account-global and names repeat across runs. Restoring by
  `/trash/<name>` is ambiguous; use `/trash/<uid>` (S15) and keep the node UID in
  state so a trash action can be undone deterministically. Name-based restore is
  only used when the name is unique.
- Pass `--json` to every command. Upload JSON is only a counter:
  `{transferredItems, transferredBytes, skippedItems, failedItems, failures[]}` —
  it carries no revision or hash, so verification comes from a parent listing.

#### 6.2.1 Local paths are glob patterns

`fs upload` expands its local path arguments as glob patterns (S14). A single
unmatched path aborts the whole batch with `No paths matched`, uploading nothing.

- Escape `[` as `[[]`; escape `*` and `?` as `[*]` / `[?]` for other OSes (both
  are illegal in Windows names).
- `]`, `{`, and `}` are literal on their own; a brace-expansion pair such as
  `{a,b}` is not.
- Do not batch paths blindly: escape first, and if a batch reports a failure,
  retry per file to isolate it. Never treat a failed literal as a wildcard.
- For a brace-expansion name, upload from a glob-safe temporary hardlink/copy
  and then `fs rename` to the canonical name (S14, `fs rename` treats `newName`
  literally). Updating such a file replaces the remote node, losing that node's
  revision history; this is accepted and reported.
- A name that cannot be staged glob-safely is skipped as `unsupported-name`;
  it is never silently dropped.

### 6.3 Remote index

`RemoteIndex` builds a tree by BFS over `fs list`, keyed by path, with an
auxiliary `uid → path` map. It is cached in memory for the run.

Incremental optimization (`--fast`) is driven by **local** changes, not remote
folder metadata. S4 showed that a folder's `modificationTime` does **not** change
when a child is added or removed, so it cannot be used to decide whether a
subtree is quiescent.

Algorithm: skip listing a remote folder when every local entry in the
corresponding subtree is unchanged against the state DB (no content, add, or
delete changes), and the folder was last fully listed within
`fastFullEveryRuns` runs (default 20). The sync root is always listed. A skipped
folder produces **no actions at all** — no uploads, downloads, renames, or
deletions — so a remote-only change is deferred to the next full scan rather than
acted on with stale data.

`--full` forces a complete scan and is the default for `run` unless `fast: true`
is configured. `doctor` reports the age of the last full scan so an operator can
see how much remote reality is being deferred.

Implementation: `FastRemoteScan` builds the skip predicate from the baseline and
local scan; `RemoteIndex` synthesizes entries for a skipped folder from B instead
of listing it. `FastScanTracker` stores the number of fast runs since the last
full listing in `<stateDir>/state/<syncId>.fast.json`; a full run resets it.
`plan --fast` reads the counter without writing, so the next `run` makes the
same full/fast decision.

### 6.4 Error taxonomy

| Class | Examples | Handling |
|---|---|---|
| auth | not logged in, session expired | abort, exit 3, print `proton-drive auth login` |
| transient | timeout, 429, 5xx, network reset | retry with exponential backoff (3 attempts) |
| permanent | invalid path, quota exceeded, forbidden | fail that action, continue others, exit 1 |
| conflict | CLI reports unexpected existing node | re-plan that path next run, never loop |

Retry policy (implementation in `ProtonCli`, `ProtonRetryOptions`):

- **Attempts:** 3 total (2 retries), base delay 1 s, doubling, capped at 30 s,
  with ±20 % jitter. The whole attempt sequence runs inside the existing CLI
  gate, so one account never sees parallel retries.
- **Idempotent calls** (`fs list`, `fs info`, `--version`, `fs download`) retry
  every transient failure. Download is replayed into the staging directory only,
  so a partial first attempt cannot corrupt a local file.
- **Non-replayable calls** (`fs create-folder`, `fs upload`, `fs move`,
  `fs rename`, `fs trash`) are retried **only** when the command provably did
  not execute: a process spawn failure or a 429 rate-limit rejection. A timeout
  or 5xx on these is surfaced immediately; the next run reconciles from state
  instead of risking a duplicate revision, folder, or trash operation.
- **Auth failures are never retried** (`exit 3` stays `ProtonAuthException` and
  exit code 3). Permanent failures are not retried either. A mid-run auth
  failure aborts the run: the failing CLI call is not retried, no later action
  runs, and no state is committed for the failed action. Already-committed
  actions remain. If auth expires after a transfer but before its verification
  or staged rename, a remote revision or a `pdsync-upload-<id>` node may already
  exist; the next run reconciles it. Recovery is `proton-drive auth login`
  followed by a rerun, which re-plans the failed work from snapshots. S8
  coverage is deterministic fake-CLI (`AuthExpiryTests`,
  `Category=AuthExpiry`), not a live expiry probe.
- Per-item `failedItems > 0` in upload/download JSON is treated as a permanent
  action failure; the executor's per-file retry path handles those.

The classification is exposed as `ProtonCliException.Kind`
(`Auth`/`Transient`/`Permanent`) plus `Attempts`, so callers and tests can
reason about it without re-parsing stderr.

The CLI's `--json` output and exit codes are parsed; if the CLI emits
non-JSON noise on stdout, it is captured as a run-log artifact and classified as
a permanent failure with the raw text in the error report.

### 6.5 Verification

`fs list` and `fs info` expose `activeRevision.claimedDigests.sha1`, the plaintext
SHA-1 supplied by the uploading client. It matches a locally computed SHA-1
exactly (S7, S14) and is emitted as `sha1Verified: false`: a claim, not a
server-attested digest. We use it as a content fingerprint and verification
signal, not as a security boundary.

| Direction | Verified by | Cost |
|---|---|---|
| Upload | local SHA-1 == remote `claimedDigests.sha1` | one `fs list --json <parent>` per affected parent, after transfer |
| Download | local size == remote `claimedSize`; AEAD decryption succeeded; local SHA-1 == digest from the remote listing when present | no extra call |

Implementation: `Verifier` performs one parent listing per upload, compares size
first, then the claimed SHA-1 when present. `sample` downloads back only a
deterministic percentage of uploads whose digest is missing and whose size is at
least `minSizeMb`; `full` downloads back every upload. A missing digest
downgrades to size rather than failing the transfer.

Modes (`verify.mode`):

- **`hash` (default)** — compare SHA-1 claims. One parent listing verifies every
  file uploaded into that parent. `fs info` costs ~4 s per call (S5), so it is
  used only for spot checks, never per file.
- **`size`** — `claimedSize` plus a changed `activeRevision.uid`. Use for very
  large trees or when digests are missing.
- **`sample`** — download back a deterministic percentage of uploads (files ≥
  `minSizeMb`) **only when the remote digest is absent**, hash, compare, delete.
- **`full`** — audit mode (`pdsync verify --sync <name>`): download every upload
  and compare. Never routine.

A failed verification leaves the local file untouched, marks the entry
`unverified`, and retries the upload next run. A missing digest on a file we just
uploaded is a verification failure: our CLI always supplies one.

### 6.6 Desktop-app mapping detection

The Proton Drive desktop app can map and sync local folders independently of
pdsync. Running both engines over the same tree is unsupported. `doctor` reads
the desktop app's `Mappings.json` **read-only** (when present) and compares each
mapping's `Local.RootFolderPath` with every configured sync `local` root.

- Windows probes `%LOCALAPPDATA%\Proton\Proton Drive\Mappings.json`, then the
  equivalent `%APPDATA%` and `%PROGRAMDATA%` locations and two older layouts.
  The macOS desktop client exists but its mapping-file path and schema have not
  been verified, so non-Windows detection is a documented best-effort no-op
  rather than a guessed probe.
- Parsing is defensive: a missing file means "no mappings"; malformed JSON, an
  unreadable file, or an unrecognized shape yields no mappings plus a non-fatal
  `warn` diagnostic. An array whose entries all lack a usable
  `Local.RootFolderPath` is schema drift, not "no mappings": it is reported as a
  warning. Entries that are individually skipped produce a partial-schema
  warning while the usable mappings are still checked. The probe itself never
  crashes and only reads files.
- `doctor` as a whole is not write-free: its existing state-DB check may still
  open or migrate an existing database exactly as before (§7). The mapping probe
  does not touch the state DB.
- Overlap is classified with pdsync's identity rules (NFC + case-folded,
  segment-aware): exact, local root inside a mapping, or mapping inside a local
  root. Sibling names sharing a string prefix do not overlap. Symlink, junction,
  and 8.3 short-name aliases are not resolved, so an aliased path can be a false
  negative.
- Every distinct overlapping mapping is a doctor problem (exit 1) reported as
  `sync <name> desktop-app overlap` with the mapping path; duplicate mappings
  are de-duplicated by identity. JSON output also carries a `desktopMappings`
  array of detected mapping roots. The additive per-check `warning` field is
  emitted only when true.

---

## 7. State and crash safety

SQLite via `Microsoft.Data.Sqlite` at `<stateDir>/state/<syncId>.sqlite`.

```sql
syncs(id TEXT PRIMARY KEY, name, local_root, remote_root, created_at, schema_ver)
entries(sync_id, path_key, path, kind,
        size, mtime_ms, sha1,
        remote_uid, remote_rev_uid, remote_size, remote_mtime_ms, remote_sha1,
        local_present INTEGER, remote_present INTEGER,
        frozen INTEGER DEFAULT 0, last_synced_at)
pending(id INTEGER PK, sync_id, op, path, payload_json, started_at)
runs(id INTEGER PK, sync_id, started_at, finished_at, status, stats_json)
```

Rules:

- The DB is the **only** source of truth for "what was synced last time".
- `plan` opens the DB read-only. If it does not exist yet, `plan` uses an
  in-memory empty baseline; it never creates, migrates, or backs up a file.
  Schema migration happens only in `run`/`doctor`, after copying the DB to
  `state/backup-<date>.sqlite`.
- Every destructive or transfer action is preceded by a `pending` row and
  followed by a state update in the same transaction where possible.
- On startup, `pending` rows are reconciled against current L/R (idempotent
  re-check) before planning: adopt if the action clearly landed, retry if not.
- A run is committed only after all actions succeed (or are explicitly skipped);
  conflicts still commit the successful subset.
- `pdsync status` surfaces uncommitted `pending` rows from a crashed run.
- Backups: on schema migration, copy the DB to `state/backup-<date>.sqlite`.
- Deleting the state DB is safe but triggers `initialSync` adoption for every
  path; `doctor` warns loudly if the DB is missing while both sides are non-empty.

Local trash layout (default `<stateDir>/trash/<sync>/<runId>/<relpath>`), with a
JSON manifest per run. Retention is **forever** by default: nothing is pruned
automatically, and a later `pdsync trash purge` (M5) will be explicit and
interactive. Remote trash is Proton Trash; `doctor` notes that Proton's own
retention policy applies there and may eventually remove the recoverable copy.

### 7.1 Run logs (NFR-10)

Every executing `run` writes one JSONL file:

```
<logDir>/<sync-name>/<runId:D8>-<yyyyMMddTHHmmssfffZ>.jsonl
```

The sync-name directory is sanitized for filesystem-invalid characters and
Windows reserved names. The timestamp suffix keeps files distinct if the state
DB is rebuilt and run ids restart; an additional `-N` suffix resolves the rare
same-millisecond collision. Each line is a self-contained JSON object with
`schemaVersion: 1` and an `event` field:

| Event | Fields |
|---|---|
| `run_start` | `sync`, `syncId`, `runId`, `startedAt`, `fast`, `runsSinceFull`, `planned` (the planner stats object) |
| `action` | `sync`, `runId`, `kind`, `path`, `pathKey`, `secondaryPath`, `outcome` (`ok`/`failed`/`skipped`), `durationMs`, `error` |
| `verification` | `sync`, `runId`, `kind`, `path`, `method` (`size`, `size+sha1`, optionally `+download-back`), `result` (`ok`/`failed`), `downloadBack`, `detail` |
| `run_finish` | `sync`, `runId`, `status` (`ok`/`conflict`/`partial`/`failed`), `finishedAt`, `durationMs`, `succeeded`, `failed`, `skipped`, `prunedRemoteDirectories`, `error` |

`kind` is the `PlanActionKind` name (PascalCase, matching `plan --json`) or
`Adopt` for state adoptions. A verification event follows the action that
verified it; a failed verification is also reflected in the action's `error` and
`verification.result` is `failed`. Counts exclude skipped actions from
`succeeded`/`failed` and report them separately in `skipped`. The
`runs.stats_json` column records the same counts plus `logFile`; on a failed run
it also records `error`, and any actions completed before the failure are still
counted.

`plan` never creates a log file or the log directory, and it does not emit a run
summary. Log writing is best-effort: if `logDir` is not writable, the run prints
a warning to stderr and proceeds; diagnostics are never written to stdout, so
`run --json` remains parseable. A process kill or power loss can leave a
truncated log without `run_finish`; readers must tolerate that.

`logDir` must not be the same as or inside any sync's local root, analogous to
`stateDir`; otherwise the next run would scan and upload its own logs. Logs are
retained indefinitely in v1; there is no automatic pruning, rotation, or size
cap. Operators manage retention themselves, and a future `history` command will
read these files.

---

## 8. Safety rules

Non-negotiable, enforced before any destructive action:

1. Never delete/trash remotely unless `onLocalDelete: remote-trash` and the
   entry exists in B with `L` gone and `RΔ = 0`.
2. Never delete a local file except into the local trash; never `unlink` a
   synced file directly.
3. **Deletion budget**: abort the run before executing deletions if
   `maxDeletes` or `maxDeletePercent` would be exceeded, unless `--force`.
   Prevents a bad ignore pattern or a detached drive from wiping a tree.
4. Refuse to run if the local root is missing, empty-but-previously-non-empty, or
   not a directory. "Empty" is a hard stop when B has entries, not a silent mass
   delete.
5. Refuse to run if the remote root disappeared (trashed/renamed externally).
6. Lock file in `stateDir` prevents overlapping runs; stale locks include the PID
   and host and are reported, never auto-broken silently.
7. `--dry-run`/`plan` performs no writes, no folder creation, no lock changes.
8. Files modified within `settleSeconds` are skipped this run (partial writes,
   editors, downloads); they are picked up next run.
9. Overlap with a Proton Drive desktop-app mapping is detected in `doctor` and
   reported as unsupported: read `Mappings.json` read-only if present, classify
   exact/ancestor/descendant overlap, and never fail on a missing or malformed
   file (§6.6).
10. Deleting the last copy is impossible by construction: every delete has a
    trash counterpart on the other side, and delete-vs-modify keeps content.
11. **Case collisions never delete.** If the remote contains two names that
    collapse to one Windows path, one is kept and the other gets a conflict
    suffix; neither is trashed as a side effect.
12. **Fast mode never plans actions in a skipped subtree.** A skipped folder
    yields no uploads, downloads, renames, or deletions; deletions additionally
    require a full listing of the containing folder in the current run (§6.3).
13. A verification failure after upload leaves the local file in place, marks the
    entry `unverified` in the state DB, and reports it; the next run retries the
    upload rather than assuming success.
14. A failed local-path glob match is isolated and reported; it is never retried
    as a wildcard and never silently drops the rest of a batch.
15. SHA-1 claims are compared, never trusted for security. A missing digest
    downgrades verification to size; it never blocks a transfer.

---

## 9. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-1 | A no-op run on a 10k-file tree with warm state completes without any upload/download calls and without writing state (beyond a run record). |
| NFR-2 | `plan` and `run` produce identical action lists for the same inputs. |
| NFR-3 | Every destructive action is journaled before execution and recoverable from the log. |
| NFR-4 | Zero data loss for the covered matrix; property tests assert "content on at least one side survives every planner branch". |
| NFR-5 | All paths handled as Unicode NFC; Windows path length ≥ 260 supported via `\\?\` where needed. |
| NFR-6 | A killed process (Ctrl-C, power loss) leaves the DB consistent and the next run safe. |
| NFR-7 | Secrets never appear in config or logs; auth remains entirely inside the CLI. |
| NFR-8 | Exit codes and `--json` are stable contract; human output may change. |
| NFR-9 | CLI version is checked at startup; unsupported versions fail with a clear message. |
| NFR-10 | Runs emit a machine-readable action log (JSONL, §7.1) under `logDir` and a human summary with counts and durations; `plan` emits no JSONL log and no run summary. |
| NFR-11 | Case is never synced: a local case-only rename produces no remote action, and no run creates a second remote entry differing only by case. |
| NFR-14 | Case-fold collisions abort the affected sync before any write; both colliding paths appear in the report. |
| NFR-15 | Upload verification uses one parent listing per folder, not one `fs info` per file. |
| NFR-16 | Local path arguments are glob-escaped; a single unmatched path never aborts a batch silently. |
| NFR-17 | A remote SHA-1 is used when present and never blocks a transfer when absent. |
| NFR-12 | `--fast` never changes the deletion decision set compared to a full scan; it may only delay creations/updates. |
| NFR-13 | `newer-wins` never resolves a conflict when the mtime delta is inside the tolerance window; it falls back to `keep-both`. |

---

## 10. Architecture and repo layout

```
pdsync.sln
src/PdSync/
  Program.cs                    # entry point, exit-code mapping
  Cli/
    CliApp.cs                   # plan/run/doctor/status parsing + reporters
  Diagnostics/
    DesktopMappingDetector.cs   # read-only desktop-app Mappings.json probe + overlap
  Config/
    ConfigModel.cs              # YAML DTOs + normalized model
    ConfigLoader.cs             # discovery, YAML parse, unknown-key rejection
    ConfigValidator.cs          # defaults, invariants, overlap checks
    PathUtil.cs                 # env/~/absolute paths, comparisons
    PathKey.cs                  # NFC + case-folded identity
  Proton/
    ProtonCli.cs                # process spawn, JSON, timeouts, write operations
    RemotePath.cs               # codec, escaping, validation
    RemoteSnapshot.cs            # R snapshot, uid/path lookups
  Scan/
    LocalScanner.cs             # walk, stat, settle check, symlink policy
    IgnoreRules.cs              # .pdignore + inline patterns, Explain()
    ContentHash.cs              # SHA-1 + size, hash cache
    LocalPathGlob.cs            # escape paths for the CLI's glob expansion
  Remote/
    RemoteIndex.cs              # BFS listing + fast-mode baseline synthesis
    FastRemoteScan.cs           # subtree predicate + full-scan counter
  Plan/
    PlanModel.cs                # Action/Conflict/Skip/Stats records
    Planner.cs                  # pure decision engine
  Exec/
    SyncService.cs              # prepare snapshots + plan/run orchestration
    Executor.cs                 # phase ordering, action application, pending journal
    RunLogger.cs                # NFR-10 JSONL run/action/verification log
    Verifier.cs                 # upload/download size + SHA-1 checks
    SafetyGuard.cs              # deletion budget + empty-tree guard
    LocalTrashStore.cs          # trash moves + manifests
    RunLock.cs                  # per-sync lock
    FileSystemUtil.cs           # cross-volume move fallback
  State/
    StateStore.cs               # SQLite, migrations, transactions

tests/PdSync.Tests/             # xUnit: planner matrix, ignore, codec, config
tests/PdSync.IntegrationTests/  # FakeProtonCli scenarios
tests/PdSync.LiveTests/         # opt-in, real account, scratch folder
tools/FakeProtonCli/            # scriptable stub used by integration tests
tools/live-smoke.ps1            # real-account scratch-folder smoke test
```

Deep-module boundaries: `ProtonCli` hides all process/JSON/path-encoding pain;
`Planner` hides all policy and is pure; `Executor` hides crash safety and
ordering. Tests target those three seams, not the CLI itself.

### 10.1 Toolchain

| Concern | Choice |
|---|---|
| SDK / TFM | .NET 8 (`net8.0`), C# 12 |
| Packaging | `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`; no AOT/trimming in v1 (native SQLite + reflective YAML make it fragile) |
| CLI/UI | Spectre.Console.Cli + Spectre.Console (tables, colors, prompts); `--json` still emits plain JSON |
| YAML | YamlDotNet, duplicate-key checking on, unmatched properties are errors |
| SQLite | Microsoft.Data.Sqlite (raw SQL, no ORM) |
| Hashing | `System.Security.Cryptography.SHA1.HashData` + file size |
| Ignore | MAB.DotIgnore 3.0.2 wrapped by our own `IgnoreRules` (case policy lives in the wrapper) |
| JSON | System.Text.Json with `PropertyNameCaseInsensitive = true` |
| Process I/O | `System.Diagnostics.Process` with async reads, linked `CancellationToken` timeout |
| Tests | xUnit + FluentAssertions; FsCheck optional for planner properties |
| Long paths | .NET Core long-path support; `\\?\` only where a Win32 call needs it |

Windows-first: `OperatingSystem.IsWindows()` gates case-folding collision checks and
hardlink staging. Linux/macOS builds use the same folded identity, so state is
portable but a Linux tree may legitimately contain a case collision that Windows
cannot represent — that is the documented hard error.

---

## 11. Testing strategy

1. **Planner truth table** — parameterized over B/L/R existence × LΔ × RΔ × kind
   × policy, including delete-vs-modify and initial adoption. Property: no branch
   yields "delete both".
2. **Ignore parity suite** — a corpus of gitignore cases run against
   MAB.DotIgnore, with our pre/post-processing and case policy, asserting
   identical verdicts.
3. **Path codec round-trip** — names with `/`, `\`, `\/`, `?`, `*`, emoji,
   combining marks, trailing spaces/dots (Windows), 255-byte names.
4. **Fake CLI integration** — `tools/FakeProtonCli`: stateful, scenario-driven,
   emits the recorded JSON shapes, mirrors SHA-1/skip/folder-mtime/case behavior,
   and injects auth expiry, timeouts, failures, and truncated downloads. Tests
   point `proton.cli` at its apphost, set `PDSYNC_FAKE_STATE`, seed a tree, and
   assert state plus `invocations.jsonl`. Smoke: `tools/FakeProtonCli/smoke.ps1`.
   S8 auth expiry is covered automatically (no live session) by
   `AuthExpiryTests`: exit 3, one attempt, no later action, and no state commit
   for the failed action; a post-transfer failure may leave a remote revision or
   staged node that the next run reconciles.
5. **Live smoke (opt-in)** — scratch remote folder `/my-files/pdsync-test`:
   create/modify/rename/delete both sides, conflict, ignore negation/anchoring, empty
   folders, large file, unicode names; then clean up.
6. **Performance** — 10k-file synthetic tree: scan time, no-op run, list-call
   count.
7. **Case-folding suite** — `Name.txt`/`name.txt`, `İ`/`i`, `ß`/`SS`,
   `Straße`/`STRASSE`, NFC vs NFD on macOS, case-only file and directory
   renames, and the staging-upload path with a mocked hardlink failure.
8. **Toolchain smoke** — published single-file exe runs on a machine without
   the .NET runtime; `dotnet test` covers unit + integration; live tests are
   skipped unless `PDSYNC_LIVE=1`.
9. **Glob-escaping suite** — `brackets[1].txt`, `{a,b}.txt`, `*`/`?` on Linux,
   brace fallback via temp upload + `fs rename`, and a batch where one path fails
   to match (must isolate, not drop the batch).
10. **SHA-1 suite** — digest present, digest absent, digest mismatch, convergence
    without transfer, adoption by hash, and verification via one parent listing.

---

## 12. Spikes (must pass before the engine is written)

| ID | Question | Why it matters |
|---|---|---|
| S1 | Does `fs upload --file-conflict-strategy create-new-revision` create a new file when none exists? What does `--json` return? | Determines the upsert primitive and verification data. |
| S2 | Does upload preserve local mtime into `claimedModificationTime`? What is `claimedSize` vs `storageSize`? | Drives adoption, `newer-wins`, and "provably converged". |
| S3 | What is "same content automatically skipped" based on (size, mtime, hash)? | Avoids fighting the CLI's own skip logic. |
| S4 | Does a folder's `modificationTime` change when a child is added/removed? | Decides whether `--fast` subtree skipping is sound. |
| S5 | Per-call latency for `list`, `info`, `upload`, `download` on a warm cache; behavior on 429. | Sizing, batching, timeout defaults. |
| S6 | `fs download` single-file semantics into a staging dir; does it delete the destination before writing? | Staging/atomic-swap design. |
| S7 | Remote path escaping for names containing `\` and `/`; UID fallback behavior. | Codec correctness. |
| S8 | Auth expiry mid-run: error shape and whether the CLI refreshes silently. | Retry/abort logic and exit 3. |
| S9 | Does `fs trash` accept multiple paths, and is the trashed node's path usable for restore? | Deletion batching and undo instructions. |
| S10 | Folder upload merge vs `create-folder` failures when the folder already exists. | Ancestor creation logic. |
| S11 | With `name.txt` already remote, does uploading a local `Name.txt` create a second node? Does `fs list` show both, proving the backend is case-sensitive? | Confirms why D14/D16 need staging uploads and a collision guard. |
| S12 | How accurate is `claimedModificationTime` after an upload (same second? timezone? sub-second?) and does it match the local mtime? | `newer-wins` and `adopt` both depend on this. |
| S13 | Does downloading a file into a folder that already has a case-colliding name overwrite, rename, or fail? Does upload from a hardlinked staging file behave identically to a normal file? | Case policy (§5.4.1) and staging uploads (§6.2). |
| S14 | How does the CLI treat local paths containing `[`, `]`, `{`, `}`, `*`, `?`? Does upload `--json` return per-file data? | Local-path glob escaping (§6.2.1) and verification strategy (§6.5). |
| S15 | Can `fs restore /trash/<uid>` disambiguate same-named trashed nodes? | Deterministic undo for deletions (§6.2). Open; verify in M1. |

**Results (2026-10-31):** `docs/spikes.md`. S1–S7 and S9–S14 answered; S15
open. S8's live probe is deferred (deliberately expiring the login would break
the rest of the suite), but pdsync's exit-3/abort/no-retry/no-commit contract is
covered by deterministic fake-CLI tests (`AuthExpiryTests`); see docs §7. Headline corrections to the design: plaintext SHA-1 **is** available,
folder mtime does **not** propagate, local paths are glob-expanded, and each CLI
call costs ~2–4 s.

---

## 13. Milestones

| M | Scope | Exit criteria |
|---|---|---|
| M0 | Spikes S1–S14 | **Done 2026-10-31**: `docs/spikes.md`; S8 live probe deferred but fake-CLI auth-expiry coverage added, no blocking unknowns |
| M1 | Config **(done)**, state DB **(done)**, local scan + ignore **(done)**, pure planner + remote index **(done)**, FakeProtonCli planner tests **(done)**, `doctor`, `plan` CLI **(done, live-smoked)**; restore-by-UID check (S15) | `plan` on a real tree lists correct creates; zero writes |
| M2 | Push path: create dirs, upload creates/revisions, verify, state commit **(done, live-smoked)** | Push-only sync converges; second run is a no-op |
| M3 | Pull path: staged download, local trash, remote index **(done)**, executor **(done, live-smoked)** | Pull converges; interrupted download never corrupts a local file |
| M4 | Two-way: deletions both ways, conflicts, renames, adoption, budgets; planner truth table **(done)**; executor/pending journal **(done, live-smoked)** | Truth-table suite green; live smoke passes; exit codes correct |
| M5 | Polish: `status`, `history`, `rebuild`, JSON report, scheduler docs, published exe | Task Scheduler recipe works unattended for 7 days |

**Live smoke 2026-10-31:** `tools/live-smoke.ps1` passed against the real
account with a unique `/my-files/pdsync-live-smoke-*` scratch root. It covered
initial push, no-op convergence, local edit/revision, remote-origin pull, remote
delete → local trash, local delete → Proton Trash, ignored files, and fast/full
mode. The script cleans up by trashing and permanently deleting its scratch root
unless `-KeepRemote` is passed.

v1 ships at M4; M5 is the "leave it running" bar.

---

## 14. Resolved decisions (2026-10-31)

| Question | Decision | Where it landed |
|---|---|---|
| Implementation stack | C# on .NET 8, self-contained single-file publish | D4, §10.1 |
| Default conflict policy | `newer-wins`, `keep-both` fallback inside the tolerance window | D10, §5.2 |
| Local trash retention | forever, manual purge later | D11, §7 |
| Ignore file scope | one `.pdignore` per sync root, no global file, no nested files in v1 | D12, §3.3 |
| `--fast` | allowed, approximate, with periodic full scans and a hard no-deletions rule | D13, §6.3 |
| Case handling | case-folded identity, casing never synced, collisions abort | D14/D16, §5.4.1 |
| Verification depth | remote plaintext SHA-1 (fallback size), sampled download-back for missing digests | D15, §6.5 |

Still open (can be decided during M0/M4, not blocking design):

1. Conflict-name format — `(conflict YYYY-MM-DD HHMMSS)` proposed; Proton may
   dislike parentheses in some clients, so a spike can confirm.
2. `samplePercent` default for `verify.mode: sample` (5% proposed).
3. Whether `doctor` should offer to register the Windows Task Scheduler job, or
   just print the `schtasks` command.

---

## 15. Reference: verified CLI facts

From `proton-drive --version` and `--help` on 2026-10-31:

- `Proton Drive CLI cli-drive@0.9.0+75612299`, SDK `js@0.22.2+75612299`
- `filesystem upload [-f STRATEGY] [-d STRATEGY] [-t] localPath... parentPath`
  - file strategies: `create-new-revision`, `rename`, `replace`, `skip`
  - folder strategies: `merge`, `rename`, `replace`, `skip`
- `filesystem download [-f STRATEGY] [-d STRATEGY] path... localFolder`
  - file strategies: `rename`, `remove`, `skip`
  - folder strategies: `merge`, `rename`, `remove`, `skip`
- `filesystem list [-t TYPE] path`, `filesystem info path`,
  `filesystem create-folder parentPath name`, `filesystem trash path...`
- `filesystem move sourcePath... targetParentPath`, `filesystem rename path newName`
- Remote paths are POSIX; `/` in a node name is escaped `\/`.
- `--json` is a global flag; `--json` list output includes `uid`, `parentUid`,
  `name: {ok, value}`, `type`, `creationTime`, `modificationTime`,
  `totalStorageSize`, `activeRevision {uid, storageSize, claimedSize,
  claimedModificationTime, claimedDigests {sha1, sha1Verified}}`.
- `claimedDigests.sha1` is the **plaintext SHA-1** supplied by the uploading
  client and matches a locally computed SHA-1; `sha1Verified` is `false`.
- Upload `--json` returns only counters: `{transferredItems, transferredBytes,
  skippedItems, failedItems, failures[]}`.
- Local path arguments are **glob-expanded**. `[` must be escaped as `[[]`;
  `{a,b}` triggers brace expansion and a single unmatched path aborts the batch.
- A folder's `modificationTime` does **not** change when a child is added or
  removed.
- Measured latency on a warm cache (S5): `list` 1.7–3.4 s, `info` ~4.2 s,
  `upload` small file ~4.0 s, `download` small file ~4.4 s. A 100-folder scan is
  therefore minutes, not seconds.
- `fs download --file-conflict-strategy remove` replaces a case-colliding local
  file and adopts the remote casing; `skip` leaves it untouched.
- Hardlinked staging files upload normally (S13); `fs rename` treats `newName`
  literally, including braces.
- Session/cache live in `%LOCALAPPDATA%\proton-drive-cli`
  (`cache-entities.sqlite`, `cache-crypto.sqlite`, `Data/events.json`).
