# Phase 7 — Session Hot Memory

## Delivered

`SessionHotMemoryCoordinator` now exposes a provider-neutral `IHotMemoryService` for
compact session memory. It accepts only a typed `HotMemoryState` containing current
goal, active entities, recent decisions, open questions, and working facts.

The state is bounded by explicit `HotMemoryPolicy` values:

- maximum TTL, total entries, entries per kind, entry characters, and estimated tokens;
- deterministic decay by importance and half-life, with stable key tie-breaking;
- one current goal at most;
- promotion eligibility based on kind, decayed score, and explicit policy.

Structured state is redacted entry by entry and stored in a versioned opaque payload
inside the existing `SessionHotMemory` record. Query context renders that payload as
compact cited text; legacy unstructured hot records remain readable.

Updates use the existing command boundary: exact-scope authorization, ingress
redaction, idempotency receipts, conditional writes, transactional outbox, and
atomic commit. Promotion currently supports eligible `WorkingFact` entries only and
calls `IMemoryCommandService.RememberAsync`; it never writes a durable record
directly. Repeated promotion uses the normal remember receipt and returns a replay.

The former command monolith is physically decomposed under `src/AgMemory.Core/Commands`:
`MemoryCommandService`, `CommandPreflight`, `CommandValueSupport`, `CommandOutbox`,
and `HotMemoryStateCommandHandler`. Existing public command interfaces and the
pre-receipt embedding order remain unchanged.

## Validation

Focused Core validation:

```text
dotnet test tests/AgMemory.Core.Tests/AgMemory.Core.Tests.csproj --no-restore
Passed: 31, Failed: 0
```

Coverage includes command authorization/replay characterization, decay, deterministic
bounded selection, compact-content rejection, TTL, exact-scope denial, expiry,
redaction, structured context rendering, promotion eligibility, and promotion replay.

## Phase 8 handoff

Phase 8 can model decision traces as `RecentDecision` hot entries for short-lived
context, then add a dedicated compact durable decision command. It should not promote
decision entries through the Phase 7 working-fact path.
