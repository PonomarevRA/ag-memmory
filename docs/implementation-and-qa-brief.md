# Implementation and QA brief — human-ready AgMemory and Web UI

> **Role card:** analyst
> **Status:** implementation plan; no production or test code changed by this brief.
> **Sources:** `docs/implementation-architecture.md`, `docs/ui-ux-design-brief.md`,
> `src/AgMemory.Contracts/Commands/`, `src/AgMemory.Contracts/Queries/`,
> `src/AgMemory.Contracts/Ports.cs` and current solution map.

## Current behaviour and fixed boundaries

The solution currently contains Contracts, Core, the LanceDB adapter and compatibility packages. Core
is already grouped into `Commands`, `Retrieval`, `HotMemory` and `Decision`, and current tests cover
those areas. There is no web project, browser application, model gateway, `AgMemory.Client`, or
`AgMemory.McpServer` in `ag-memory.slnx`.

The material remaining code units are `MemoryCommandService` (406 lines) and
`LanceDbMemoryStore` (1,352 lines). The former already has a public facade plus four mutations; the
latter currently combines connection lifecycle, schema manifest, predicates, mapping, persistence,
search and its transaction implementation.

These boundaries are non-negotiable for every task below:

- `AgMemory.Contracts` and `AgMemory.Core` remain free of ASP.NET, browser, provider, LanceDB/Arrow,
  SQLite and AGM types. A new web host is a separate project and composition root.
- A request is authorised before any memory port. Only validator-issued exact selectors are usable;
  no run/chat/workspace/project/tenant widening is allowed.
- No raw content reaches store, receipt, outbox or telemetry before ingress redaction. An idempotency
  key reused with a different payload returns `IdempotencyKeyConflict`; a success commits mutation,
  receipt and content-free outbox atomically.
- Public command/query names, signatures, result enums and `MemoryErrorCode` values remain compatible.
  `MemoryError` continues to have no free-form message field.
- Browser-local visits and chat threads are not AgMemory durable records. Provider keys, database paths,
  authorisation rules, raw transcript and chain-of-thought never reach the browser or an implicit
  `RememberAsync` call.

## Ten separately owned change tasks

| # | Requested change and owner | Ordered implementation delta | Acceptance criteria | Explicit non-changes |
| --- | --- | --- | --- | --- |
| 1 | **Feature folders** — developer | Preserve existing Core feature folders; move only code that has a single feature owner. Split the LanceDB adapter into `Connection`, `Schema`, `Query`, `Persistence`, `Serialization` and `Transactions` internal folders. Add `src/AgMemory.Web/{Features,UiKit,Navigation,LocalData,Gateway}` and feature-matched test folders. | Each production file has one feature owner; solution/project references remain valid; public namespaces and public types are unchanged; no storage/provider type appears in Contracts/Core. | Do not move `src/Compatibility/*`, rename public APIs, merge unrelated phases, or introduce an AGM source reference. |
| 2 | **Split large classes** — developer, critic review | Keep `MemoryCommandService` as the public facade; extract one internal handler each for remember, lifecycle, append-hot-memory and forget behind a shared preflight/workflow boundary. Split `LanceDbMemoryStore` by the folders in task 1 while preserving its public constructor, `CurrentStorageSchemaVersion`, transaction semantics and exact predicate generation. Do this only after characterization tests exist. | No public API diff; each handler has one mutation; a transaction still commits once after record/hot/delete + receipt + outbox staging; LanceDB reopen/schema/search/isolation tests retain their observable results. | Do not change current embedding-before-receipt ordering during mechanical extraction; do not change schema version, storage layout, RRF, token estimate or delete policy. |
| 3 | **Human-ready summaries** — developer, reviewer | Add concise XML `<summary>` documentation to public Contracts types/members and public Core facades; add intent comments only at exact-selector, redaction-before-persistence, idempotency, deterministic tie-break and token-budget boundaries. Add human-oriented README text only for verified host/UI capabilities. | Generated API XML/documentation has summaries for public extension points; comments explain *why*, not restate code; code remains concise and builds without warnings. | Do not invent provider configuration, endpoint URLs, scope policy or unimplemented Client/MCP features; do not add noisy comments to every private method. |
| 4 | **Testing** — QA owner, developer supplies fixtures | Extend the existing xUnit suite before refactoring; add a host/UI test project after the web host exists and a centrally pinned browser/E2E runner only if needed by the chosen UI framework. Maintain feature-oriented test names and fixtures. | Unit, integration, contract/architecture, accessibility and browser-flow cases in the QA matrix below pass; Release solution test command and pack validation pass when run by the implementation owner. | Do not claim tests have run in this planning task; do not substitute mock-only tests for LanceDB isolation/reopen coverage or browser back-navigation coverage. |
| 5 | **UI/UX design review** — ui-designer, developer implements | Treat `docs/ui-ux-design-brief.md` as the accepted design source: information architecture, responsive shell, states, accessibility, local-data rules and copy boundaries are implementation requirements. | All listed routes and states map to implemented features; a reviewer can trace every UI feature to the brief; automated and manual accessibility evidence is attached. | Do not rewrite the accepted brief in an implementation PR or replace it with an unreviewed third-party template. |
| 6 | **UI chat with a model** — developer, security/product unblock provider | In the isolated host define an internal server-only `IModelChatGateway` and safe streaming event/error DTOs. The browser posts only a thread-local opaque ID and prompt to a same-origin endpoint; the host resolves identity, authorisation and optional memory context server-side. Provide a disabled gateway implementation for unconfigured environments and fakes for tests. | New thread, draft, send, streaming response, Stop and retry work with a configured gateway. `Unavailable`, `Unauthorized`, `RateLimited`, `Timeout`, `Interrupted` and invalid input are rendered as distinct safe states; an authorised citation can accompany a response. | Do not introduce a model SDK/provider dependency into Contracts/Core, expose secrets, let the browser select an `ActorId`/authorised selector, fake an answer when unavailable, or auto-save transcript as memory. |
| 7 | **Project UI-kit** — developer, ui-designer review | Implement first-party tokens and reusable primitives under `AgMemory.Web/UiKit`; include a component-gallery route or framework-equivalent documentation page. Consume primitives from features rather than duplicating control CSS. | Light/dark/system tokens and documented default/hover/focus/active/disabled/loading/error/empty states exist for shell, navigation, controls, feedback, chat and citation components. Gallery renders at a narrow viewport. | Do not copy a whole third-party design system, use colour as the sole status signal, or make visual motion required for comprehension. |
| 8 | **Agent connection information page** — developer, docs reviewer | Implement `/connect` from the accepted copy outline: what is retained, prerequisites, server-side actor/scope model, connection stages, security warning and links to canonical docs. Render Client/MCP cards from actual capability status. | The page explains how an agent connects through a server-owned host and exact scope authorisation; unavailable `AgMemory.Client`/MCP remain visibly “planned/unavailable”; all copyable snippets are verified and versioned. | Do not publish a fictional endpoint, environment variable, credential, scope map or claim that P0-01 is resolved. |
| 9 | **Persistent visit history and Back** — developer, QA owner | Implement native browser `pushState`/`popstate` plus scroll restoration and a versioned local store such as `agmemory.ui.visit-log.v1`. Record only de-duplicated `{ route, title, visitedAt }` records, cap the log, expose history drawer/page, revisit/remove/clear actions and storage-failure fallback. Keep thread history in a separate local store. | Every completed UI navigation survives reload in the visit log; browser Back/Forward restores route and scroll; keyboard users can open and use history; quota/unavailable local storage leaves the current-tab navigation working. | Do not put prompts, transcript, query text, form state, external IDs, scope values or secrets in URL/history; do not treat visit/thread data as durable AgMemory memory. |
| 10 | **Resource measurement and optimisation** — QA/performance owner, developer fixes measured hotspots | Establish reproducible browser and server baselines after tasks 1–9. Use deterministic fake gateway/retrieval fixtures; capture route and stream latency, DOM nodes, JS heap where supported, local-store size/fallback, process CPU/allocation/working set and retrieval p50/p95. Optimise only a reported hotspot. | Report records environment, commit, scenario, fixture size, metric and before/after result. Long chat/results are virtualised or windowed, streamed DOM updates are batched and local writes debounced if those are measured bottlenecks; functional/security regression cases still pass. | Do not invent SLOs from the synthetic corpus, optimise by guesswork, log raw prompt/memory text, or weaken exact-scope/redaction/citation checks for speed. |

## Developer implementation brief

### Delivery sequence

1. Add baseline/characterization tests from task 4, then deliver task 1 and task 2 as an extraction-only
   change. Keep the public source types in their namespaces; `MemoryCommandService` is a thin facade,
   and internal handlers receive only the workflow dependencies they need.
2. Complete task 3 alongside extraction, using summaries to document public obligations rather than
   changing them. The author of each file owns its summary.
3. Create `src/AgMemory.Web` as a separate `Microsoft.NET.Sdk.Web` project targeting the solution SDK.
   It may reference Contracts/Core/adapter only in its composition root; UI feature code speaks to
   host-internal interfaces and DTOs, never to storage types.
4. Build tasks 7, 9 and the shared shell first, then pages from task 5 and task 8. Their static,
   honest unavailable states must work without a model provider.
5. Build task 6 around the server gateway. Use an explicit cancellation token for Stop and avoid
   streaming provider exceptions or raw diagnostics to the UI. Optional memory context enters only
   after server-side authorisation and retains the Contract error/citation semantics.
6. Complete the test evidence (task 4), establish the baseline and make only measured task-10 changes.

### Host-internal contracts and error mapping

These are host-local, not new `AgMemory.Contracts` public API. Their exact class names may differ, but
their capability and error boundary must remain.

| Host capability | Required behaviour |
| --- | --- |
| `IModelChatGateway.StreamAsync` | Accepts server-resolved identity/context, prompt and cancellation; yields text/status/citation events; has no provider secret or browser-supplied authorisation input. |
| Request mapper | Rejects blank/oversized input before gateway use; generates/validates only local thread IDs; converts cancellation to a completed stopped state. |
| Safe error mapper | Emits one of `InvalidInput`, `Unavailable`, `Unauthorized`, `RateLimited`, `Timeout`, `Interrupted`; keeps provider exception/endpoint/credential details server-only. Maps `MemoryErrorCode.Unauthorized` to denial, not empty search. |
| Memory save action | Requires explicit confirmation and creates a compact proposed record/decision; invokes existing command services, including redaction/idempotency/authorisation, rather than serialising a transcript. |
| Capability status | The host reports configured/unconfigured chat gateway and available/planned integration paths. It does not infer P0 policy from UI input. |

The existing result/error contracts remain the source of truth: invalid command/query input is
`InvalidArgument`; incompatible version is `UnsupportedContractVersion`; denied scope is
`Unauthorized`; unavailable embedding/retention policy is `PolicyNotConfigured`; replay conflict is
`IdempotencyKeyConflict`; stale writes remain `StaleVersion`; context errors return an empty content
shape with the safe error. UI wording may be Russian-first, but no wording may expose exception text.

### Exact refactor invariants

- Preserve observed `RememberAsync` order during extraction:
  validate → authorise → redact → canonicalise → optionally embed → transaction/receipt → write,
  receipt/outbox → one commit. A later receipt-before-embedding change requires a separately approved
  semantic design and concurrency analysis.
- `BuildContextAsync` retains its exact-scope hot read, authorised search, deterministic packing,
  citation toggle, budget stop and single-summary-fallback rules. No refactor may turn an authorisation
  denial into a silent empty success.
- `LanceDbMemoryStore` preserves schema-manifest validation, exact SQL-like predicate escaping,
  vector-table partitioning, storage reopen and transaction serialisation. Internal mappers must not
  leak Arrow/LanceDB types through a public signature.
- Keep the current `ceil(chars / 4)` token estimate unchanged in extraction. Any new token policy is a
  separately versioned proposal.

## QA brief

| Area | Happy path | Edge / negative / regression cases |
| --- | --- | --- |
| Feature split and public compatibility | Build all target frameworks; public command/query and LanceDB tests run through the same facade/constructor. | API/assembly scan rejects ASP.NET, LanceDB, Arrow, SQLite and `Agm.*` in Contracts/Core; no changed result enum/error semantics; compatibility tests remain isolated. |
| Command extraction | Remember, reinforce, lifecycle, hot append and forget preserve expected result and outbox receipt. | Denied request starts no transaction; redaction reject persists nothing; same key/same payload replays; same key/different payload conflicts; stale version, failed commit and required-embedding mismatch preserve outcomes; characterise current embedding call on replay before any order change. |
| Adapter split | Open, write, search, reopen, schema-manifest and vector paths behave as before. | Foreign tenant/same workspace and same chat/different run never reach rank/citation; escaped scope input remains a literal; expired/inactive records excluded before rank; transaction failure does not half-write receipt/outbox. |
| Human-ready documentation | API XML and user-facing pages render public summaries and verified capability status. | No docs promise Client/MCP/provider availability that the solution lacks; no summary exposes source payload/secret. |
| UI kit and accessibility | Keyboard user can navigate shell, use form, dialog, drawer, toast and component gallery in light/dark themes. | Automated accessibility scan plus manual screen-reader, focus-return, `prefers-reduced-motion`, 320 px, 200% zoom and high-contrast checks; contrast/focus/target-size failures fail review. |
| Chat gateway | Configured fake gateway streams text, Stop cancels, retry retains draft, authorised citations display. | Blank/oversized input, unconfigured gateway, denial, 429, timeout, broken stream and provider exception map to safe distinct UI states; page/network log contains no key or provider exception; transcript is not remembered unless explicit review confirms it. |
| Connect page | Page explains server-owned connection and links canonical docs. | Client/MCP availability follows actual feature flag/status; copy action cannot copy unverified/pseudo configuration as executable; P0-01 remains visibly unresolved. |
| Visit and thread history | Reload preserves privacy-safe visit list and local thread; Back/Forward restores scroll and selection. | Visit log excludes prompt/query/scope/secret fields; remove/clear works; quota/security error degrades to session routing; keyboard history drawer and `/history` remain operable; private local data deletion is complete. |
| Resource baseline | Same deterministic fixtures provide comparable browser/server run output. | 100-visit log plus long-thread/result fixture detects unbounded DOM/heap growth; repeated navigation detects listener leak; stream batching, virtualisation and debounced storage are checked after optimisation; security/authorisation tests re-run after each change. |

## Assumptions and blockers

1. The web host is accepted as a new isolated project, but the UI framework and browser E2E runner have
   not been selected. The developer must make and record a framework-compatible choice without changing
   public memory contracts.
2. No model provider, credentials, endpoint contract or server-side identity integration is supplied.
   A real model conversation is blocked until the product/security owner supplies one; the host can ship
   its disabled state and test fake gateway, but must not simulate production availability.
3. P0-01 (authorised `tenant/project/workspace/chat/run` mapping) remains unresolved. Chat without
   memory context can be integrated after a gateway exists; authorised memory recall/save in production
   remains blocked until P0-01 is approved.
4. P0-02 retention/delete and P0-03 embedding policy remain external. The UI may display their safe
   `PolicyNotConfigured` result but cannot choose a retention/embedding policy.
5. `AgMemory.Client` and `AgMemory.McpServer` are planned Phase-10 work, not current features. The
   connection page therefore documents the host boundary and marks those integrations unavailable.
6. Numeric performance targets do not exist. Task 10 must publish a baseline before proposing a budget
   or declaring an optimisation complete.

## Handoff

**Next owner:** developer.

**Exact artifact:** `docs/implementation-and-qa-brief.md`.

Developer starts with task 4 characterization coverage and then task 1/2 extraction-only work; QA owns
the evidence matrix and task-10 baseline once a web host exists. The critic reviews any proposed
semantic change to command ordering, public API or dependency firewall before it is merged.
