from __future__ import annotations

from dataclasses import dataclass
from threading import RLock
import subprocess
import time
from urllib.request import urlopen

from .manifest import WapkManifest
from .paths import exe_path, html_path
from .ports import find_free_port
from .settings import GlobalSettings
from .webview import launch_webview


@dataclass
class RunningApp:
    manifest: WapkManifest
    port: int
    backend: subprocess.Popen
    webview: subprocess.Popen | None


class AppRunner:
    def __init__(self, settings: GlobalSettings | None = None) -> None:
        self.running: dict[str, RunningApp] = {}
        self.lock = RLock()
        self.settings = settings or GlobalSettings.load()

    def is_running(self, app_id: str) -> bool:
        with self.lock:
            running = self.running.get(app_id)
            if not running:
                return False
            if running.backend.poll() is not None:
                self.running.pop(app_id, None)
                return False
            if running.webview is not None and running.webview.poll() is not None:
                self.stop(app_id)
                return False
            return True

    def start(self, manifest: WapkManifest) -> RunningApp:
        with self.lock:
            if self.is_running(manifest.id):
                return self.running[manifest.id]

        port = find_free_port(*manifest.port_range)
        backend = subprocess.Popen(
            [str(exe_path(manifest.id)), *manifest.args_for_port(port)],
            cwd=exe_path(manifest.id).parent,
            creationflags=_backend_creation_flags(self.settings),
        )

        try:
            self._wait_ready(manifest, port, backend)
            runtime = {
                "appId": manifest.id,
                "name": manifest.name,
                "version": manifest.version,
                "port": port,
                "apiBase": manifest.api_base_for_port(port),
            }
            webview = launch_webview(
                html_path(manifest.id),
                runtime,
                manifest.name,
                manifest.window,
                self.settings,
            )
        except Exception:
            _terminate(backend)
            raise

        running = RunningApp(manifest=manifest, port=port, backend=backend, webview=webview)
        with self.lock:
            self.running[manifest.id] = running
        return running

    def stop(self, app_id: str) -> None:
        with self.lock:
            running = self.running.pop(app_id, None)
        if not running:
            return
        if running.webview is not None:
            _terminate(running.webview)
        _terminate(running.backend)

    def stop_all(self) -> None:
        for app_id in list(self.running):
            self.stop(app_id)

    def statuses(self) -> dict[str, bool]:
        with self.lock:
            app_ids = list(self.running)
        return {app_id: self.is_running(app_id) for app_id in app_ids}

    def _wait_ready(self, manifest: WapkManifest, port: int, backend: subprocess.Popen) -> None:
        ready_url = manifest.ready_url_for_port(port)
        if not ready_url:
            time.sleep(0.4)
            if backend.poll() is not None:
                raise RuntimeError("앱 실행 파일이 바로 종료되었습니다.")
            return

        deadline = time.monotonic() + 15
        last_error: Exception | None = None
        while time.monotonic() < deadline:
            if backend.poll() is not None:
                raise RuntimeError("앱 실행 파일이 준비 전에 종료되었습니다.")
            try:
                with urlopen(ready_url, timeout=1) as response:
                    if 200 <= response.status < 500:
                        return
            except Exception as exc:
                last_error = exc
            time.sleep(0.25)
        raise RuntimeError(f"앱 준비 상태 확인 실패: {last_error}")


def _terminate(process: subprocess.Popen) -> None:
    if process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=3)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=3)


def _backend_creation_flags(settings: GlobalSettings) -> int:
    if not settings.show_backend_console and hasattr(subprocess, "CREATE_NO_WINDOW"):
        return subprocess.CREATE_NO_WINDOW
    return 0
