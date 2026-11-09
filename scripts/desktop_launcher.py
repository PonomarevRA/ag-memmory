"""Windows/Linux native owner for the bundled self-contained .NET service; macOS uses Swift."""

import json
import os
from pathlib import Path
import subprocess
import sys
import threading
import urllib.request
from urllib.parse import urlsplit

VERSION = '1.5.1'
READY = b'AGMEMORY_DESKTOP_URL='
ACTIVITY_SCRIPT = """(()=>{const panel=document.querySelector('.agent-activity');const chat=panel?.querySelector('input[data-setting=chat]');if(chat&&!chat.checked)chat.click();const restore=panel?.querySelector('[data-action=restore]');if(restore&&!restore.hidden)restore.click();})()"""


def local_origin(value):
    """Accept only the exact IPv4 loopback readiness origin emitted by our own child."""
    try:
        url = urlsplit(value)
        if url.scheme != 'http' or url.hostname != '127.0.0.1' or url.username or url.password or url.query or url.fragment or url.path != '/' or not 1 <= url.port <= 65535:
            return None
        return f'http://127.0.0.1:{url.port}/'
    except (ValueError, TypeError):
        return None


def data_directory():
    """Mirror the existing .NET ApplicationData default, preserving explicit environment and installer precedence."""
    from desktop_support import configured_data_dir
    override = os.environ.get('AGMEMORY_DATA_DIR')
    if override and override.strip():
        return Path(override).absolute()
    if sys.platform == 'win32':
        root = Path(os.environ.get('APPDATA') or Path.home() / 'AppData/Roaming')
    else:
        root = Path(os.environ.get('XDG_CONFIG_HOME') or Path.home() / '.config')
    return configured_data_dir('agmemory', root / 'AgMemory')


def open_activity(window, origin):
    """Run one fixed action only in our own local page; raw run_js preserves the application's no-eval CSP."""
    current = urlsplit(window.get_current_url() or '')
    expected = urlsplit(origin)
    if current.scheme != expected.scheme or current.netloc != expected.netloc or current.username or current.password:
        return
    # Recheck inside the main frame as navigation can race the native URL read.
    guard = 'if(window.location.origin===' + json.dumps(origin.rstrip('/')) + '){' + ACTIVITY_SCRIPT + '}'
    window.run_js(guard)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """A local identity check never follows a redirect to another service or network origin."""
    def redirect_request(self, request, file, code, message, headers, new_url):
        return None


def verify_identity(origin):
    """Bound the local response and verify both product and actual listening origin before showing the window."""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    with opener.open(origin + 'api/desktop/v1/widget', timeout=3) as response:
        raw = response.read(65_537)
    if len(raw) > 65_536:
        raise ValueError('Desktop identity response is too large')
    value = json.loads(raw)
    if value.get('schemaVersion') != 'ag-desktop/widget-v1' or value.get('appId') != 'agmemory' or value.get('origin') != origin.rstrip('/'):
        raise ValueError('Bundled service identity mismatch')


class Backend:
    """Own exactly one child and a bounded readiness buffer; no discovery or adoption of unrelated processes."""
    def __init__(self, executable):
        self.executable = Path(executable)
        self.process = None
        self.origin = None
        self.ready = threading.Event()

    def start(self):
        """Start committed bundled code and continuously drain stdout; failure stops only this owned child."""
        environment = dict(os.environ, AGMEMORY_DESKTOP_MODE='1', AGMEMORY_DESKTOP_EMBEDDED='1', AGMEMORY_DATA_DIR=str(data_directory()), AGMEMORY_DESKTOP_STDIN_EXIT='1')
        flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
        self.process = subprocess.Popen([str(self.executable)], cwd=self.executable.parent, env=environment,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, creationflags=flags)
        threading.Thread(target=self._read, daemon=True, name='backend-readiness').start()
        if not self.ready.wait(30) or self.origin is None or self.process.poll() is not None:
            self.stop()
            raise RuntimeError('The bundled AgMemory service did not become ready')
        try:
            verify_identity(self.origin)
        except Exception:
            self.stop()
            raise
        return self.origin

    def _read(self):
        """Discard ordinary server output and retain at most64KiB while matching one fixed readiness prefix."""
        pending = bytearray()
        try:
            while chunk := self.process.stdout.read1(4096):
                if self.origin is not None:
                    continue
                pending.extend(chunk)
                if len(pending) > 65_536:
                    del pending[:-65_536]
                while b'\n' in pending:
                    line, _, remaining = pending.partition(b'\n')
                    pending[:] = remaining
                    if line.startswith(READY):
                        origin = local_origin(line[len(READY):].decode('ascii', errors='replace').strip())
                        if origin:
                            self.origin = origin
                            self.ready.set()
        finally:
            self.ready.set()

    def stop(self):
        """Drain the sole owned child for six seconds, then kill it without an unbounded finalizer wait."""
        child = self.process
        if child is None or child.poll() is not None:
            return True
        try:
            child.stdin.close()
        except (BrokenPipeError, OSError):
            pass
        try:
            child.wait(timeout=6)
        except subprocess.TimeoutExpired:
            child.kill()
            try:
                child.wait(timeout=1)
            except subprocess.TimeoutExpired:
                return False
        return True


def main():
    """Bind native close/tray/system-end policy around a self-contained backend, preserving its fixed authority."""
    if sys.platform not in ('win32', 'linux'):
        raise RuntimeError('Use the native Swift application on macOS')
    import webview
    from desktop_support import bind_background
    from desktop_runtime.native_shutdown import bind_native_shutdown
    root = Path(getattr(sys, '_MEIPASS', Path(__file__).resolve().parent))
    backend = Backend(root / 'backend' / ('AgMemory.Web.exe' if os.name == 'nt' else 'AgMemory.Web'))
    origin = backend.start()
    background = None
    lifecycle = [None]
    try:
        window = webview.create_window('AgMemory', origin, width=1160, height=780, min_size=(900, 560), confirm_close=False, js_api=None)
        background = bind_background(window, app_id='agmemory', title='AgMemory', origin=origin,
                                     on_quit=lambda: lifecycle[0].request_quit(),
                                     open_activity=lambda: open_activity(window, origin),
                                     data_directory=data_directory(), current_version=VERSION)
        lifecycle[0] = bind_native_shutdown(window, backend.stop, on_closed=background.close, hide_on_close=background.hide_on_close)
        webview.start(gui='edgechromium' if os.name == 'nt' else 'qt')
    finally:
        if background:
            background.close()
        if lifecycle[0]:
            lifecycle[0]()
        backend.stop()



if __name__ == '__main__':
    main()
