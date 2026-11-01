---
name: pdsync-escalation
description: Use when pdsync work reveals a potential new task, a SPEC gap, an uncovered design decision, or a security hardening item. Classify and package it for the orchestrator, who creates GitHub issues and routes decisions to the user instead of silently expanding the current PR.
---

# pdsync escalation

This is the shared escalation protocol for pdsync workers and the orchestrator.
It prevents two failure modes:

1. useful work is discovered but lost in chat, and
2. a worker invents a design decision that `SPEC.md` does not actually cover.

`AGENTS.md` and `SPEC.md` remain canonical. Resolve them from the repository
root (three levels above this skill directory).

## Rule

Do not silently expand the current task. Do not invent a contract, choose a
user-facing design option, or edit `SPEC.md` to match an implementation guess.

If you find something outside the current task:

- package it,
- classify it,
- report it to the orchestrator,
- continue only if it is genuinely non-blocking.

## Classification

- **`POTENTIAL_TASK`** — concrete follow-up work with evidence and acceptance
  criteria. Not required for the current PR.
- **`SPEC_GAP`** — `SPEC.md` is missing, contradictory, or ambiguous on behavior
  the current work touches.
- **`DESIGN_DECISION`** — multiple valid options; the choice affects users,
  safety, compatibility, or scope. The user/owner must decide.
- **`SECURITY_TASK`** — security hardening or a confirmed security issue outside
  the current PR.
- **`QUESTION`** — a clarification that blocks the current task. Use
  `orca orchestration ask` rather than guessing.

## Worker deliverable

Include this block in the review, `worker_done`, or coordinator message:

```text
ESCALATION
kind: POTENTIAL_TASK | SPEC_GAP | DESIGN_DECISION | SECURITY_TASK | QUESTION
title: <short, issue-ready title>
source: <task/PR/commit/file:line>
evidence: <what was observed and why it matters>
impact: <who/what is affected; current workaround>
blocking: yes | no
options: <for DESIGN_DECISION; otherwise "n/a">
recommendation: <preferred option and why, or "none">
acceptance: <observable completion criteria for a follow-up task>
references: <links, SPEC sections, issue numbers>
```

For a non-blocking escalation, include it under an `Escalations` heading in
`worker_done`. Do not bury it in a long summary.

For a blocking question, ask the coordinator and stop that line of work:

```powershell
orca orchestration ask --question "<question>" `
  --options "<option A>,<option B>" --timeout-ms 600000 --json
```

Resume the same message ID after a timeout; do not create a duplicate question.

## Orchestrator triage

The orchestrator owns escalation triage.

1. Deduplicate before creating anything:
   ```powershell
   gh issue list --state open --search "<distinctive terms>"
   ```
2. For every accepted `POTENTIAL_TASK`, create a GitHub issue immediately:
   ```powershell
   gh issue create --title "<title>" --body-file <temp-file> `
     --label enhancement
   ```
   Include source, evidence, impact, acceptance criteria, and non-goals.
   Use `documentation` instead of `enhancement` for docs-only work.
3. For every `DESIGN_DECISION` or decision-bearing `SPEC_GAP`, create a
   decision issue:
   ```powershell
   gh issue create --title "Decision: <title>" --body-file <temp-file> `
     --label decision
   ```
   Include the options, trade-offs, recommendation, affected SPEC sections, and
   what happens if no decision is made. Do not choose for the user.
4. If the decision blocks the current task, stop and surface it to the user
   before continuing. Use `orca orchestration gate-create` when a Task must be
   formally blocked:
   ```powershell
   orca orchestration gate-create --task <task-id> `
     --question "<question>" --options '["A","B"]' --json
   ```
5. If the decision is non-blocking, continue the current PR, but list the
   decision issue in the final report and do not start dependent work until it
   is resolved.
6. After the user decides, record the outcome in the relevant `SPEC.md` section
   (or §14 for resolved decisions), link the issue, and close it. Do not leave a
   resolved decision only in chat.

## Never

- create a duplicate issue without searching first;
- implement an out-of-scope task inside the current PR;
- silently choose a design option because it is convenient;
- change `SPEC.md` to match code without an explicit decision;
- create an issue with no evidence or acceptance criteria;
- leave a discovered task or decision only in a worker transcript.
