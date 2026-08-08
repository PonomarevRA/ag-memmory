# Performance baseline

**Recorded:** 2026-08-08  
**Environment:** .NET SDK 10.0.102, macOS 27.0, arm64.  
**Command:** `dotnet test ag-memory.slnx -c Release --no-build --no-restore -v quiet`  
**Fixture:** the complete deterministic test suite; this is a CI health baseline, not a production load test.

## Warm samples

| Metric | Samples | Median |
| --- | --- | --- |
| Wall time | 2.05, 1.92, 2.06, 1.79, 2.04 s | **2.04 s** |
| User CPU | 7.53, 7.38, 7.33, 7.08, 7.30 s | **7.30 s** |
| System CPU | 3.04, 2.93, 3.49, 2.73, 3.19 s | **3.04 s** |
| Peak RSS | 178.6, 183.1, 183.1, 182.1, 180.3 MB | **182.1 MB** |
| Peak memory footprint | 134.5, 136.7, 136.7, 135.8, 133.9 MB | **135.8 MB** |

The preceding cold build-and-test run took 8.44 s with a 254.6 MB maximum resident set. It includes
restore/build work, so it must not be compared to the warm samples as a code-performance delta.

## Implemented, bounded optimisation

`LanceDbMemoryStore` previously copied a valid query vector once to check finiteness and again to call
`NearestTo`. Finiteness is now checked over `ReadOnlySpan<float>`; LanceDB still receives its one required
array. This removes one `float[]` allocation for every valid vector query without changing ranking, schema,
authorisation or transaction semantics. The focused LanceDB suite and the full suite remain green.

## Remaining candidates and guardrails

- One adapter-wide semaphore serialises reads and writes. Do not relax it before a concurrent-read benchmark
  proves queueing cost and transaction isolation tests cover the proposed change.
- Lexical retrieval materialises and tokenises all eligible records. Measure 1k/10k/100k scoped data before
  choosing FTS or an index policy.
- Browser route/back, long-thread DOM/heap and local-storage quota require a browser-capable E2E runner;
  they were not measured by this CLI baseline.

Any further optimisation must report fixture size, p50/p95 latency, allocation/GC, CPU, peak working set
and regression-test outcome before it is accepted.
