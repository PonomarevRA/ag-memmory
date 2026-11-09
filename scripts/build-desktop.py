"""Build a target-native self-contained Windows/Linux payload; installer packaging is a separate shared step."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def build(runtime, output):
    """Require the matching native runner and an empty destination; never delete arbitrary output or user stores."""
    expected = {'win-x64': 'win32', 'linux-x64': 'linux'}
    if expected.get(runtime) != sys.platform:
        raise ValueError('Build each Windows/Linux payload on its matching native runner')
    output = output.absolute()
    if output.is_symlink() or output == Path(output.anchor) or output.exists() and any(output.iterdir()):
        raise ValueError('Output must be an empty non-symlink directory')
    root = Path(__file__).resolve().parent.parent
    manifest = json.loads((root / 'packaging/desktop-product.json').read_text())
    with tempfile.TemporaryDirectory(prefix='agm-desktop-build-') as temporary:
        staging = Path(temporary)
        backend = staging / 'backend'
        # Desktop runs beside other local services; workstation GC bounds per-process heap overhead.
        subprocess.run(['dotnet', 'publish', str(root / 'src/AgMemory.Web/AgMemory.Web.csproj'), '-c', 'Release', '-r', runtime,
                        '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:ServerGarbageCollection=false', '--output', str(backend)], check=True)
        # Ship the same separate MCP composition used by the native macOS bundle.
        subprocess.run(['dotnet', 'publish', str(root / 'src/AgMemory.McpServer/AgMemory.McpServer.csproj'), '-c', 'Release', '-r', runtime,
                        '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '--output', str(backend / 'mcp')], check=True)
        separator = ';' if os.name == 'nt' else ':'
        arguments = [sys.executable, '-m', 'PyInstaller', '--noconfirm', '--clean', '--windowed', '--onedir', '--name', manifest['binary'],
                     '--distpath', str(output), '--workpath', str(staging / 'work'), '--specpath', str(staging), '--paths', str(root / 'scripts'),
                     '--add-data', str(backend) + separator + 'backend', '--collect-all', 'webview', '--hidden-import', 'desktop_runtime.shutdown_platforms']
        if sys.platform == 'linux':
            arguments += ['--collect-all', 'PySide6', '--hidden-import', 'qtpy', '--hidden-import', 'webview.platforms.qt']
        else:
            arguments += ['--hidden-import', 'webview.platforms.winforms']
        arguments.append(str(root / 'scripts/desktop_launcher.py'))
        subprocess.run(arguments, check=True, env=dict(os.environ, QT_API='pyside6'))
        shutil.copy2(root / 'packaging/desktop-product.json', output / manifest['binary'] / 'desktop-product.json')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--runtime', choices=['win-x64', 'linux-x64'], required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    build(args.runtime, args.output)
