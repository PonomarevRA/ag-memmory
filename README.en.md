# AgMemory

[Русский](README.md) · English · [Install with an agent](INSTALL-AGENT.md#english)

AgMemory is local long-term memory for AI agents. Save facts, outcomes and procedures, retrieve them through MCP, and explore them in a browser as a wiki or a graph. Storage and retrieval run locally without a model or API key.

Search loaded cards in the knowledge catalogue and open a procedure to see its steps. Agents in different projects that use the same local store and tenant can publish and discover shared solutions through their regular MCP connections. [How the shared library works](docs/knowledge-retrieval-and-lifecycle.md#shared-procedure-library-across-projects).

## Quick start

Check [GitHub Releases](https://github.com/PonomarevRA/ag-memmory/releases) for an installer matching your OS and CPU. If no installer is listed, run from source. You need Git, the .NET 10 SDK selected by [global.json](global.json), Node.js 22.12+ or 24 LTS, and npm. The first build downloads NuGet and npm dependencies.

```sh
git clone https://github.com/PonomarevRA/ag-memmory.git
cd ag-memmory
dotnet restore ag-memory.slnx
dotnet run --project src/AgMemory.Web/AgMemory.Web.csproj --launch-profile http
```

Open [http://localhost:5123](http://localhost:5123). The server binds to loopback. Stop it with Ctrl+C. A fresh store is empty. **Read Memory** opens the wiki, **Memory Graph** shows related entries, and **Connect** provides the agent setup guide.

On Apple silicon, build a local app with `bash scripts/package-macos-app.sh osx-arm64`, then open `artifacts/macos/osx-arm64/AgMemory.app`. The source-built app is ad hoc signed and is intended for local use. See [desktop distribution](docs/desktop/distribution.md) for platform and build details.

## Connect an AI agent

Open **Connect** in the app or give your agent [INSTALL-AGENT.md](INSTALL-AGENT.md#english). Web and MCP are separate processes. Choose distinct actor and client IDs for each client. Keep the intended storage path and scope consistent when you want clients to read the same records. The [AgMemory skill](skills/agmemory/SKILL.md) explains how to use memory and where to find the complete skill package.

Copy this to Codex, Cursor, or Claude:

> Install AgMemory locally from https://github.com/PonomarevRA/ag-memmory following INSTALL-AGENT.md. Preserve existing memory, backups, and MCP settings. Connect a separate AgMemory MCP server and verify a real memory_capabilities call. Report the app URL and the verification result.

## Data and updates

AgMemory keeps its data outside the repository and app bundle. The default data folder on macOS is `~/Library/Application Support/AgMemory`; a configured installation can use a different path. Check the app's storage settings before changing anything. Follow the [backup guide](docs/memory/backup-policy.md) before upgrading. Keep API keys in local settings or environment variables.

AgMemory source is under the [Apache 2.0 license](LICENSE). [Third-party notices](THIRD-PARTY-NOTICES.md) apply to bundled components.
