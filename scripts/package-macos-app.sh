#!/usr/bin/env bash
set -euo pipefail

task_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
runtime_id=${1:-osx-arm64}
read_product_version() {
  sed -n 's/.*<AgMemoryProductVersion>\([^<]*\)<\/AgMemoryProductVersion>.*/\1/p' "$task_root/Directory.Build.props" | head -n 1
}
version=${AGMEMORY_VERSION:-$(read_product_version)}
if [[ -z "$version" ]]; then
  echo "Unable to resolve AgMemoryProductVersion. Set AGMEMORY_VERSION or Directory.Build.props." >&2
  exit 1
fi
output_root="${AGMEMORY_PACKAGE_OUTPUT_ROOT:-$task_root/artifacts/macos/$runtime_id}"
publish_root="$output_root/publish"
app_bundle="$output_root/AgMemory.app"
restore_workspace_on_exit=false
icon_source="$task_root/src/AgMemory.Web/wwwroot/assets/brand/agmemory-icon.svg"

restore_workspace() {
  exit_code=$?
  if [[ "$restore_workspace_on_exit" == true ]]; then
    dotnet restore "$task_root/ag-memory.slnx" >/dev/null || true
  fi

  exit "$exit_code"
}

trap restore_workspace EXIT

case "$runtime_id" in
  osx-arm64|osx-x64) ;;
  *)
    echo "Supported macOS runtime identifiers: osx-arm64, osx-x64." >&2
    exit 64
    ;;
esac

create_application_icon() {
  local iconset="$output_root/AppIcon.iconset"
  local icon_path="$app_bundle/Contents/Resources/AppIcon.icns"

  if [[ ! -f "$icon_source" ]]; then
    echo "Missing application icon source: $icon_source" >&2
    exit 1
  fi

  mkdir -p "$iconset"

  render_icon() {
    local size="$1"
    local name="$2"
    sips -s format png -z "$size" "$size" "$icon_source" --out "$iconset/$name" >/dev/null
  }

  render_icon 16 icon_16x16.png
  render_icon 32 icon_16x16@2x.png
  render_icon 32 icon_32x32.png
  render_icon 64 icon_32x32@2x.png
  render_icon 128 icon_128x128.png
  render_icon 256 icon_128x128@2x.png
  render_icon 256 icon_256x256.png
  render_icon 512 icon_256x256@2x.png
  render_icon 512 icon_512x512.png
  render_icon 1024 icon_512x512@2x.png
  iconutil -c icns "$iconset" -o "$icon_path"
  rm -rf "$iconset"
}

# An explicit output must be empty: never remove an arbitrary caller-supplied directory.
if [[ -n "${AGMEMORY_PACKAGE_OUTPUT_ROOT:-}" ]]; then
  if [[ "$output_root" != /* || -L "$output_root" || "$output_root" == "/" ]]; then
    echo "AGMEMORY_PACKAGE_OUTPUT_ROOT must be an absolute, non-symlink directory." >&2; exit 64
  fi
  if [[ -d "$output_root" ]] && [[ -n "$(find "$output_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
    echo "AGMEMORY_PACKAGE_OUTPUT_ROOT must be empty." >&2; exit 64
  fi
else
  rm -rf "$output_root"
fi
mkdir -p "$publish_root" "$app_bundle/Contents/MacOS" "$app_bundle/Contents/Resources"
cp "$task_root/scripts/macos-backups.py" "$app_bundle/Contents/Resources/macos-backups.py"
mkdir -p "$app_bundle/Contents/Resources/desktop_support"
cp "$task_root/scripts/desktop_support/"*.py "$app_bundle/Contents/Resources/desktop_support/"

dotnet restore "$task_root/src/AgMemory.Web/AgMemory.Web.csproj" \
  --runtime "$runtime_id" \
  -p:TargetFramework=net10.0
restore_workspace_on_exit=true

# Desktop-only GC policy: preserve the ordinary Web server publish defaults.
dotnet publish "$task_root/src/AgMemory.Web/AgMemory.Web.csproj" \
  --configuration Release \
  --runtime "$runtime_id" \
  --self-contained true \
  --no-restore \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  -p:ServerGarbageCollection=false \
  -p:Version="$version" \
  --output "$publish_root"

ditto "$publish_root" "$app_bundle/Contents/MacOS"
# MCP is a separate composition. Keep its appsettings/dependencies isolated from the Web host.
dotnet publish "$task_root/src/AgMemory.McpServer/AgMemory.McpServer.csproj" \
  --configuration Release --runtime "$runtime_id" --self-contained true \
  -p:PublishSingleFile=false -p:PublishTrimmed=false -p:Version="$version" \
  --output "$app_bundle/Contents/MacOS/mcp"
create_application_icon
sed "s/@VERSION@/$version/g" "$task_root/scripts/macos/Info.plist.template" > "$app_bundle/Contents/Info.plist"
chmod +x "$app_bundle/Contents/MacOS/AgMemory.Web"
# Keep the server binary intact for CLI integrations; only the app's executable is the native window owner.
case "$runtime_id" in osx-arm64) native_arch=arm64 ;; osx-x64) native_arch=x86_64 ;; esac
native_developer_dir="${DEVELOPER_DIR:-/Library/Developer/CommandLineTools}"
DEVELOPER_DIR="$native_developer_dir" xcrun swiftc -swift-version 5 -O \
  -module-cache-path "$output_root/swift-module-cache" -target "$native_arch-apple-macosx13.0" -framework AppKit -framework WebKit \
  "$task_root/scripts/macos/AgMemoryDesktop.swift" "$task_root/scripts/macos/TerminationDeadline.swift" "$task_root/scripts/macos/DesktopInstallation.swift" \
  "$task_root/scripts/macos/DesktopBackground.swift" "$task_root/scripts/macos/DesktopUpdates.swift" -o "$app_bundle/Contents/MacOS/AgMemoryDesktop"

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$app_bundle"
fi

ditto -c -k --sequesterRsrc --keepParent "$app_bundle" "$output_root/AgMemory-$version-$runtime_id.zip"
dotnet restore "$task_root/ag-memory.slnx"
restore_workspace_on_exit=false

printf 'Created %s\n' "$app_bundle"
printf 'Created %s\n' "$output_root/AgMemory-$version-$runtime_id.zip"
