# AgMemory.Web

| Setting | Value |
|---------|-------|
| **Interactivity Mode** | Vite single-page application |
| **Interactivity Scope** | Browser-owned |

## Rendering configuration

The browser receives one Vite-built SPA from `wwwroot/dist`. Routes, layout and local UX state
are owned by TypeScript; ASP.NET Core owns only static hosting and `/api/*` contracts.

## Component and browser rules

- Keep browser features decomposed under `client/src/features/`, routes in `client/src/app/`, and
  shared browser utilities in `client/src/shared/`.
- Never pass provider credentials, storage paths, actor IDs or authorisation policy to the browser.
- Browser APIs are called directly by TypeScript; there is no server-rendered UI transport.
- Before `dotnet build` or `dotnet run`, the Web project runs `npm ci`, `npm run build`, and `npm test`
  in `client/` when `package.json` is present. Use `npm run dev` there for watch rebuilds during UI work.
- Local visit/thread data is UX state, not durable AgMemory memory. It must never contain credentials,
  scope values, raw query text in the visit log, or implicit `Remember` payloads.

## Data and gateway rules

- The model gateway is server-only and provider-neutral. Its OpenAI-compatible adapter reads only the
  `ModelGateway` configuration section; do not log or render configuration values or exceptions.
- An unconfigured gateway returns a safe unavailable state. Never manufacture a successful answer in
  disabled mode.
