# AgMemory Vite client

The browser is a single Vite SPA. It owns routing, layouts and browser-only history,
threads and preferences; ASP.NET Core owns `/api/*`, antiforgery validation, rate limits,
model gateway access and storage configuration.

`npm run build` emits the SPA to `../wwwroot/dist`. `npm test` runs the TypeScript test suite.
The browser obtains a same-origin request token from `/api/antiforgery` before posting chat data;
tokens, scopes, actor IDs, storage paths and provider credentials never enter browser state.
