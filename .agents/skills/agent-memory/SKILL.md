---
name: agent-memory
description: "Maintain compact, reusable project knowledge across tasks and agents. Use proactively at the start and close of nontrivial repository work to retrieve or record architectural decisions, constraints, confirmed behavior, failed approaches, outcomes, and structured handoffs without storing private reasoning or duplicating source documentation."
---

# Agent memory

Treat memory as a compact knowledge layer. It preserves why a decision was made and prevents repeated investigation; it never replaces source code, tests, official documentation, configuration, or issue tracking.

Read [memory-records.md](references/memory-records.md) before creating a durable record or a structured multi-agent handoff.

## Proactive lifecycle

Use this skill automatically for a nontrivial repository task: an implementation, bug investigation, code review, design decision, migration, or test change that depends on project context. Do not wait for the user to name the skill.

At the start, bootstrap the shared copy if needed, retrieve only the knowledge relevant to the task, and identify applicable constraints or failed attempts. Before closing, consider whether the work produced a reusable decision, outcome, constraint, or failed attempt; record it only when it meets the rules below.

Skip the lifecycle for greetings, general questions, a single reversible edit with no project context, or work outside a repository. Never create a durable record merely because a task ran.

## Bootstrap the shared project skill

On the first applicable use inside a Git repository, run:

```bash
python3 scripts/bootstrap_project_skill.py
```

The bootstrap creates the same portable `agent-memory` skill only in missing project paths:

| Tool | Project path |
|---|---|
| Codex and Rider | `.agents/skills/agent-memory/` |
| Claude Code | `.claude/skills/agent-memory/` |
| Cursor | `.cursor/skills/agent-memory/` |

It never overwrites an existing project copy. If a path already exists with a different version, report the conflict and ask the project owner how to reconcile it. After a successful first bootstrap, include the created folders in the repository so every developer receives the same skill.

Do not bootstrap outside a Git repository. Do not rerun the bootstrap after all three paths exist.

## Retrieve before investigating

1. Understand the current task and scope.
2. Inspect the current task context and project conventions.
3. Retrieve only relevant existing decisions, constraints, and failed attempts, in this order: current context → recent relevant records → decisions → constraints → similar work.
4. Verify the record against the authoritative source when it describes code, configuration, or an external contract.
5. Do not load all memory or repeat an investigation already captured by an active, applicable record.

## Decide whether to record knowledge

Record something only if all of these are true:

- It will help future work or another agent.
- Repeating the work would waste meaningful time or risk a repeat mistake.
- It is stable enough, or its uncertainty is stated.
- A search found no existing equivalent record to extend or supersede.

Do not record raw conversations, command/debug logs, generated output, trivial actions, temporary thoughts, unverified assumptions, or private reasoning.

## Use the smallest record type

| Need | Record |
|---|---|
| Stable technical truth | Fact |
| Requirement or limitation | Constraint |
| Accepted approach and rationale | Decision |
| Useful discovery awaiting confirmation | Observation |
| Approach that failed and its replacement | Failed attempt |
| Measured or observed result of a decision | Outcome |

Keep an observation below 200 tokens. Aim for 500–1200 tokens for a decision record. Keep one topic per record; avoid broad project summaries and append-only history files.

## Write durable records

1. Use the project's established location and format for decisions or knowledge.
2. If no convention exists, ask before creating a new permanent memory location; do not invent a global store for project-private knowledge.
3. Attribute the source and date or task when it improves traceability.
4. Link or name the primary source of truth, such as a code path, test, document, or issue.
5. When a decision changes, create a new record that explicitly supersedes the old one. Do not rewrite accepted history silently.
6. Before closing material work, consider whether a decision, outcome, failed attempt, or task result merits one record. Omit it if it lacks future value.

## Collaborate through structured context

For multi-agent work, share only the goal, relevant records, constraints, expected output, and owner. Use a short status update only for meaningful progress, decision, result, or blocker.

Keep each task's goal, owner, participants, state (`planned`, `active`, `blocked`, `completed`, `cancelled`), important decisions, and final outcome explicit. Never mix personal, confidential, or unrelated-project knowledge across scopes.

## Guardrails

- Never store chain-of-thought, hidden analysis, or internal deliberation. Capture a concise decision rationale instead.
- Do not override an approved decision informally. Propose and record a superseding decision when facts change.
- A memory record explains context and intent; code and tests explain current behavior.
- Optimize for future usefulness per token, not completeness.
