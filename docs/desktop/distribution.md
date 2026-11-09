# AgMemory desktop distribution

AgMemory uses native AppKit/WKWebView on macOS and a bundled pywebview launcher on Windows/Linux. The launcher owns one bundled self-contained .NET child. Reopening the window restores that child; it does not start a second service.

## Locations and compatibility

The installer writes `ag-desktop/install-v1` settings outside the replaceable app:

- macOS: `~/Library/Application Support/AGDesktop/agmemory/installation.json`
- Windows: `%LOCALAPPDATA%/AGDesktop/agmemory/installation.json`
- Linux: `${XDG_CONFIG_HOME:-~/.config}/ag-desktop/agmemory/installation.json`

Data, backup and application roots must be absolute, canonical, separate, and outside repositories and replaceable bundles. Existing CLI/environment data and backup overrides win. Without a locator, the existing.NET `ApplicationData/AgMemory` default remains. Invalid settings stop startup visibly; the reader does not select an empty replacement store. Web and MCP journal identity use the same resolver. No actor or scope is changed.

The installer preserves existing locations. It refuses moving a nonempty store: relocation requires a separate reviewed migration. Runtime storage menus display folders and never migrate live data. `scripts/macos-backups.py` preserves explicit arguments and environment overrides, then uses installer defaults. Backup/restore still requires all clients offline.

The preferred desktop port defaults to 8891. Installer ports are 1024..65535; explicit `AGMEMORY_DESKTOP_PORT=0` requests an ephemeral port for isolated runs. An occupied preferred port falls back to a newly owned ephemeral listener, never to the existing occupant. Final startup readiness contains the actual bound origin; configure widgets with that port. A bind race after the preflight probe fails startup safely.

## Background lifecycle

Window close hides only when `closeToTray` is enabled and a real native status/tray item is available. Otherwise it retains close-to-quit behavior. The tray offers Open, Agent requests, Check for Updates, Storage Locations, and Quit. Quit, Cmd-Q, SIGTERM and OS session end always use bounded shutdown; close-to-tray never overrides them. Login startup is only an explicit installer option.

The Swift owner preserves the existing 7 second shutdown deadline and common/modal RunLoop completion. Windows/Linux use the vendored shared 8 second owner deadline and 6 second host drain. Their private inherited stdin pipe is observed only in embedded mode: owner EOF requests `StopApplication()`, followed by a bounded forced kill if drain fails. There is no HTTP shutdown endpoint or arbitrary command protocol.

Update checks run only on explicit user action. Swift reads at most 256KiB from the pinned official GitHub latest-release API, refuses redirects/drafts/prereleases, and opens only the matching macOS architecture's official installer URL after another explicit click. The portable shared adapter opens the pinned official release page after confirmation. No downloaded code is executed, no release body becomes a command, and no automatic update is performed.

## Widget snapshots

`GET /api/desktop/v1/widget` returns `ag-desktop/widget-v1` with app identity/version, UTC capture time, actual origin and source coverage. Up to 8 latest operations are folded from 64 authorized activity journal events. Strings are redacted and bounded to 512 characters; the complete WebJSON response is capped at 60KiB (under the consumer 64KiB cap) by retaining fewer newest rows and explicitly reporting omissions when needed. Captured agent labels are explicitly declared identities; client/session labels are not verified subagents. Empty and unavailable are distinct.

The read-only endpoint accepts only loopback peers and localhost/loopback Host with same-origin browser requests when Origin is present. No query parameters select paths, authority or scope. Widget polling does not append activity and does not load the graph or memory bodies. It is independent of any native system widget registration.

## Local builds

```sh
bash scripts/package-macos-app.sh osx-arm64
python packaging/build_installer.py --manifest packaging/desktop-product.json --source artifacts/macos/osx-arm64/AgMemory.app --output artifacts/installers
```

Use `AGMEMORY_PACKAGE_OUTPUT_ROOT=/absolute/empty/directory` for isolated builds. The script refuses a nonempty explicit destination.

On matching Windows/Linux runners, install build dependencies from `packaging/requirements-desktop.txt`, then:

```sh
python scripts/build-desktop.py --runtime win-x64 --output artifacts/native
# On Linux use --runtime linux-x64 instead.
python packaging/build_installer.py --manifest packaging/desktop-product.json --source artifacts/native/AgMemoryDesktop --output artifacts/installers
```

Each payload includes its Python launcher, GUI dependencies and published.NET service. End users do not install Python or.NET. Windows still requires Microsoft's WebView2 runtime and ordinary Windows system components; Linux requires normal desktop/glibc/display system libraries. Linux bundles QtWebEngine and uses the available desktop tray, with close-to-quit fallback when none exists. The setup is built for the native target, not cross-compiled on macOS.

The workflow is manual and uploads CI artifacts only; it does not publish GitHub releases. Current LanceDB 2.5.0 native assets cover macOS ARM64, Windows x64 and Linux x64. A macOS x64 graph-backed release is blocked until a matching native binding exists. Windows/Linux build and runtime behavior require execution on their native CI runners; local macOS validation does not prove them.

macOS builds are ad-hoc signed and not notarized. The public source includes the local snapshot endpoint. The optional WidgetKit companion is distributed separately from the public AgMemory source. Local installer creation performs no production installation or migration.

## Evidence boundaries

Focused tests cover malformed/oversized locators, path overlap/repository/symlink aliases, port occupancy, loopback/origin guards, bounded/redacted journal-only snapshots, private owner-pipe lifecycle, strict release versions/URLs/architecture and close-to-tray policy. Native self-checks retain toolbar and termination invariants. Real menu/tray interaction requires native UI acceptance; pure tests do not establish OS registration or startup-at-login behavior.
