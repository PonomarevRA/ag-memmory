# Retrieval, task context and evidence in AgMemory 1.7

The existing `agmemory-agent-v2` and `agmemory-knowledge-v1` envelopes remain unchanged. New tools are additive and must be negotiated from capabilities; a package version never substitutes for discovery. Legacy `memory_recall`, `memory_context`, `working_context` and `working_update` retain their arguments and wire projections. The default ranker remains lexical overlap; BM25 is opt-in.

## Retrieval

`AGMEMORY_QUERY_ALIASES=on` enables the packaged, reviewable `recall-aliases/v1` RU/EN dictionary. It is bounded to three expansions plus the original query. Acronyms such as `AFTER` are case-sensitive and respect ordinary/typographic quotation boundaries. Ordinary `after` is not expanded. Unknown wording remains lexical; this is not semantic search. `off` restores literal queries, including legacy queries longer than1000 characters.

Ranks are fused with bounded reciprocal-rank fusion, preserving canonical text and logical deduplication keys. At most256 provider candidates become a final pool of64, followed by whole-record packing of at most10 sources. The serialized character budget still includes citations and envelopes; required L2 overflow fails explicitly.

`AGMEMORY_RECALL_INDEX=on` enables a derived exact-scope SQLite posting index. The native Lance table generation, tokenizer and scope fence each cached generation. Query-time type/lifecycle/expiry filters also determine BM25 statistics. Rare multi-term queries seek postings before joining document metadata. BM25 still performs metadata aggregate work; the index does not make every query constant-time.

The cache is not an authority boundary: candidate IDs/text are rechecked against a pinned, exact-eligible canonical projection before even legacy recall can return them. Context APIs then read up to64 current source projections in one batch, preserving ranking while avoiding repeated full-record reads. Neither hot path deserializes embeddings or provenance JSON. Current revisions and expiry are used for citations; stale or changed sources are omitted.

Index limits are100,000 rows,16 exact-scope files and256MiB per database, with an8MiB SQLite cache setting. Build streams256 native rows at a time. Capacity or cache I/O failure falls back to the bounded scan; `off` selects scans explicitly. A change anywhere in the native table can invalidate a scope's cache. Cold builds hold the cooperating native lease. Diagnostics count materialized native/posting rows, excluding SQLite internal metadata reads; timings must be measured at the full MCP path too.

## Task L2 and legacy compatibility

`task_context_compact` is an additive advertised tool using the same task L2, eligibility, citations and whole-record budget as `task_context`. It starts with three cards (`maxCards` accepts 1–10). With a nonblank original query, clients may supply up to three optional `queryFacets` of at most 160 characters. The server deduplicates query strings and interleaves ranked candidates across facets and the original query, with a final pool of 64. Each logical query retains its ordinary bounded lexical alias behavior. Clients generate focused terms in their own model context and can widen the card limit when evidence is incomplete. The existing tools retain their response limits and behavior.

Cards are summaries. If a detail is absent, an agent can check `memory_evidence` for an available locator and use its authorized source reader to confirm it. Source locators do not confer file or network access.

`task_context` and `task_working_update` require a1–100 character ASCII symbolic `taskKey`. Each task has its own24h L2, CAS fence and replay receipts, bound to the existing exact actor/team authority. The label does not authenticate a participant or grant access. Canonical sources and warm residency remain shared within that configured authority.

Task state lives in `.agmemory-working` using the established schema-v1 aggregate. Older shared-context writers therefore cannot erase unfamiliar task fields. Legacy shared L2 remains in `.agmemory-knowledge`; task reads never fall back to an unrelated shared goal. Expired content is hidden while `WorkingSetRevision` remains visible. The task namespace is bounded to128 profiles; each retains existing aggregate/receipt limits.

The portable `agent-memory`3.2 guidance keeps Markdown and A2A v1 layouts, existing fields and record kinds. Alternate search terms do not establish semantic equivalence; record timestamps do not establish factual validity. Bootstrap updates only unchanged managed copies, preserves extensions and refuses conflicting/legacy copies without explicit adoption. No memory migration or MCP dependency is introduced.

## Claims, time and procedures

`memory_evidence` resolves a current citation and returns structured caller claims when present; otherwise it returns `LegacyUnknown`. Citation validity means current revision eligibility, not truth, observation or approval. Raw evidence has `HasConflict=null` because it has not performed tuple/time conflict analysis.

`memory_claim` appends a caller-reported `UserStatement`, `ToolObservation` or `AgentInference` with an explicit source revision, optional locator/observation time and validity interval. Missing dates remain unknown. `memory_claims` separates half-open valid time from known-at transaction time and preserves conflicting eligible values. Unknown intervals can represent potential conflicts requiring review. Supersession is applied only among eligible sources, so a stale replacement cannot hide a current predecessor. Default queries require a current source; explicit history is flagged and carries no fresh citation. Scope moves/deletions revoke historical projection too.

Annotations use a separate private atomic namespace, bounded to128 profiles,4MiB and1024 entries per collection. Old knowledge writers cannot drop them. It is not an unbounded event archive.

`memory_procedure_facets` attaches immutable declared environment-family/capability/precondition facets to one exact procedure revision. A newer revision does not inherit them. `memory_procedure_discovery` uses lexical RU/EN terms and filters missing capabilities; a family match in another environment remains `TransferCandidateNeedsReview`. Legacy `memory_procedures` keeps its exact applicability behavior. Applications retain their actual procedure revision and caller-reported outcome; no tool grants `VerifiedSuccess`.

### Shared procedure library across projects

Ordinary project connections use `memory_shared_procedure_publish` to share a reusable solution and `memory_shared_procedure_discovery` to find it. Both tools target the built-in library in the connection's existing local store and tenant. Agents in different repositories need no shared `teamId` or additional MCP profile. Project notes, local procedures and task L2 keep their existing scope.

For example, after resolving Kafka consumer lag, an agent publishes a generalized card. Send `goal`, `aliases` (including RU/EN search terms when useful), `applicability` (the exact target environment label, up to 500 characters, such as `dev`), `preconditions`, `steps`, `verification` and a symbolic `requestKey`. Remove repository-specific paths, private URLs, hostnames, secrets and incident payloads. The tool takes the new card's text explicitly. It does not copy an existing note, accept a runbook reference or promote an entire task.

Publication creates revision 1. Use the same request key for retries from the same configured agent/profile. Repeating it with changed content returns a conflict; independent profiles can use the same symbolic key for their own publications. Ordinary project tools cannot edit a shared card with its discovery reference. Existing cards remain discoverable, including cards published through the previous dedicated profile. A later contribution can be published as a separate card using a new key, with applicability and verification describing what changed.

When another project encounters the same problem, it calls `memory_shared_procedure_discovery` with its query, environment and available capabilities. Discovery returns caller-reported, unverified advice. Check applicability, preconditions and current authorization before use. Separate tenants and separate stores have separate libraries.

## Temporary notes and issuance history

When advertised, `memory_temporary_note_create` writes a note with a fixed 48-hour UTC lifetime. Provide a title (up to 160 characters), content (up to 4,000 characters) and a stable `requestKey`. A successful retry preserves the same ID and expiry. Notes live in the configured store and exact tenant/project/workspace/chat/run scope. Different configured actors in that same scope can find and receive them. A note ID or a chat label cannot widen that scope.

`memory_temporary_notes(query, limit)` matches titles and returns up to ten active summaries with author, timestamps and recipient history. It does not return bodies or add a recipient. `memory_temporary_note_read(noteId, conversationId)` commits the issuance before returning the body. The history coalesces repeated issuances by actor, client, chat and run and records the first and last timestamps and count. It describes server issuances, without claiming that the transport reached the model or a human read the content.

The server takes actor, client and run from its configuration. The optional symbolic `conversationId` is labelled `caller_reported` when the configuration supplies no chat. If neither is present the chat is unknown. A supplied ID cannot contradict a configured chat. The scope admits up to 64 active notes and 32 distinct recipients per note. Expired notes and their histories are hidden immediately. Later mutations scrub expired content and identities, with an opaque replay tombstone retained for up to another 48 hours before it is pruned. Up to 128 note scopes have a separate quota from claims and procedure facets. At capacity, a new scope can reclaim one whose notes and replay window have all expired. Notes use the annotation backup lease. Existing backups can retain earlier copies.

## Optional durable replay journal

Legacy knowledge storage remains the default. No live migration happens on startup. The installed MCP executable provides operator-only `--knowledge-migrate`, `--knowledge-status` and `--knowledge-rollback-before-writes` commands using its ordinary configured environment. They are not MCP tools. A source-checkout convenience wrapper is available under `tools/AgMemory.KnowledgeMaintenance`.

Migration holds the existing namespace lease, imports every receipt without changing lookup keys, preserves exact v1 bytes, then atomically publishes a schema fence. Malformed receipt-key mismatches fail before the fence. A process interrupted between import and fence leaves legacy authoritative; repeat import incorporates subsequent legacy writes. Older knowledge writers reject the fence instead of overwriting the journal. Activate `AGMEMORY_KNOWLEDGE_STORAGE=journal` and reconnect the affected writers after an explicit migration. Canonical memory APIs and task L2 remain compatible.

SQLite commits the active aggregate and receipt together. Receipts remain retained without eviction, with a namespace limit of1,000,000. The512MiB database quota uses a fixed4096-byte page size; preserved v1 files and transient rollback journals are separate. Capabilities distinguish namespace counts from profile counts. Other active collection limits remain bounded. Rollback-before-writes is rejected once any journal mutation has been accepted; later downgrade requires an explicit restore/export workflow.

Backup acquires canonical, audit, shared-knowledge, task-L2 and annotation leases in order and copies journal data before compression. Restore to the same configured path preserves authority/citations. Moving a store to another path deliberately changes authority identity and needs a separate migration. Process-kill tests do not establish power-loss durability.

## Evidence and conditional research gates

`tools/AgMemory.RoadmapProbe` contains a frozen64-query,21-document repository-derived pilot, split by topic families. It runs scan/index × lexical/BM25 × literal/aliases, with actual unrelated-task L2 controls. It is not an independently sampled real-user benchmark. `tools/AgMemory.PerformanceProbe --indexed-recall` measures cold/warm storage work at1k/10k/100k; full stdio MCP measurements are also needed to catch hydration/IPC regressions.

The controlled25-pair reader experiment uses actual MCP discovery cards and four fresh-context current-Codex readers: no memory, project skill, equivalent L2 facts and procedure cards. It tests advice consumption and safe abstention, not procedure execution or cross-model transfer. L2 and cards carry the same information; absent case facts make baseline abstention appropriate. Exact model-build, billing and token counters are not exposed, so character estimates are not model token measurements.

Semantic embeddings remain excluded by the current user choice. Derived links/evolution stay disabled: this pilot supplies no incremental-benefit evidence for them beyond direct sources/L2/cards. Retain both as conditional research directions rather than claiming general retrieval or transfer success.
