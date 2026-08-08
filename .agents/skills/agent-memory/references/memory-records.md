# Memory record templates

## Retrieval checklist

Before creating a record, search relevant project knowledge and answer:

1. Is this already recorded?
2. Is it reusable by a later task or agent?
3. Is it stable or clearly labeled as an observation?
4. What source proves it?

## Fact

```markdown
# Fact: <short statement>

- Scope: <project/component>
- Source: <code, test, document, or issue>
- Fact: <stable technical information>
- Consequence: <why a future agent needs it>
```

## Constraint

```markdown
# Constraint: <short statement>

- Scope: <project/component>
- Source: <owner/document/contract>
- Requirement or limitation: …
- Consequence: <what work must or must not do>
```

## Decision

```markdown
# Decision: <short name>

- Status: proposed | approved | superseded by <record>
- Owner: …
- Date/task: …
- Problem: …
- Context: …
- Options: <brief alternatives>
- Decision: …
- Rationale: …
- Consequences: …
- Sources: …
```

## Observation

```markdown
# Observation: <short statement>

- Scope: …
- Source: …
- Observation: …
- Follow-up: <confirm, monitor, or convert to a fact/decision>
```

## Failed attempt

```markdown
# Failed attempt: <short name>

- Scope: …
- Attempt: …
- Result: …
- Cause: …
- Preferred alternative: …
- Evidence: …
```

## Outcome

```markdown
# Outcome: <decision or change>

- Linked decision/change: …
- Measurement or observation: …
- Result: …
- Consequence / next action: …
- Evidence: …
```

## Handoff message

```markdown
## <Task request | assignment | review | result | blocked>
- From: …
- To: …
- Objective: …
- Scope and constraints: …
- Relevant records: …
- Expected result: …
- Owner / status: …
```

Use `blocked` only with the affected area, concrete blocker, required action, and next decision owner.

## Source-of-truth matrix

| Question | Primary source |
|---|---|
| What does the system do? | Code and tests |
| What was agreed or why? | Decision record / approved documentation |
| What configuration is active? | Versioned configuration / deployment state |
| What work is planned or tracked? | Issue tracker / plan |
| What should a future agent avoid repeating? | Failed-attempt or outcome record |
