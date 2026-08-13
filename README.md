# ag-memmory

Standalone .NET solution for the gradual extraction of AGM memory. Phase 1
contains the legacy-compatible packages unchanged at the public API boundary;
the future `AgMemory.*` contracts and runtime are deliberately not introduced
here.

## Phase-1 compatibility layout

- `src/Compatibility/Agm.Memory.Abstractions` — legacy provider-neutral contracts
- `src/Compatibility/Agm.Memory` — legacy ingestion, retrieval and context services
- `src/Compatibility/Agm.Memory.Sqlite` — legacy SQLite persistence and DI integration
- `tests/Compatibility/Agm.Memory.Tests` — the migrated legacy test suite

The folder makes the temporary `Agm.Memory.*` boundary explicit, while package
IDs, assembly names and public namespaces remain `Agm.Memory.*` for supported
AGM consumers. No source project under this repository is referenced by AGM.

## Build and verify

The repository pins the SDK in `global.json` and every direct external package
version in `Directory.Packages.props`.

```bash
dotnet test ag-memory.slnx -c Release
dotnet pack ag-memory.slnx -c Release --no-build -o artifacts/local-feed
```

## Local Web UI

`AgMemory.Web` is a separate .NET host for a Russian-first Vite single-page application. The browser
UI is built entirely from decomposed TypeScript modules; .NET serves the local API, static SPA files
and server-only model/storage configuration without adding web or model-provider dependencies to
Contracts or Core.

```bash
dotnet run --project src/AgMemory.Web/AgMemory.Web.csproj
```

Development defaults to the locally running Small Yuki `llama-server` at
`http://127.0.0.1:8081/v1/chat/completions` with model alias `small-yuki-local`. It is loopback-only and
uses a non-secret local marker required by the gateway's OpenAI-compatible validation. To use another
provider, override the server-side-only settings (never browser code): `ModelGateway__Mode`,
`ModelGateway__Endpoint`, `ModelGateway__Model`, and `ModelGateway__ApiKey`. A configured real model is
allowed only from loopback in Development; production enablement requires a separately implemented
authenticated host. Requests carry antiforgery protection, are limited to 12 per minute per remote
address, and send an upstream output-token cap. The SPA obtains its same-origin request token from
`/api/antiforgery`; the token and cookie are never embedded in the Vite build. The browser transcript
and navigation remain local UI state. In Development, the server records each submitted prompt and
completed model answer as a local AgMemory event in the configured exact scope; later prompts retrieve
server-bounded, lifecycle-filtered context for Yuki. This is local persistence, not a production
identity-to-scope policy.

The Vite client is in `src/AgMemory.Web/client`. Run `npm test` and `npm run build` there for focused
client verification; the Web project runs both automatically before its .NET build or test.

### Codex, Cursor and Claude ↔ AgMemory

`AgMemory.McpServer` exposes one explicitly configured local store over stdio MCP. Every client
registration has only three tools: `memory_recall`, `memory_remember` and `memory_status`. The storage
path, actor and exact scope are fixed in the MCP process environment — they are not tool inputs. Set
`AGMEMORY_CLIENT_ID` separately for each host (`codex`, `cursor`, or `claude`); it is written to the
new memory's provenance and cannot be supplied by an agent. Omit it only for an existing Codex
registration, where it defaults to `codex`.

Use the same storage path and scope values when the clients are intentionally sharing memory; keep
separate actor IDs per client so provenance remains attributable. Example environment block for a
Cursor registration (the command is identical for Codex and Claude):

```json
{
  "command": "dotnet",
  "args": ["run", "--no-build", "--project", "/absolute/path/to/ag-memmory/src/AgMemory.McpServer/AgMemory.McpServer.csproj"],
  "env": {
    "AGMEMORY_STORAGE_PATH": "/absolute/path/to/shared-memory.lancedb",
    "AGMEMORY_ACTOR_ID": "cursor-local",
    "AGMEMORY_TENANT_ID": "local-shared",
    "AGMEMORY_CLIENT_ID": "cursor"
  }
}
```

`memory_recall` remains a compatibility-safe list response, bounded to 1–10 active scoped matches;
its ranking now uses the Core retrieval service. The local Yuki chat uses the same Core context builder
with an 800-token budget and does not expose memory IDs or provenance to the browser. Start a new
conversation after registration and call `memory_status`, then `memory_remember` and `memory_recall`
to verify the shared scope. This local development bridge is not a production authorization model;
it does not claim concurrent multi-process writer safety or semantic embeddings.

### Local Memory Status

Страница `/memory-status` показывает, отвечает ли то же локальное хранилище, что используют чат и граф,
и сводку для его exact scope: общее число записей, доступные для recall active memory, истекшие и
неактивные записи, время последнего обновления и распределение active memory по типам. Используйте
«Обновить» после сохранения memory агентом. Если `/memory-status` показывает доступное хранилище и
ожидаемый счётчик, а MCP-инструмент `memory_status` возвращает то же число active memory, агент и UI
работают с одной настроенной памятью. Страница не передаёт в браузер текст записей, scope, actor,
сущности или постоянные идентификаторы.

### Local Memory Graph

Страница `/memory-graph` — локальная диагностическая визуализация. Настройте её только через
server-side environment variables или user secrets, не через browser code и не через committed config:
`MemoryGraph__Enabled`, `MemoryGraph__StoragePath`, `MemoryGraph__ActorId` и обязательный
`MemoryGraph__Scope__TenantId`. Остальные точные измерения scope — `MemoryGraph__Scope__ProjectId`,
`MemoryGraph__Scope__WorkspaceId`, `MemoryGraph__Scope__ChatId` и `MemoryGraph__Scope__RunId` — задавайте
только когда они присутствуют в целевом scope.

Граф доступен только в Development с loopback-запроса; при выключенной, неполной или некорректной
конфигурации, а также вне loopback/Development, он возвращает безопасное состояние unavailable. Браузер
не получает записи memory, имена сущностей или scope: только response-local opaque IDs, типы, числовые
bands/degrees и агрегированные рёбра. `SharedEntity` означает совпадение нормализованной сущности во
время запроса, а не сохранённый `MemoryRelation`.

Если браузер поддерживает WebGL2, страница показывает локальную 3D-карту; иначе автоматически остаётся
доступной совместимая 2D-карта и текстовая сводка. Three.js 0.185.1 закреплён и vendored в Web host с
MIT-лицензией и provenance в `src/AgMemory.Web/wwwroot/vendor/three/`; страница не загружает CDN или
другие внешние browser-зависимости.

## macOS standalone app

Build a self-contained `.app` bundle for Apple Silicon (or pass `osx-x64` for Intel):

```bash
./scripts/package-macos-app.sh osx-arm64
open artifacts/macos/osx-arm64/AgMemory.app
```

The bundle starts a loopback-only host and opens the UI in the default browser. Drag the generated
`AgMemory.app` to `/Applications` to install it; the accompanying ZIP is suitable for transfer.
It is ad-hoc signed for local use. External distribution still requires an Apple Developer certificate
and notarization.

The app bundle is replaceable code. User-owned configuration and default LanceDB data live outside it in
`~/Library/Application Support/AgMemory/` (or the absolute path in `AGMEMORY_DATA_DIR`), so replacing
`AgMemory.app` on update does not remove existing data. Put optional server-only settings in
`appsettings.local.json` in that same directory; environment variables override the file. A relative
`MemoryGraph__StoragePath` is resolved beneath that persistent directory, and defaults to
`memory-graph.lancedb` when the local graph is enabled. Development enables the graph with an explicit
synthetic `local-development` actor and tenant scope, so a fresh loopback launch can open
`/memory-graph` without manual configuration; use `appsettings.local.json` or environment variables to
select another local scope.

The pack produces the synchronized compatibility packages `Agm.Memory.Abstractions`,
`Agm.Memory` and `Agm.Memory.Sqlite` at version `1.0.0`. Set the standard MSBuild
property `Version` to issue a coordinated compatibility version, for example:

```bash
dotnet pack ag-memory.slnx -c Release -p:Version=1.0.1 -o artifacts/local-feed
```

## Local package feed and future private feed

`artifacts/local-feed` is a checked-in empty directory used only as a local,
credential-free package source. `NuGet.Config` maps `Agm.Memory.*` packages to
that source and all other packages to NuGet.org. It neither assumes nor stores
credentials for a private registry.

After packing, a consumer can validate the same package identities locally:

```bash
dotnet add package Agm.Memory.Sqlite --version 1.0.0 --source /absolute/path/to/ag-memmory/artifacts/local-feed
```

A release owner must replace this local validation source with the approved
private feed and publish the exact version before AGM swaps project references
for version-pinned package references. That external release step is tracked in
[`docs/phase-1-open-problems.md`](docs/phase-1-open-problems.md); it is not
silently simulated by this repository.
