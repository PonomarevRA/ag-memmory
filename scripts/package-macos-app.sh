#!/usr/bin/env bash
set -euo pipefail

task_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
runtime_id=${1:-osx-arm64}
version=${AGMEMORY_VERSION:-1.0.0}
output_root="$task_root/artifacts/macos/$runtime_id"
publish_root="$output_root/publish"
app_bundle="$output_root/AgMemory.app"
restore_workspace_on_exit=false

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

rm -rf "$output_root"
mkdir -p "$publish_root" "$app_bundle/Contents/MacOS" "$app_bundle/Contents/Resources"

dotnet restore "$task_root/src/AgMemory.Web/AgMemory.Web.csproj" \
  --runtime "$runtime_id" \
  -p:TargetFramework=net10.0
restore_workspace_on_exit=true

dotnet publish "$task_root/src/AgMemory.Web/AgMemory.Web.csproj" \
  --configuration Release \
  --runtime "$runtime_id" \
  --self-contained true \
  --no-restore \
  -p:PublishSingleFile=false \
  -p:PublishTrimmed=false \
  --output "$publish_root"

ditto "$publish_root" "$app_bundle/Contents/MacOS"
sed "s/@VERSION@/$version/g" "$task_root/scripts/macos/Info.plist.template" > "$app_bundle/Contents/Info.plist"
chmod +x "$app_bundle/Contents/MacOS/AgMemory.Web"

if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - "$app_bundle"
fi

ditto -c -k --sequesterRsrc --keepParent "$app_bundle" "$output_root/AgMemory-$version-$runtime_id.zip"
dotnet restore "$task_root/ag-memory.slnx"
restore_workspace_on_exit=false

printf 'Created %s\n' "$app_bundle"
printf 'Created %s\n' "$output_root/AgMemory-$version-$runtime_id.zip"
