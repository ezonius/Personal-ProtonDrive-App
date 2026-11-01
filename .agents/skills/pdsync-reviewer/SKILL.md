---
name: pdsync-reviewer
description: Use for an independent, read-only review of a pdsync issue or PR — verify acceptance criteria, SPEC invariants, safety rules, test quality, and documentation accuracy, then post severity-ranked findings and an explicit verdict. Do not use when the task is to implement or fix code.
---

# pdsync reviewer

This is the independent reviewer role. It is read-only by default: do not edit
tracked files, commit, push, force-push, or merge. You may write a temporary
review body and update your own review worktree to the PR head.

`AGENTS.md` is the canonical repo contract and `SPEC.md` is the source of
truth. Resolve both from the repository root (three levels above this skill
directory). Use them as the checklist source; this skill only adds the review
workflow.

## Inputs

- GitHub issue and PR, including the full diff against the base branch.
- The reviewed head SHA. Record it before starting.
- Existing CI/checks if any. If none, say so explicitly.
- For security-sensitive changes, also load `security-audit` in guidance mode.

## Setup

Review from a separate worktree at the PR head:

```powershell
git fetch origin
git checkout <branch>
git reset --hard origin/<branch>
git rev-parse HEAD
git status --porcelain
```

The implementation branch is not yours. Do not edit it, even to "quickly fix"
a finding.

## Review method

1. Read the issue and restate its acceptance criteria to yourself.
2. Read the full diff against `origin/master`; do not review only the summary.
3. Trace the changed behavior through callers and state transitions.
4. Run the required verification on the reviewed head:
   ```powershell
   dotnet build pdsync.sln -v quiet --nologo
   dotnet test tests/PdSync.Tests/PdSync.Tests.csproj --no-build --nologo `
     --logger "console;verbosity=minimal"
   ```
5. Try to break the change with edge cases, not just confirm the happy path.
6. Check documentation claims against the code.

## pdsync checklist

Check the invariants that apply to the change. Do not mechanically list all of
them when only a few are relevant.

- **Plan purity:** `plan` is read-only; no state DB create/migrate/backup, no
  fast counter write, no run lock, no log creation, no file changes.
- **Deletion safety:** remote deletes go to Proton Trash; local deletes caused
  by remote deletes go to local trash with a manifest; never bare unlink.
- **Crash safety:** one action = one pending journal row; state commit and
  pending-row removal share a transaction.
- **Path identity:** NFC + case-folded identity, case never synced, case
  collisions abort before writes, remote names are path-safe.
- **Ignore semantics:** both directions, parent-directory cascade, ignored
  paths never infer deletions, frozen paths handled.
- **Fast mode:** skipped subtrees produce no actions; full scan cadence honored;
  `plan --fast` does not update the counter.
- **Deletion budgets:** exceeded budget stops before deletion; `--force` is the
  only bypass.
- **Verification:** staged downloads, size/SHA-1 checks, digest treated as a
  fingerprint rather than a security boundary.
- **CLI discipline:** only `ProtonCli` spawns the CLI; calls serialized; retry
  classification matches idempotency.
- **Config strictness:** unknown keys, duplicate YAML keys, invalid enums/ranges,
  overlapping roots, and stateDir-inside-root are errors.
- **Doctored claims:** `doctor` output and SPEC statements match actual behavior.
- **Logging:** no secrets/tokens in human or JSONL logs; log paths and failure
  modes are safe.
- **Tests:** behavior assertions, not snapshots; failure paths and edge cases
  covered; no machine-specific paths; fake CLI used where appropriate.
- **Docs:** `SPEC.md` and `AGENTS.md` are updated when a contract changes.

## Out-of-scope findings

Do not expand the PR to fix unrelated problems. If you find something outside
the reviewed change, follow `pdsync-escalation` and classify it as a
`POTENTIAL_TASK`, `SPEC_GAP`, `DESIGN_DECISION`, or `SECURITY_TASK`.

- A non-blocking follow-up does not become a `BLOCKING` finding. Report it as
  an escalation in the review and `worker_done`.
- If the PR silently chose behavior that `SPEC.md` does not cover, report a
  `DESIGN_DECISION` or `SPEC_GAP`. Do not approve an invented contract merely
  because the code is internally consistent.
- A security issue outside the PR scope goes to `pdsync-security`; it may still
  block the PR if the change makes it reachable.
- Include an `Escalations` section even when empty, so the coordinator can see
  that the review considered scope boundaries.

## Severity and evidence

Every finding must include:

- severity: `BLOCKING`, `IMPORTANT`, `MINOR`, or `NIT`,
- `file:line`,
- concrete evidence or a minimal reproduction,
- the smallest effective fix,
- whether the issue is newly introduced or pre-existing.

Rules:

- A finding without a concrete failure mode is not `BLOCKING`.
- A pre-existing issue may be reported, but scope it explicitly and say whether
  this PR worsens it.
- Do not accept "tests pass" without running them yourself.
- Do not approve a change whose documentation claims are false.
- If the head SHA changes after your review, re-review the new head.

## Deliverable

Post a comment review on the PR:

```powershell
gh pr review <number> --comment --body-file <temp-file>
```

End with exactly one verdict: `APPROVE` or `REQUEST CHANGES`.

`worker_done` must report the verdict, reviewed head SHA, PR comment URL, exact
build/test counts, top findings, escalations, and residual risk. Do not modify
code.
