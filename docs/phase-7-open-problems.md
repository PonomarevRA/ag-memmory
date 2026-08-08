# Phase 7 — Open Problems

1. `SessionHotMemory.Content` remains the adapter's opaque string field. A future
   storage schema migration can persist structured entries separately for filtering
   and analytics without exposing storage types to Core.
2. Compact one-line validation prevents transcript and raw-log shapes, but it cannot
   semantically classify every conversation or log. Ingress policy should add
   product-specific detection before constructing `HotMemoryState`.
3. Promotion is intentionally limited to `WorkingFact`. Phase 8 must define the
   decision-trace contract, redaction policy, and durable representation before
   enabling `RecentDecision` promotion.
4. The estimated token bound covers rendered entry text, not citation/separator
   overhead. This preserves the Phase 6 estimate contract; a future context contract
   can make rendered-token accounting strict and testable.
5. The coordinator is constructed directly around `MemoryCommandService` so the
   internal typed-state handler can retain command transaction semantics. Phase 10
   should define composition/DI registration and client-facing construction without
   leaking that concrete implementation to callers.
