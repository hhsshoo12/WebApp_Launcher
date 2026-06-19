from pathlib import Path
import tempfile
import unittest
import zipfile

from wapk_launcher.manifest import ManifestError, WapkManifest


VALID_TOML = """
id = "mini-timetable"
name = "Mini Timetable"
version = "0.1.0"
repository = "example/mini-timetable"
ref = "main"
app_exe = "dist/app.exe"
app_html = "app.html"
args = ["--port", "{PORT}"]
port_range = [52000, 52500]
ready_url = "http://127.0.0.1:{PORT}/health"
api_base = "http://127.0.0.1:{PORT}"

[window]
borderless = true
fullscreen = false
transparent = true
level = "bottom"
"""


class ManifestTests(unittest.TestCase):
    def test_load_valid_manifest(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "app.wapk"
            path.write_text(VALID_TOML, encoding="utf-8")

            manifest = WapkManifest.load(path)

        self.assertEqual(manifest.id, "mini-timetable")
        self.assertEqual(manifest.repository, "example/mini-timetable")
        self.assertEqual(manifest.app_exe, "dist/app.exe")
        self.assertEqual(manifest.app_html, "app.html")
        self.assertEqual(manifest.args_for_port(52001), ["--port", "52001"])
        self.assertEqual(manifest.api_base_for_port(52001), "http://127.0.0.1:52001")
        self.assertEqual(manifest.ready_url_for_port(52001), "http://127.0.0.1:52001/health")
        self.assertTrue(manifest.window.borderless)
        self.assertFalse(manifest.window.fullscreen)
        self.assertTrue(manifest.window.transparent)
        self.assertEqual(manifest.window.level, "bottom")

    def test_rejects_zip_wapk(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            path = Path(temp_dir) / "legacy.wapk"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr(
                    "metadata.toml",
                    "repository = 'example/legacy'\nref = 'main'\n",
                )

            manifest = WapkManifest.load(path)

        self.assertEqual(manifest.repository, "example/legacy")

    def test_rejects_repository_url(self) -> None:
        data = VALID_TOML.replace('repository = "example/mini-timetable"', 'repository = "https://github.com/example/mini-timetable"')

        with self.assertRaises(ManifestError):
            WapkManifest.from_dict(__import__("tomllib").loads(data))

    def test_rejects_invalid_port_range(self) -> None:
        data = VALID_TOML.replace("port_range = [52000, 52500]", "port_range = [52500, 52000]")

        with self.assertRaises(ManifestError):
            WapkManifest.from_dict(__import__("tomllib").loads(data))

    def test_rejects_invalid_window_level(self) -> None:
        data = VALID_TOML.replace('level = "bottom"', 'level = "desktop"')

        with self.assertRaises(ManifestError):
            WapkManifest.from_dict(__import__("tomllib").loads(data))


if __name__ == "__main__":
    unittest.main()
