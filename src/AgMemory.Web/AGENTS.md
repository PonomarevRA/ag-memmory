# AgMemory.Web

| Setting | Value |
|---------|-------|
| **Interactivity Mode** | Server |
| **Interactivity Scope** | Global |

## Rendering configuration

This project uses global Interactive Server with prerendering. It was created with
`dotnet new blazor -int Server -ai` because chat, local visit history and UI settings require a
single interactive circuit across the application.

All pages are interactive by default through `<Routes @rendermode="InteractiveServer" />` in
`Components/App.razor`. Do not add a page-level render mode.

## Component and browser rules

- Keep routable features in `Features/` and shared UI primitives in `UiKit/`.
- Components execute on the server through SignalR. Never inject `HttpContext` into an interactive
  component and never pass provider credentials or authorisation policy to the browser.
- Browser APIs are available only through a typed `IJSRuntime` wrapper and Vite-built modules under
  `client/src/` (published to `wwwroot/dist/`). Call interop only after `OnAfterRenderAsync` or from an
  event handler; dispose modules asynchronously and tolerate a disconnected circuit.
- Before `dotnet build` or `dotnet run`, the Web project runs `npm ci`, `npm run build`, and `npm test`
  in `client/` when `package.json` is present. Use `npm run dev` there for watch rebuilds during UI work.
- Local visit/thread data is UX state, not durable AgMemory memory. It must never contain credentials,
  scope values, raw query text in the visit log, or implicit `Remember` payloads.

## Data and gateway rules

- The model gateway is server-only and provider-neutral. Its OpenAI-compatible adapter reads only the
  `ModelGateway` configuration section; do not log or render configuration values or exceptions.
- An unconfigured gateway returns a safe unavailable state. Never manufacture a successful answer in
  disabled mode.
