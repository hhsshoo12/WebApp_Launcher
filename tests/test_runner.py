import unittest

from wapk_launcher.manifest import WapkManifest, WindowOptions
from wapk_launcher.runner import AppRunner, RunningApp


class FakeProcess:
    def __init__(self, returncode: int | None = None) -> None:
        self.returncode = returncode
        self.terminated = False
        self.killed = False

    def poll(self) -> int | None:
        return self.returncode

    def terminate(self) -> None:
        self.terminated = True
        self.returncode = 0

    def wait(self, timeout: int | None = None) -> int:
        return self.returncode or 0

    def kill(self) -> None:
        self.killed = True
        self.returncode = 1


class RunnerTests(unittest.TestCase):
    def test_webview_close_stops_backend_and_clears_running_state(self) -> None:
        manifest = WapkManifest(
            id="test-app",
            name="Test App",
            version="1.0.0",
            repository="example/test-app",
            ref="main",
            app_exe="dist/app.exe",
            app_html="app.html",
            args=("--port", "{PORT}"),
            port_range=(52000, 52500),
            ready_url=None,
            api_base="http://127.0.0.1:{PORT}",
            window=WindowOptions(),
        )
        backend = FakeProcess()
        webview = FakeProcess(returncode=0)
        runner = AppRunner()
        runner.running[manifest.id] = RunningApp(
            manifest=manifest,
            port=52000,
            backend=backend,
            webview=webview,
        )

        self.assertFalse(runner.is_running(manifest.id))
        self.assertTrue(backend.terminated)
        self.assertNotIn(manifest.id, runner.running)


if __name__ == "__main__":
    unittest.main()
