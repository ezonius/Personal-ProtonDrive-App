---
name: pdsync-security
description: Use for a security-focused review of pdsync changes — trust boundaries, path traversal, symlink/junction escapes, destructive delete/trash behavior, state DB integrity, CLI argument injection, secrets, and untrusted remote names. Guidance mode by default; full audit only when explicitly requested.
---

# pdsync security

This role is a defensive, source-first security overlay for pdsync. It is
read-only by default: do not edit tracked files, commit, push, or merge unless a
separate implementation task explicitly authorizes it.

Load the global security skill first:

```text
/skill:security-audit
```

Use its **guidance mode** for focused review and triage. Run its complete
six-phase audit workflow only when the user explicitly asks for a full audit or
pen-test. `AGENTS.md` and `SPEC.md` remain the canonical project contracts;
resolve them from the repository root (three levels above this skill directory).

## Scope

pdsync is a local↔remote sync engine built on the official `proton-drive` CLI.
The security-relevant assets are:

- local files under the sync root,
- the remote Proton Drive tree,
- the SQLite state DB and pending journal,
- local trash and remote Proton Trash,
- config/YAML and `.pdignore`,
- logs, reports, and CLI output,
- the authenticated Proton CLI session.

The engine is not a network client and does not implement Proton cryptography.
Do not report "missing TLS" or "no encryption" as a pdsync finding; the CLI/SDK
owns those boundaries.

## Threat model to check

- **Remote name/path handling:** empty names, `/`, `.`/`..`, control characters,
  backslashes, Unicode normalization, case collisions, names that collapse to
  the same local path, and undecryptable/UID fallback paths.
- **Local path handling:** relative paths, drive roots, UNC paths, `..`,
  symlinks, junctions, 8.3 aliases, cross-volume moves, and `stateDir` inside a
  sync root.
- **CLI expansion:** `[`, `*`, `?`, and `{a,b}` in names; ensure staging or
  escaping prevents the CLI from acting on a different path.
- **Destructive operations:** remote deletes must use Proton Trash; local
  deletes must use the local trash store; deletion budgets must stop before any
  deletion; empty-tree/root-disappeared guards must fail closed.
- **State integrity:** pending journal written before the CLI call; state commit
  and journal removal in one transaction; read-only plan mode cannot migrate or
  back up the DB; crash recovery reconciles safely.
- **Verification:** download staging before replace; size/SHA-1 checks; SHA-1 is
  a fingerprint, not a security boundary; missing digest falls back safely.
- **Secrets:** CLI stderr/stdout, JSONL logs, human reports, and error messages
  must not leak tokens, session data, or private paths beyond what the user
  explicitly requested.
- **Config/ignore parsing:** strict unknown/duplicate key rejection; malformed
  ignore patterns do not silently widen scope; environment expansion cannot
  escape the intended root.
- **Concurrency:** one account, serialized live CLI calls; the run lock prevents
  overlapping writers; stale locks are reported, not silently broken.
- **Desktop-app overlap:** mapping detection is best-effort; false negatives and
  alias limitations must be documented, not presented as guarantees.

## Method

1. Map the changed trust boundary and the principal/resource/outcome involved.
2. Read the source path end-to-end, including callers and failure handling.
3. Prefer source evidence and existing fake-CLI tests. Do not run destructive
   live operations against the real Proton account.
4. If a local proof is needed, use the fake CLI and a scratch directory. Keep
   any build/test execution inside the project's normal test environment.
5. Separate confirmed findings from hypotheses. A candidate without a concrete
   affected principal, resource, or security outcome is `needs_validation`, not
   a confirmed finding.

## Escalation

Security work often discovers follow-up tasks or policy choices that are larger
than the PR under review. Use `pdsync-escalation` instead of silently widening
the change.

- Confirmed but out-of-scope security work becomes a `SECURITY_TASK` issue.
- A missing trust-boundary or policy decision becomes a `DESIGN_DECISION` issue
  labeled `decision`; do not choose the policy yourself.
- A blocking security question must go to the orchestrator/user before the
  affected task continues.
- A security finding that is reachable through the current PR can still be a
  `BLOCKING` review finding; say why the PR makes it reachable.

Include an `Escalations` section in `worker_done` even when empty.

## Deliverable

Post findings to the PR review or return them to the coordinator with:

- severity: `BLOCKING`, `IMPORTANT`, `MINOR`, or `NIT`,
- affected asset and trust boundary,
- `file:line` and concrete evidence,
- safe reproduction or test,
- smallest effective fix,
- residual risk and whether the issue is pre-existing.

Do not edit code as part of the security review. If the coordinator asks for a
fix, create a separate implementation task with explicit ownership. Report
follow-up security tasks and unresolved design decisions to the coordinator;
do not leave them only in the review prose.
