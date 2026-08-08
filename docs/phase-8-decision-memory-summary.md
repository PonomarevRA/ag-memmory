# Phase 8 — Decision Memory

## Delivered

`DecisionDetails` now resides in `AgMemory.Contracts/DecisionMemory.cs` together
with a narrow `RecordDecisionCommand` and `IDecisionMemoryService`. The trace has
exactly the reusable decision fields:

- Problem, Context, Options;
- Decision and Reason;
- Consequences and optional Outcome.

There is no field for chain-of-thought, transcript, conversation, or raw log.

`DecisionMemoryService` validates a compact trace and delegates persistence to the
existing `IMemoryCommandService.RememberAsync` path as a `MemoryRecordType.Decision`.
It does not access the store. Therefore exact-scope authorization, ingress
redaction, canonicalization, idempotency receipts, conditional write, and outbox
commit remain the one existing command workflow.

`DecisionTraceValidator` requires non-blank trace fields, at least one option,
one-line/control-character-free values, bounded list counts and bounded total
content. The decision record stores a compact searchable decision/problem summary,
its reason, and the structured details. This makes it retrievable and citeable
without persisting a deliberation transcript.

## Validation

Focused Core validation:

```text
dotnet test tests/AgMemory.Core.Tests/AgMemory.Core.Tests.csproj --no-restore
Passed: 36, Failed: 0
```

Added tests cover:

- trace redaction, durable decision type, and exact replay;
- incomplete and transcript-shaped input rejection before ingress;
- exact-scope denial with no redaction or transaction;
- decision retrieval and context citation;
- public contract reflection proving that no reasoning/transcript payload exists.

## Phase 10 handoff

Phase 10 can expose `IDecisionMemoryService.RecordDecisionAsync` as the shared
implementation behind .NET client and `memory_record_decision` transport adapters.
Those adapters must retain the supplied envelope and must not construct broader
scopes or bypass this facade.
