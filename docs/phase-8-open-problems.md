# Phase 8 — Open Problems

1. Compact structural validation blocks multiline/transcript-shaped payloads but
   cannot semantically identify every prose chain-of-thought. Product ingress policy
   may add a versioned classifier or review rule before it constructs a decision
   command; it must not retain rejected text in receipts or outbox messages.
2. `Outcome` remains optional because a decision can be recorded before its result is
   known. A future lifecycle or outcome-linking contract should define how an outcome
   is updated without overwriting the immutable decision trace.
3. Decision deduplication follows the existing durable-memory key derived from the
   compact decision/problem text. Product policy has not yet defined whether similar
   decisions with different context should reinforce or coexist.
4. Phase 10 still needs client/MCP transport composition, authenticated actor mapping,
   and shared conformance tests for `RecordDecisionAsync`; this phase intentionally
   adds neither transport nor dependency-injection registration.
