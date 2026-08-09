---
name: agmemory
description: "Use the local AgMemory MCP tools as an independent durable-memory layer. Invoke for every material task that creates, changes, validates, or connects a durable artifact, and persist exactly one compact Outcome before handoff; also use to recall relevant saved context or diagnose availability."
---

# AgMemory

Use `memory_recall`, `memory_remember`, and `memory_status` only when they add durable context. They operate in a fixed local store and exact scope; never send or infer storage paths, actor IDs, scope values, or durable IDs.

## Required independent lifecycle

Treat AgMemory as a durable runtime-memory layer, separate from any repository files or `agent-memory` skill. Do not wait for, infer from, or substitute a file-based memory update.

For every material task that creates, changes, validates, or connects a durable artifact:

1. At the start, make one focused `memory_recall` only if saved context may materially affect the task.
2. Before the final handoff, call `memory_remember` exactly once with a compact `Outcome`, even when another memory layer was updated.
3. If the write is unavailable or fails, say so in the handoff; never imply that the outcome was saved.

A material task includes an implementation, bug fix, investigation with a verified result, test or configuration change, migration, review with actionable findings, or integration. Exclude greetings, discussion-only requests, temporary experiments, and work with no durable result.

## Choose the smallest action

| Situation | Action |
| --- | --- |
| Greeting, self-contained question, or source files/tests already answer it | No MCP call. |
| Saved project context may materially affect a task | One focused `memory_recall`. |
| User asks whether agents share or can reach memory | One `memory_status`. |
| Material work created or validated a durable artifact | One compact `Outcome` at the end. |
| A personal note or conversation yields a lasting preference, agreement, follow-up, or insight | One tagged compact memory. |

If a memory tool is unavailable, say so briefly. Do not guess configuration, edit it automatically, or repeat a failed call. A new Codex conversation may be required after MCP configuration changes.

## Recall efficiently

1. Query with 2–5 discriminative terms, not a full question or task transcript.
2. Set `limit: 3`; use `5` only for a genuinely broad task. Never exceed `10`.
3. Make at most one recall per task. A second is justified only after the task changes to a materially different subproblem.
4. Read only relevant hits. Treat them as hints and verify claims against code, tests, configuration, or approved documentation.
5. If there are no hits, continue without retries or fabricated context.

Example: `memory_recall({ query: "MCP local scope", limit: 3 })`

## Record the required outcome

Before handing off a material task, call `memory_remember` exactly once. Use `memoryType: "Outcome"` and state only: what changed, the most useful relative path or artifact name, and one validation result. Target 60 words or fewer. This is the required evidence that AgMemory received a useful record.

Example: `memory_remember({ content: "Outcome: Added local memory-status diagnostics in src/AgMemory.Web/Features/MemoryStatus; Web tests passed.", memoryType: "Outcome" })`

Skip this write only for greetings, discussion-only work, temporary experiments, or a task with no durable result. Do not write more than one artifact record per task, and do not run recall first merely to decide whether to write.

For a durable reusable fact or constraint not represented by the outcome, use one atomic `Fact` or `Constraint` instead. Keep the default importance and confidence unless there is a clear reason to change them. Omit `entities` unless 1–3 stable labels improve future lookup.

Allowed types include `Fact`, `Constraint`, `Preference`, `Task`, `Procedure`, `Observation`, `Outcome`, `Incident`, `LessonLearned`, `Summary`, and `Event`. Do **not** use `Decision`: the local MCP cannot supply its required structured decision trace.

Never store secrets, credentials, raw conversation or command logs, private reasoning, user data, temporary progress, guesses, or code that is better kept in the repository. Name a test or check, but do not paste its output. Exact duplicate writes are idempotent.

Example: `memory_remember({ content: "Constraint: AgMemory MCP scope is fixed by its process environment and is never supplied to tools.", memoryType: "Constraint" })`

## Save personal notes and conversation conclusions

Treat `entities` as compact tags. Save a personal note only when it is a durable preference, goal, boundary, or reusable insight. Use `memoryType: "Preference"` or `"Observation"` with `entities: ["personal-note"]`.

Example: `memory_remember({ content: "Personal note: Prefer concise Russian status updates with links to changed files.", memoryType: "Preference", entities: ["personal-note"] })`

After meaningful communication, save only a concise agreement, decision outcome, follow-up, or insight—not the transcript. Use `memoryType: "Outcome"`, `"Task"`, or `"Observation"` with `entities: ["conversation"]`; add at most one stable topic tag when it improves retrieval.

Example: `memory_remember({ content: "Conversation outcome: Verify AgMemory through one tagged artifact after each material task.", memoryType: "Outcome", entities: ["conversation", "memory-policy"] })`

Do not save casual chat, quotes, personal contact details, third-party information, sensitive health/financial/legal information, or anything the user asks not to retain. Keep each note under 60 words and write no more than one personal or conversation memory for one distinct lasting point.

## Diagnose explicitly

Call `memory_status({})` only for an explicit health/synchronization request, or once at the start of a multi-agent session. It returns availability and the active non-expired count for the fixed scope. Compare that count with the AgMemory `/memory-status` page only when both integrations are known to use the same storage path and exact scope.

Do not make `memory_status` a routine startup step or run a status → remember → recall sequence unless the user explicitly requests an integration smoke test.
