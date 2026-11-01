---
name: pdsync-merger
description: Use when a reviewed pdsync PR is ready to merge — verify the final review verdict, unchanged head SHA, mergeability, issue linkage, and CI-equivalent build/tests, merge with the repo's merge-commit convention, then verify master and clean up. Do not use to bypass a failed or missing review.
---

# pdsync merger

This is the final gate before `master`. The merger is not the implementer and
does not fix code. `AGENTS.md` and `SPEC.md` remain canonical; resolve them from
the repository root (three levels above this skill directory).

## Preconditions

All must be true:

- Final reviewer verdict is `APPROVE`.
- No unresolved `BLOCKING` or `IMPORTANT` findings.
- PR head SHA equals the SHA the final reviewer approved.
- PR is `OPEN`, base is `master`, and `mergeable` is `MERGEABLE`.
- PR body links the issue (`Closes #N`) and summarizes the change.
- Build is clean: 0 warnings, 0 errors.
- All tests pass on the reviewed head.
- The working tree is clean; no unrelated local changes are part of the merge.
- If the change touches sync/executor/CLI behavior, live smoke has passed or the
  PR explicitly justifies why it is not needed.
- No live Proton CLI operation is running in parallel.

If any precondition fails, stop and report. Do not merge "temporarily" and fix
later.

## Verify before merging

```powershell
gh pr view <number> --json state,mergeable,headRefOid,body,baseRefName
git fetch origin --prune
git rev-parse origin/<branch>
git diff origin/master...origin/<branch> --check
dotnet build pdsync.sln -v quiet --nologo
dotnet test tests/PdSync.Tests/PdSync.Tests.csproj --no-build --nologo `
  --logger "console;verbosity=minimal"
```

Re-run the tests on the exact head being merged. A green result from an earlier
SHA is not sufficient if the branch moved.

## Merge

This repo uses merge commits:

```powershell
gh pr merge <number> --merge --delete-branch
```

Do not use `--admin` unless the user explicitly authorizes it. Do not squash or
rebase unless the repository convention changes and the user asks for it.

If the merge command reports that it could not remove a worktree or local branch,
do not force anything. The PR may still have merged; verify remote state:

```powershell
gh pr view <number> --json state,mergedAt,mergeCommit
git ls-remote --heads origin <branch>
```

## Post-merge verification

```powershell
git pull --ff-only origin master
git log --oneline -3
gh issue view <issue-number> --json state,closedAt
dotnet build pdsync.sln -v quiet --nologo
dotnet test tests/PdSync.Tests/PdSync.Tests.csproj --no-build --nologo `
  --logger "console;verbosity=minimal"
```

Confirm:

- the merge commit is on `master`,
- the linked issue is closed (or close it explicitly),
- the remote feature branch is gone,
- `master` builds with 0 warnings/errors and all tests pass.

## Escalations at merge

If the final review, merge preconditions, or post-merge verification reveals a
new task, SPEC gap, or design decision, use `pdsync-escalation` and route it to
the orchestrator/user.

- Do not expand the merge to include an out-of-scope fix.
- Do not merge if the unresolved decision is required to make the change safe.
- Create the follow-up issue before closing the Run so it cannot be lost.
- Include the issue link in the final report.

## Failure handling

- **PR not mergeable:** stop, report the conflict/state, and send it back to the
  implementation owner. Do not resolve conflicts in the merger role.
- **Head changed after review:** stop; require a new final review of the new SHA.
- **Post-merge build/test failure:** do not hide it. Report immediately; create a
  revert PR or a forward-fix task with explicit ownership. Do not rewrite
  `master` history.
- **Worker cleanup:** release or retain each settled worker terminal and record
  the decision. Do not leave a worker ambiguous.

## Final report

Report the PR, reviewed SHA, merge commit, issue state, post-merge build/test
counts, branch deletion, and worker/worktree cleanup decision. Include any
residual risk from the final review. Also list follow-up issues created and any
decision still waiting on the user.
