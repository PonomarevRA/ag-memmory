# UI/UX design brief — AgMemory Web

> **Статус:** proposal для отдельной задачи web-host; production UI в репозитории пока отсутствует.
> **Владелец:** ui-designer.
> **Основание:** `README.md`, `docs/architecture.md`, `docs/contracts.md` и `src/AgMemory.Contracts/*`.

## Findings and design boundary

В solution нет web-проекта, client facade, MCP-server или model provider. Поэтому UI добавляется как
отдельный host `src/AgMemory.Web`; `Contracts` и `Core` не получают ASP.NET/UI-зависимостей.
Чат обращается только к same-origin server-side model gateway. Ключи провайдеров, internal endpoints и
авторизационная политика не попадают в browser.

`AgMemory` не является хранилищем разговоров: туда запрещено неявно записывать сообщения, raw logs и
chain-of-thought. Transcript живёт только в browser-local history; сохранить из него memory можно лишь
осознанным действием пользователя, через существующий redacted/authorised command path. Пока gateway
или утверждённое отображение scope (P0-01) не настроены, UI показывает честное состояние
«Не настроено», а не имитирует ответ модели или results поиска.

## Information architecture

| Route | Purpose | Primary content |
| --- | --- | --- |
| `/` | Overview / first-run | capability status, quick actions «Начать чат» and «Подключить агента», privacy summary |
| `/chat` and `/chat/:threadId` | Chat with model | thread list, messages, composer, streaming state, source citations, local-thread controls |
| `/memory` | Read-only memory exploration | authorised search, filters, result cards, citations and empty/denied states |
| `/connect` | Connect an agent | prerequisites, server-side connection steps, client/MCP availability, copyable verified snippets |
| `/about` | Project information | purpose, architecture boundary, what is and is not retained, links to project docs |
| `/history` | Visit history | local route history, revisit and clear controls |
| `/settings` | Local preferences | theme, motion, local-data controls and accessible-display preferences |

Global shell: skip link, product mark, primary navigation (`Чат`, `Память`, `Подключить агента`,
`О проекте`), current-page heading, explicit Back control and a History drawer. On desktop the chat
thread rail is persistent; on mobile it becomes a modal drawer. `/history` is also available as a full
page so history remains usable without a pointer device.

Do not encode query text, transcript content, scope values or credentials into routes. Browser-created
opaque thread IDs are permitted only for local thread selection.

## Critical user flows

### First visit and navigation

1. Landing page explains the current host/model connection status and makes the next safe action clear.
2. Each completed navigation calls `history.pushState`; Back calls native `history.back()` and
   `popstate` restores the exact route and scroll position.
3. A separate browser-local visit log records a de-duplicated `{ route, title, visitedAt }` entry
   after navigation. Selecting an entry revisits the route; it never stores search terms, form values,
   message text, IDs from an external service, or secrets.
4. The History drawer/page allows one-item removal and **Clear local visit history**. On unavailable
   storage or quota failure, routing still works in the current tab and a non-blocking status explains
   that persistence is unavailable.

### Chat with a model

1. User opens a new local thread, reads the compact privacy note and enters a prompt.
2. Composer validates non-empty input, keeps a draft on network failure, and sends it to the
   authenticated same-origin gateway. The server resolves actor and authorised scope; the browser
   never submits authoritative selectors or provider credentials.
3. The assistant message streams incrementally. A visible `Генерируется…` state has a Stop action;
   screen readers receive a polite, throttled live announcement rather than every token.
4. If a response uses AgMemory context, citations appear as compact expandable chips. They expose
   memory/evidence IDs only after the server authorised them; no citation is fabricated for an empty
   result or denied scope.
5. Explicit **Сохранить как memory** opens a review dialog: compact proposed fact/decision, type,
   scope display, and redaction/error result. It does not send the entire transcript. Cancel leaves the
   thread unchanged.
6. No configured gateway, authorization denial, timeout, stream interruption and rate limit each have
   distinct recoverable messages. A denied read is not rendered as “no memories found”.

Chat transcripts may be retained locally to make the chat usable after refresh, but are separate from
navigation history and from AgMemory durable records. The UI makes this visible and offers per-thread
and all-local-data deletion.

### Connect an agent

The page is an executable-looking guide only where the corresponding host/client feature actually
exists. It has these sections:

1. **What the platform keeps:** compact reusable facts, decisions, citations and bounded context;
   it does not retain raw conversation histories or provider keys.
2. **Prerequisites:** deploy/configure a server-side AgMemory host, authenticate the calling agent,
   configure the authorised scope validator and an approved model gateway.
3. **Integration method:** a status card for `.NET client` and for `MCP`. The current repository has
   the Contracts/Core boundary but not `AgMemory.Client` or `AgMemory.McpServer`; those cards must say
   “planned / unavailable” until a versioned package and endpoint are delivered.
4. **Connection steps:** point an agent to the server-owned endpoint, map its identity to `ActorId`,
   request an exact `MemoryScope`, then use recall/remember only through the public facade. State that
   tenant/project/workspace/chat/run mapping is pending P0-01.
5. **Security callout:** never put model-provider keys, database paths or authorisation rules into
   agent/browser configuration. Link to `docs/architecture.md`, `docs/contracts.md` and
   `docs/phase-0-open-problems.md`.

Copy controls must copy only verified, versioned examples. Before a stable server configuration is
defined, show conceptual pseudo-configuration labelled as such rather than invented environment names
or credentials.

## UI kit

Implement tokens as CSS custom properties (or their typed equivalent) and expose light, dark and
system themes. The visual tone is calm, technical and information-dense without resembling a terminal.

| Token group | Specification |
| --- | --- |
| Colour | Neutral surface/text scale; indigo primary; teal success; amber warning; red destructive. Every semantic state has light/dark values and never relies on colour alone. |
| Typography | System sans stack with Russian glyph coverage; 14 px body minimum, 16 px composer text, 20/24/32 px heading scale, 1.45–1.6 line-height. Monospace only for snippets/IDs. |
| Space and shape | 4 px base grid; 8/12/16/24/32/48 spacing; 8 px controls, 12 px cards, 16 px dialogs; 1 px semantic borders. |
| Layout | App content max-width 1,200 px; readable prose max-width 760 px; desktop chat rail 280 px; composer sticky above safe-area inset. |
| Motion | 120–180 ms opacity/transform only; honour `prefers-reduced-motion`; no motion is required to understand state. |

Required primitives:

- `AppShell`, `TopNav`, `SideNav`, `PageHeader`, `Breadcrumbs`, `BackButton`, `HistoryDrawer`;
- `Button` (primary, secondary, ghost, destructive, icon), `TextField`, `Textarea`, `Select`,
  `Checkbox`, `Toggle`, `Badge`, `Tooltip`, `Card`, `Divider`, `Tabs`, `Dialog`, `Toast`;
- `StatusBanner`, `EmptyState`, `ErrorState`, `Skeleton`, `Progress`, `InlineNotice`;
- `ChatThreadList`, `MessageBubble`, `Composer`, `StreamingIndicator`, `CitationChip`,
  `ScopeBadge`, `MemoryResultCard`, `CodeSnippet` with copy feedback;
- all controls document default, hover, focus-visible, active, disabled, loading, error and
  empty/no-data states.

The initial UI-kit Storybook/component-gallery (or framework-equivalent route) must show every state,
light/dark theme and a narrow viewport. It is a project-owned component set, not a pasted third-party
template.

## Accessibility, responsive and performance requirements

- Meet WCAG 2.2 AA: text contrast at least 4.5:1, visible keyboard focus, logical heading order,
  labelled controls, 44×44 px touch targets, semantic buttons/links, dialog focus trap and return,
  and no keyboard trap in drawers or code snippets.
- Provide a skip link, `aria-current` navigation state, route-change announcement, descriptive copy
  for loading/error/empty state and text/icon pairing for all status colours. Messages remain
  selectable; citations are reachable by keyboard.
- At 320 px width, primary navigation and chat history collapse into drawers; composer stays available,
  code examples wrap or horizontally scroll, and no essential action depends on hover. Test 200% zoom
  and Windows high-contrast mode.
- Keep only current/nearby chat messages in the DOM; virtualise long threads/results and render
  streamed tokens in batches. Persist local state on debounce/route completion, cap the visit log,
  and use versioned local-storage records so upgrades can migrate or discard safely.
- QA establishes the first browser baseline using a 100-item visit log and long chat fixtures:
  route transitions, streamed-message render cost, DOM node count, JS heap before/after repeated
  navigation and local-storage/quota fallback. Report the measurements before setting numeric SLOs;
  do not claim production targets from the synthetic memory corpus.

## Acceptance checks and handoff

1. Every route is reachable via keyboard and every completed navigation appears after reload in a
   privacy-safe local visit log; browser Back restores route and scroll state.
2. Chat supports draft, send, streaming, stop, recoverable errors and local thread resume; it cannot
   expose provider keys or silently convert a transcript into memory.
3. Memory results show only server-authorised citations; `Unauthorized` and unavailable-provider states
   are distinguishable from an empty result.
4. `/connect` accurately marks unavailable Client/MCP capabilities and contains no fabricated endpoint,
   credential or scope-mapping claim.
5. The UI-kit has documented component/state/theme examples and passes automated accessibility checks
   plus manual keyboard, screen-reader and narrow-viewport checks.
6. QA includes local-history persistence/back-navigation and the browser-resource measurement report in
   its test evidence.

**Developer handoff:** build the separate host and UI-kit against a server gateway interface; preserve
the Contracts/Core dependency boundary and introduce no provider secret or implicit transcript-to-memory
write.

**QA handoff:** test the acceptance checks above with gateway configured/unconfigured, authorised/denied
scope, streamed-error and storage-quota fixtures; attach browser resource measurements rather than an
unsubstantiated performance claim.
