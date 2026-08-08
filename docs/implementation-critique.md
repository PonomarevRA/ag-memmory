# Critic card — implementation architecture and paused Phase 6 delta

> **Reviewer:** `phase3_storage_abstraction` acting as critic  
> **Scope:** `docs/implementation-architecture.md`, current `MemoryServices.cs`, retrieval/contracts and Core tests  
> **Status:** review complete — developer must resolve the gates below before resuming Phase 6 as an extraction task.  
> **Reviewed:** 2026-08-08

## Verdict

**Do not resume the paused Phase-6 delta as a behavior-preserving file split.** It currently contains a public Contract addition and a different RRF deduplication rule, neither protected by the characterization tests required by the blueprint. A pure physical split may resume only after those edits are isolated/reverted and the baseline tests below are added. Each intentional behavioral change then needs a separately owned, approved delta.

## Non-negotiable invariants

- Authorization precedes every port call; exact validator-issued selectors are never widened.
- Redaction succeeds before a command can persist, enqueue, or emit safe metadata.
- A successful command stages its mutation, receipt, and content-free outbox message in one transaction and commits once.
- Normal retrieval/context excludes foreign, inactive, and expired memory; RRF remains deterministic.
- Context stays within its documented budget semantics and citations identify only selected evidence.

## Issues requiring a decision

### C-01 — the paused delta is not a no-semantic-delta extraction

- **Evidence:** the blueprint forbids public-contract changes without a separate Contract task ([architecture:5–9](implementation-architecture.md#архитектура-реализации-memory-platform--фазы-612)); its extraction map requires copied algorithms in the extraction PR ([architecture:156–175](implementation-architecture.md#memoryservicescs-extraction-map)). The current worktree adds `QueryVector` to the public `MemorySearchRequest` and `MemoryContextRequest` (`src/AgMemory.Contracts/Commands.cs:115–145`) and changes `DeterministicRetrieval.Fuse` from returning every ID to collapsing records by `DeduplicationKey` (`src/AgMemory.Core/DeterministicRetrieval.cs:30–45`).
- **Risk:** the public request shape and ranked/context candidate count can change while a PR is labelled as a safe physical move. The current RRF test covers different deduplication keys only (`tests/AgMemory.Core.Tests/CoreQueryTests.cs:53–69`).
- **Owner:** architecture + Contracts/retrieval owner.
- **Required clarification:** are caller-provided query vectors and cross-ID deduplication approved Phase-6 semantics, with a version/compatibility decision, or are they future work?
- **Simplest safe option:** remove these two deltas from the extraction branch; add each later in its own Contract/retrieval change with focused tests. If either is accepted now, reclassify the task as a semantic delta before coding.

### C-02 — idempotency-before-embedding remains untestable and must not be “fixed” during extraction

- **Evidence:** `RememberAsync` calls `IEmbeddingProvider.CreateAsync` before `BeginTransactionAsync` and `FindReceiptAsync` (`src/AgMemory.Core/MemoryServices.cs:80–97`). The blueprint accurately identifies this tension ([architecture:210–223](implementation-architecture.md#required-critic-decision-before-order-changes)), but the current embedding test asserts only first-write/mismatch call counts, not an embedding-required replay (`tests/AgMemory.Core.Tests/CoreCommandTests.cs:67–90`).
- **Risk:** moving the receipt lookup before embedding changes transaction duration, retry cost, and concurrent same-key behavior. Leaving the order undocumented makes a later “cleanup” indistinguishable from an accidental behavior change.
- **Owner:** command-workflow owner; architecture owner approves any order change.
- **Required clarification:** should the present behavior be the extraction baseline, or should receipt-before-embedding be a separate semantic change with a defined transaction/concurrency model?
- **Simplest safe option:** preserve the current order for the split and add a characterization test: two identical `EmbeddingMode.Required` calls create one durable row/outbox row and invoke the embedding provider twice. Do not change order until a separate approved delta supplies the required transaction semantics.

### C-03 — optional vector/graph behavior and provider failures have no defined contract

- **Evidence:** the paused public DTO can carry `QueryVector` (`Commands.cs:115–145`), but `SearchAsync` passes `null` to `SearchPortRequest` (`MemoryServices.cs:490–495`) and `BuildContextAsync` discards `MemoryContextRequest.QueryVector` when it constructs `MemorySearchRequest` (`MemoryServices.cs:533–535`). At the same time, `_vector.SearchAsync` and `_graph.RerankAsync` are mandatory constructor dependencies/calls (`MemoryServices.cs:450–465`, `493–508`); `Task.WhenAll` and graph calls have no provider-exception policy. The blueprint calls graph reranking optional ([architecture:227–237](implementation-architecture.md#retrieval-and-context-ownership--phase-6)).
- **Risk:** a real adapter cannot receive a caller vector through either public path; a null-vector adapter, lexical/vector fault, or graph fault can unexpectedly alter availability or leak an implementation exception instead of a typed outcome.
- **Owner:** retrieval/Contracts owner, with adapter owner confirming no-op behavior.
- **Required clarification:** for absent vectors and individual provider/graph failures, should Core skip the optional source, use a registered no-op implementation, return `DependencyFailure`, or preserve exception propagation? How are vector reference, vector length, and embedding contract validated together?
- **Simplest safe option:** for a behavior-preserving split, continue invoking both current ports with the current inputs and preserve current failure propagation; use composition-root no-op implementations where a capability is intentionally absent. Move vector forwarding/validation and any graceful-degradation policy to one explicit Phase-6 semantic delta with tests.

### C-04 — the document overstates the current context token guarantee

- **Evidence:** `BuildContextAsync` admits records by their stored `EstimatedTokenCost` and reports that sum (`MemoryServices.cs:557–569`), then renders extra `[memory-id]` prefixes and newlines. It emits no citations when `RequireCitations` is false. `ToSearchHit` separately estimates hot content (`MemoryServices.cs:597–603`). The blueprint states the rendered builder “never exceeds `TokenBudget`” ([architecture:239–243](implementation-architecture.md#retrieval-and-context-ownership--phase-6)), while the only context test checks a single Summary’s stored cost/citation (`CoreQueryTests.cs:72–88`).
- **Risk:** an extraction may claim a strict rendered-token bound while preserving only an aggregate record-cost bound; adding citation/header accounting silently would be a Token Economy policy change.
- **Owner:** retrieval owner now; token-policy owner owns any new accounting semantics.
- **Required clarification:** does `TokenBudget` constrain the sum of selected `EstimatedTokenCost` values (current behavior) or the fully rendered context including IDs, separators, and citations? Is empty citation output when `RequireCitations == false` intended?
- **Simplest safe option:** state the current invariant precisely as `EstimatedTokenCost <= TokenBudget`, preserve it in the split, and characterize multi-hit skip, hot-first ordering, citation toggle, and fallback. Treat rendered-token accounting as a separate Phase-11 policy/version change.

### C-05 — required characterization gates are listed but not yet present

- **Evidence:** the blueprint requires tests before moves ([architecture:285–295](implementation-architecture.md#test-and-review-sequence-for-a-developer)) and enumerates retrieval cases ([architecture:245–249](implementation-architecture.md#retrieval-and-context-ownership--phase-6)). Current Core coverage comprises six command and six query tests. It lacks: embedding-required replay call timing; same-dedup-key cross-ID fusion; public query-vector forwarding (including the context path); vector/lexical/graph fault behavior; graph result filtering of foreign/non-fused IDs; hot-before-durable packing with a budget skip; and citation-toggle/output-cost characterization.
- **Risk:** a compile-green split can regress authorization call count, side-effect timing, candidate ordering, or cited context without any test failure.
- **Owner:** Phase-6 developer, reviewed by critic before merge.
- **Required clarification:** which cases are intended to preserve present behavior, and which have an approved semantic-delta owner? No ambiguous case should be left to a file-extraction PR.
- **Simplest safe option:** add the listed characterization tests first, naming the observed behavior rather than the desired future behavior. Then move one command or retrieval component at a time; run Core/Contracts after each move. Run adapter tests whenever `SearchPortRequest` behavior changes.

## Human-ready comment policy for the developer

Keep comments only where they preserve a non-obvious invariant: exact selector matching, redaction before durable boundaries, the accepted replay/embedding order, deterministic RRF tie-breaking/deduplication, and the precise budget stop condition. Each comment states **why** the guard exists and links to a focused test when useful. Do not use comments to conceal a new fallback, failure policy, model choice, or token calculation; those require an explicit contract/semantic decision.

## Handoff

The developer may resume a mechanical split after C-01 is isolated and C-02/C-05 characterization gates are in place. C-03 and C-04 require the stated semantic clarification before any optional-provider or token-accounting behavior is added.
