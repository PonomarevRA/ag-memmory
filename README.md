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

`AgMemory.Web` is a separate Interactive Server host. It provides a Russian-first chat interface,
a first-party UI-kit, local visit/thread history, and project/agent-connection pages without adding
web or model-provider dependencies to Contracts or Core.

```bash
dotnet run --project src/AgMemory.Web/AgMemory.Web.csproj
```

The host starts with the model gateway disabled and shows an explicit unavailable state. To connect
an OpenAI-compatible server, set the server-side configuration only (never browser code or a checked-in
file): `ModelGateway__Mode=OpenAiCompatible`, `ModelGateway__Endpoint`, `ModelGateway__Model`, and
`ModelGateway__ApiKey`. This starter host permits a configured real model only on a loopback request
in the Development environment; production enablement requires a separately implemented authenticated
host. Requests carry antiforgery protection, are limited to 12 per minute per remote address, and send
an upstream output-token cap. Chat transcripts and navigation remain browser-local; they are not turned
into durable memories implicitly.

See [`docs/codebase-guide.md`](docs/codebase-guide.md) for feature ownership and
[`docs/ui-ux-design-brief.md`](docs/ui-ux-design-brief.md) for UI behaviour and privacy boundaries.

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
