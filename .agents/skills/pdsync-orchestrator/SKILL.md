---
name: pdsync-orchestrator
description: Use when coordinating pdsync work through Orca orchestration — choosing a GitHub issue, creating a Run and Task DAG, dispatching implementation and independent review workers, enforcing review/fix gates, and merging a reviewed PR. Do not use for a single direct code edit or when the user did not ask to supervise.
---

# pdsync orchestrator

This is the coordinator role for pdsync. It adds coordination workflow only.
`AGENTS.md` remains the canonical implementation contract and `SPEC.md` the
source of truth. Resolve both from the repository root (three levels above this
skill directory) and follow them when they are stricter than this skill.

Before running Orca commands, load the version-matched orchestration guide:

```powershell
orca skills get orchestration --json
```

## Outcome

One explicit outcome per Task, an independent review verdict, a merged PR, and
a post-merge verification of `master`. The final report names each Task, its
worker, its evidence, and any residual risk.

## Preflight

1. Confirm the repo is clean and current:
   ```powershell
   git status --short
   git fetch origin --prune
   git pull --ff-only origin master
   ```
2. Inspect the candidate issues and pick by safety value first, then user value,
   then effort. For pdsync, safety-critical work wins: destructive operations,
   path identity, state/trash, auth, and dual-sync overlap.
3. Read the issue, `AGENTS.md`, and the relevant `SPEC.md` sections. Define
   observable acceptance before dispatching anyone.
4. Avoid starting a second writer on a branch or file area that is already in
   flight. Worktrees isolate checkouts, not merge conflicts.

## Create the Run

```powershell
orca orchestration run-create --objective "<one sentence>" --json
orca orchestration run-current --json
```

One Run has one coordinator. Do not create a second coordinator for the same
Run. For independent workstreams, create separate Runs instead.

## Default Task DAG

Use this shape unless the issue is trivial:

1. **Implement** — one writer, one worktree, opens a PR. Does not merge.
2. **Review** — independent read-only reviewer, separate worktree at PR head.
3. **Fix** — original implementer fixes accepted review findings and pushes to
   the same branch.
4. **Final re-review** — original reviewer rechecks the new head.
5. **Merge** — coordinator merges only after APPROVE and green verification.

Create tasks with dependencies:

```powershell
orca orchestration task-create --spec $spec --task-title "Implement ..." --json
orca orchestration task-create --spec $spec --task-title "Review ..." `
  --deps '["<implement-task-id>"]' --json
orca orchestration task-create --spec $spec --task-title "Fix ..." `
  --deps '["<review-task-id>"]' --json
orca orchestration task-create --spec $spec --task-title "Final re-review ..." `
  --deps '["<fix-task-id>"]' --json
```

## Task spec contract

Every spec must be self-contained:

```text
TARGET:        files, component, environment, issue/PR links
CHANGE:        concrete result to produce
CONSTRAINTS:   invariants, compatibility rules, do-not-touch boundaries
OWNERSHIP:     which worktree/branch/files this worker may edit
ACCEPTANCE:    observable behavior that proves completion
VERIFY:        exact build/test/live-smoke commands
DELIVERABLE:   PR, commit, review comment, or report
REPORT:        what worker_done must contain
ESCALATIONS:   report out-of-scope tasks, SPEC gaps, design decisions, and security items; do not implement them
```

Include the issue body and acceptance criteria when they are not obvious.
Never rely on the worker discovering the assignment from the Run objective.
Every task spec must require the worker to follow `pdsync-escalation` and return
an `Escalations` section when it finds something outside scope.

## Worker topology

Start an implementation worker on a fresh child worktree:

```powershell
orca orchestration worker-start --task <task-id> `
  --worktree new-child --repo name:Personal-ProtonDrive-App `
  --name <descriptive-name> --base-branch origin/master `
  --agent pi --setup skip --json
```

Start the reviewer only after the PR exists, from the PR head:

```powershell
orca orchestration worker-start --task <review-task-id> `
  --worktree new-child --repo name:Personal-ProtonDrive-App `
  --name review-<name> --base-branch origin/task/<branch> `
  --agent pi --setup skip --json
```

Reuse a settled terminal for a follow-up Dispatch instead of paying for a new
context:

```powershell
orca orchestration worker-start --task <fix-task-id> `
  --terminal <handle> --worktree name:<worktree> --json
```

Reviewers must stay read-only. They may update their own review worktree to the
PR head, but must not edit the implementation branch.

## Supervision loop

```powershell
orca orchestration check --wait `
  --types "worker_done,escalation,question" `
  --timeout-ms 900000 --json
```

For each delivery:

1. Read the message. If it is a question, reply with
   `orca orchestration reply --id <id> --body "<answer>" --json`.
2. Validate `worker_done` against the expected Task/Dispatch and acceptance
   criteria. Do not accept prose-only failure.
3. Triage every escalation before deciding the next owner. Create accepted
   follow-up issues; surface blocking design decisions to the user.
4. Decide the settled terminal's next owner: reuse, retain, or release.
5. Acknowledge the batch:
   ```powershell
   orca orchestration check --ack <delivery-id> --json
   ```
6. Continue until every expected Task settles.

If `worker-start` reports `turnStart: unsupported`, check progress with
`worker-show`, `worker-read`, or the terminal. Do not relaunch blindly.

## Escalation and backlog

The coordinator owns the path from a worker discovery to a durable artifact.
Use `pdsync-escalation` for the exact classification and payload.

- **Potential task:** deduplicate with `gh issue list --search`, then create a
  GitHub issue immediately with evidence, impact, and acceptance criteria.
  Label it `enhancement` or `documentation`.
- **Design decision or decision-bearing SPEC gap:** create a
  `Decision: <title>` issue labeled `decision`. Do not choose for the user.
- **Blocking decision:** stop the affected line of work and ask the user before
  continuing. Use `orca orchestration gate-create` when the Task itself must be
  formally blocked.
- **Non-blocking decision:** continue the current PR, but list the decision
  issue in the final report and do not start dependent work.
- **Security task:** create an issue and route it through the security review
  role; do not smuggle a hardening change into the current PR.

After the user decides, update the relevant `SPEC.md` section (or §14), link the
issue, and close it. A decision that exists only in chat is not resolved.

## Gates

- **Review gate:** reviewer must post a severity-ranked PR review with a verdict.
  `REQUEST CHANGES` is not mergeable.
- **Fix gate:** implementer must triage every finding. Rejecting a finding is
  allowed only with a concrete reason, recorded on the PR.
- **Final review gate:** reviewer rechecks the new head SHA, not the old one.
  `APPROVE` plus `MERGEABLE` plus green build/tests is required.
- **Merge gate:** use the `pdsync-merger` skill. Do not merge from this skill's
  supervision loop without a final APPROVE.

## Concurrency

- 1–3 active implementation writers is the safe default.
- 3–5 read-only reviewers can run in parallel.
- 3–5 active workers is comfortable for one coordinator; 6–8 only when tasks
  are truly independent and messages are low-volume.
- Split into separate Runs when a workstream has its own coordinator, branch, or
  merge owner. Do not run two coordinators on one integration line.
- Never run live Proton smoke tests in parallel; the CLI is single-account and
  serialized.

## Final report

Report per Task: outcome, evidence, worker/dispatch, PR/commit, review verdict,
and residual risk. Then report the merge commit, post-merge build/test result,
issue state, and worker cleanup decision.

Also report:

- new GitHub issues created from worker escalations, with links;
- design decisions requested from the user, with links and blocking status;
- SPEC gaps deferred to a follow-up task;
- security items handed to `pdsync-security`.
