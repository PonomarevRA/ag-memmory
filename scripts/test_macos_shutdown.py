"""Compile and exercise the pure Swift deadline without closing an app or the OS session."""

from pathlib import Path
import os
import subprocess
import tempfile


def main():
    """Run fast-exit, repeated-request and expired-deadline checks in an isolated executable."""
    root = Path(__file__).resolve().parents[1]
    with tempfile.TemporaryDirectory(prefix='agmemory-shutdown-') as directory:
        work = Path(directory)
        (work / 'main.swift').write_bytes((root / 'tests/native/TerminationDeadlineTests.swift').read_bytes())
        environment = dict(os.environ)
        environment.setdefault('DEVELOPER_DIR', '/Library/Developer/CommandLineTools')
        # An Intel Python under Rosetta must not select x86 xcrun with an ARM-only CLT.
        library = Path(environment['DEVELOPER_DIR']) / 'usr/lib/libxcrun.dylib'
        description = subprocess.run(['/usr/bin/file', str(library)], capture_output=True, text=True).stdout
        arm_only = 'arm64' in description and 'x86_64' not in description
        tool = ['/usr/bin/arch', '-arm64', '/usr/bin/xcrun'] if arm_only else ['xcrun']
        subprocess.run(tool + ['swiftc', '-swift-version', '5', '-module-cache-path', str(work / 'modules'),
                        str(root / 'scripts/macos/TerminationDeadline.swift'), str(work / 'main.swift'),
                        '-o', str(work / 'deadline-test')], env=environment, check=True)
        subprocess.run([str(work / 'deadline-test')], check=True, timeout=5)


if __name__ == '__main__':
    main()
