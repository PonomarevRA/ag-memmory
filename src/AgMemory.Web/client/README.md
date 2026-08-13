# AgMemory Web Client

Vite + TypeScript browser modules for the Blazor Interactive Server host.

## Summary

The .NET host owns routing, authorization and API endpoints. This package owns every
browser-facing fetch, sanitization boundary and local UI persistence module that Blazor
loads through `IJSRuntime.import('/dist/<feature>.js')`.

## Layout

| Path | Responsibility |
|------|----------------|
| `src/shared/` | Cross-feature fetch helpers, text limits, antiforgery headers |
| `src/features/chat/` | Streaming POST to `/api/chat` (Yuki / local gateway) |
| `src/features/memory-reader/` | Safe DTO projection for wiki reader APIs |
| `src/features/memory-graph/` | Graph snapshot sanitization and renderer orchestration |
| `src/features/memory-status/` | Local memory counters from `/api/memory-status` |
| `src/features/navigation/` | Visit log, chat threads and preferences in `localStorage` |
| `src/layout/` | Blazor reconnect modal wiring |

Vendor renderers (`three.js`, graph universe) stay in `../wwwroot/vendor/` and are
imported through the `@vendor` alias — no CDN, no runtime network fetches inside
renderers.

## Commands

```bash
npm ci
npm run build    # writes ../wwwroot/dist/*.js
npm test         # vitest unit tests for sanitizers and local state
npm run dev      # watch rebuild while editing TypeScript
```

`dotnet build` on `AgMemory.Web` runs `npm ci && npm run build` automatically when
`node` is available.

## Privacy boundaries

Browser modules never receive durable memory ids, scope values, actor ids or provider
credentials. Every API response is re-validated in TypeScript before Blazor renders it.
