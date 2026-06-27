from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import installer  # noqa: E402
from wapl_installer import release as installer_release  # noqa: E402
from wapl_installer import runtime as installer_runtime  # noqa: E402
from wapl_installer import state as installer_state  # noqa: E402
from wapl_installer import system as installer_system  # noqa: E402
from wapl_installer import ui as installer_ui  # noqa: E402


def make_installation(root: Path, *, marker: bool = True, version: str | None = None) -> Path:
    destination = root / "WebAppLauncher"
    destination.mkdir()
    (destination / "WebAppLauncher.exe").write_bytes(b"launcher")
    if marker:
        state: dict[str, object] = {
            "format": installer.INSTALL_STATE_FORMAT,
            "product": installer.PRODUCT_NAME,
            "version": version or "0.1.0",
            "install_location": str(destination.resolve()),
        }
        (destination / installer.INSTALL_STATE_FILE).write_text(
            json.dumps(state),
            encoding="utf-8",
        )
    return destination


class InstallerStateTests(unittest.TestCase):
    def test_is_installation_dir_rejects_unrelated_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "WebAppLauncher.exe").write_bytes(b"launcher")
            self.assertFalse(installer.is_installation_dir(root))

    def test_find_existing_installation_prefers_registered_location(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            destination = make_installation(Path(temp))
            with (
                patch.object(installer_system, "read_registered_install_dir", return_value=destination),
                patch.object(installer_system, "default_install_dir", return_value=Path(temp) / "missing"),
            ):
                self.assertEqual(installer.find_existing_installation(), destination.resolve())

    def test_remove_installation_removes_program_files_not_webapp_data(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            destination = make_installation(root)
            (destination / "nested").mkdir()
            (destination / "nested" / "file.txt").write_text("data", encoding="utf-8")
            user_data = root / ".webapp" / "app" / "sample"
            user_data.mkdir(parents=True)
            (user_data / "data.txt").write_text("keep", encoding="utf-8")
            shortcut = root / "WebApp Launcher.lnk"
            shortcut.write_bytes(b"shortcut")
            progress: list[tuple[int, int, str]] = []

            with (
                patch.object(installer_system, "start_menu_shortcut", return_value=shortcut),
                patch.object(installer_system, "clear_registered_install_dir") as clear_registry,
            ):
                installer.remove_installation(
                    destination,
                    lambda current, total, item: progress.append((current, total, item)),
                )

            self.assertFalse(destination.exists())
            self.assertFalse(shortcut.exists())
            self.assertTrue((user_data / "data.txt").is_file())
            self.assertEqual(progress[-1][0], progress[-1][1])
            clear_registry.assert_called_once_with()

    def test_write_install_state_records_version_and_setup_path(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / "WebAppLauncher"
            destination.mkdir()
            setup_path = Path(temp) / "WebAppLauncher-Setup.exe"
            setup_path.write_bytes(b"setup")
            installer.write_install_state(destination, version="0.2.0", setup_path=setup_path)
            payload = json.loads((destination / installer.INSTALL_STATE_FILE).read_text(encoding="utf-8"))
            self.assertEqual(installer.INSTALL_STATE_FORMAT, payload["format"])
            self.assertEqual("0.2.0", payload["version"])
            self.assertEqual(str(destination.resolve()), payload["install_location"])
            self.assertEqual(str(setup_path.resolve()), payload["setup_path"])

    def test_maintenance_actions_have_running_process_helpers(self) -> None:
        self.assertIs(installer_ui.find_running_launcher_processes, installer_system.find_running_launcher_processes)
        self.assertIs(installer_ui.kill_running_launcher_processes, installer_system.kill_running_launcher_processes)

    def test_begin_install_logs_setup_version_and_starts_worker(self) -> None:
        class Flag:
            def __init__(self, value: bool) -> None:
                self.value = value

            def get(self) -> bool:
                return self.value

        class TextValue:
            def __init__(self) -> None:
                self.value = ""

            def set(self, value: str) -> None:
                self.value = value

        class Event:
            def __init__(self) -> None:
                self.cleared = False

            def clear(self) -> None:
                self.cleared = True

        class FakeThread:
            created: list["FakeThread"] = []

            def __init__(self, *, target, args, daemon) -> None:
                self.target = target
                self.args = args
                self.daemon = daemon
                self.started = False
                FakeThread.created.append(self)

            def start(self) -> None:
                self.started = True

        class App:
            def __init__(self) -> None:
                self.running = False
                self.start_menu = Flag(False)
                self.install_runtimes = Flag(False)
                self.file_associations = Flag(False)
                self.cancel_event = Event()
                self.log_messages: list[str] = []
                self.status = TextValue()

            def _reset_progress(self) -> None:
                pass

            def _show_page(self, page: int) -> None:
                self.page = page

            def _write_log(self, message: str) -> None:
                self.log_messages.append(message)

            def _install_worker(self, *_args) -> None:
                pass

        with tempfile.TemporaryDirectory() as temp, patch.object(installer_ui.threading, "Thread", FakeThread):
            app = App()
            installer_ui.InstallerApp._begin_install(app, Path(temp))

        self.assertTrue(app.running)
        self.assertTrue(app.cancel_event.cleared)
        self.assertEqual(3, app.page)
        self.assertIn(f"Setup 버전: v{installer_ui.SETUP_VERSION}", app.log_messages)
        self.assertEqual(1, len(FakeThread.created))
        self.assertTrue(FakeThread.created[0].started)


class FakeWinreg:
    HKEY_CURRENT_USER = "HKCU"
    REG_SZ = 1
    KEY_ALL_ACCESS = 0x000F003F

    def __init__(self) -> None:
        self.trees: dict[str, dict[str, object]] = {}

    def _resolve(self, parent, name: str) -> str:
        if isinstance(parent, FakeWinregKey):
            return f"{parent.path}\\{name}"
        return f"{parent}\\{name}"

    def CreateKey(self, parent, name: str) -> "FakeWinregKey":
        path = self._resolve(parent, name)
        self.trees.setdefault(path, {})
        return FakeWinregKey(self, path)

    def OpenKey(self, parent, name: str, *_args, **_kwargs) -> "FakeWinregKey":
        path = self._resolve(parent, name)
        if path not in self.trees:
            raise FileNotFoundError(path)
        return FakeWinregKey(self, path)

    def DeleteKey(self, parent, name: str) -> None:
        path = self._resolve(parent, name)
        if path in self.trees:
            del self.trees[path]

    def SetValueEx(self, key: "FakeWinregKey", value_name: str, _reserved, _type, value: object) -> None:
        key.values[value_name] = value
        self.trees[key.path] = key.values

    def QueryValueEx(self, key: "FakeWinregKey", value_name: str) -> tuple[object, int]:
        return (key.values[value_name], 0)

    def EnumKey(self, key: "FakeWinregKey", index: int) -> str:
        prefix = key.path + "\\"
        children = sorted(
            name[len(prefix):].split("\\", 1)[0]
            for name in self.trees
            if name.startswith(prefix)
        )
        if index >= len(children):
            raise OSError("no more keys")
        return children[index]


class FakeWinregKey:
    def __init__(self, owner: FakeWinreg, path: str) -> None:
        self.owner = owner
        self.path = path
        self.values: dict[str, object] = {}

    def __enter__(self) -> "FakeWinregKey":
        return self

    def __exit__(self, *_args) -> None:
        pass


class FileAssociationTests(unittest.TestCase):
    def test_register_associations_writes_user_classes(self) -> None:
        fake = FakeWinreg()
        with tempfile.TemporaryDirectory() as temp:
            destination = make_installation(Path(temp))
            with patch.object(installer_state, "winreg", fake), \
                 patch.object(installer_state, "_notify_shell_associations_changed") as notify:
                result = installer.register_file_associations(destination)
        self.assertTrue(result)
        for ext, progid in installer.ASSOC_EXTENSIONS.items():
            ext_key = fake.trees[f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{ext}"]
            self.assertEqual(ext_key[""], progid)
            progid_key = fake.trees[f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{progid}"]
            self.assertEqual(progid_key[""], installer.ASSOC_DESCRIPTIONS[progid])
            cmd_key = fake.trees[
                f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{progid}\\shell\\open\\command"
            ]
            self.assertEqual(cmd_key[""], f'"{destination}\\WebAppLauncher.exe" "%1"')
        notify.assert_called_once_with()

    def test_register_associations_skips_when_launcher_missing(self) -> None:
        fake = FakeWinreg()
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / "WebAppLauncher"
            destination.mkdir()
            with patch.object(installer_state, "winreg", fake):
                result = installer.register_file_associations(destination)
        self.assertFalse(result)
        self.assertEqual(fake.trees, {})

    def test_unregister_associations_removes_keys(self) -> None:
        fake = FakeWinreg()
        with tempfile.TemporaryDirectory() as temp:
            destination = make_installation(Path(temp))
            with patch.object(installer_state, "winreg", fake), \
                 patch.object(installer_state, "_notify_shell_associations_changed"):
                installer.register_file_associations(destination)
                installer.unregister_file_associations()
        for ext in installer.ASSOC_EXTENSIONS:
            self.assertNotIn(f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{ext}", fake.trees)
        for progid in (installer.ASSOC_WAPK_PROGID, installer.ASSOC_WEBAPP_PROGID):
            self.assertNotIn(f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{progid}", fake.trees)


class OnlineInstallTests(unittest.TestCase):
    def test_find_latest_launcher_release_returns_v_release_with_assets(self) -> None:
        fake_release = {
            "tag_name": "v0.2.0",
            "draft": False,
            "published_at": "2026-06-26T10:00:00Z",
            "assets": [
                {"name": "WAPL-Launcher-v0.2.0.zip", "browser_download_url": "https://example/zip"},
                {"name": "WAPL-Launcher-v0.2.0.zip.sha256", "browser_download_url": "https://example/sha"},
                {"name": "WAPL-Runtime-v0.1.zip", "browser_download_url": "https://example/runtime-zip"},
            ],
        }
        with patch.object(installer_release, "_http_get_json", return_value=[fake_release]):
            release = installer.find_latest_launcher_release()
        self.assertEqual("0.2.0", release["version"])
        self.assertEqual("https://example/zip", release["zip_url"])
        self.assertEqual("https://example/sha", release["checksum_url"])

    def test_find_latest_launcher_release_skips_runtime_and_drafts(self) -> None:
        with patch.object(
            installer_release,
            "_http_get_json",
            return_value=[
                {"tag_name": "runtime-v0.1", "draft": False, "published_at": "2026-06-30T00:00:00Z", "assets": []},
                {"tag_name": "v0.1.0", "draft": True, "published_at": "2026-06-29T00:00:00Z", "assets": []},
                {
                    "tag_name": "v0.3.0",
                    "draft": False,
                    "published_at": "2026-06-28T00:00:00Z",
                    "assets": [
                        {"name": "WAPL-Launcher-v0.3.0.zip", "browser_download_url": "https://example/zip-3"},
                        {"name": "WAPL-Launcher-v0.3.0.zip.sha256", "browser_download_url": "https://example/sha-3"},
                    ],
                },
            ],
        ):
            release = installer.find_latest_launcher_release()
        self.assertEqual("0.3.0", release["version"])

    def test_find_latest_launcher_release_skips_incomplete_newer_release(self) -> None:
        with patch.object(
            installer_release,
            "_http_get_json",
            return_value=[
                {
                    "tag_name": "v0.4.0",
                    "draft": False,
                    "published_at": "2026-06-29T00:00:00Z",
                    "assets": [
                        {"name": "WAPL-Launcher-v0.4.0.zip", "browser_download_url": "https://example/zip-4"},
                    ],
                },
                {
                    "tag_name": "v0.3.0",
                    "draft": False,
                    "published_at": "2026-06-28T00:00:00Z",
                    "assets": [
                        {"name": "WAPL-Launcher-v0.3.0.zip", "browser_download_url": "https://example/zip-3"},
                        {"name": "WAPL-Launcher-v0.3.0.zip.sha256", "browser_download_url": "https://example/sha-3"},
                    ],
                },
            ],
        ):
            release = installer.find_latest_launcher_release()
        self.assertEqual("0.3.0", release["version"])

    def test_find_latest_runtime_release_returns_runtime_asset(self) -> None:
        fake_release = {
            "tag_name": "runtime-v0.2",
            "draft": False,
            "published_at": "2026-06-26T10:00:00Z",
            "assets": [
                {"name": "WAPL-Runtime-v0.2.zip", "browser_download_url": "https://example/runtime.zip"},
                {"name": "WAPL-Runtime-v0.2.zip.sha256", "browser_download_url": "https://example/runtime.sha256"},
            ],
        }
        with patch.object(installer_release, "_http_get_json", return_value=[fake_release]):
            release = installer.find_latest_runtime_release()
        self.assertEqual("0.2", release["version"])
        self.assertEqual("https://example/runtime.zip", release["zip_url"])

    def test_find_latest_launcher_release_raises_when_no_assets(self) -> None:
        with patch.object(
            installer_release,
            "_http_get_json",
            return_value=[{"tag_name": "v0.1.0", "draft": False, "published_at": "2026-06-26T00:00:00Z", "assets": []}],
        ):
            with self.assertRaises(installer.NetworkError):
                installer.find_latest_launcher_release()

    def test_verify_sha256_accepts_matching_digest(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            archive = Path(temp) / "payload.zip"
            archive.write_bytes(b"hello launcher")
            expected = hashlib.sha256(b"hello launcher").hexdigest()
            installer.verify_sha256(archive, expected)

    def test_verify_sha256_rejects_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            archive = Path(temp) / "payload.zip"
            archive.write_bytes(b"hello launcher")
            with self.assertRaises(installer.NetworkError):
                installer.verify_sha256(archive, "0" * 64)

    def test_download_launcher_payload_verifies_and_extracts(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            staging_root = Path(temp) / "staging"
            staging_root.mkdir()
            payload_zip = Path(temp) / "WAPL-Launcher-v9.9.9.zip"
            with zipfile.ZipFile(payload_zip, "w") as archive:
                archive.writestr("WebAppLauncher.exe", b"launcher-bytes")
                archive.writestr("WebAppLauncher.Cli.exe", b"cli-bytes")
                archive.writestr("Ui/index.html", b"ui")
            real_hash = hashlib.sha256(payload_zip.read_bytes()).hexdigest()

            with (
                patch.object(
                    installer_release,
                    "find_latest_launcher_release",
                    return_value={
                        "version": "9.9.9",
                        "tag": "v9.9.9",
                        "zip_url": "https://example/zip",
                        "zip_name": payload_zip.name,
                        "checksum_url": "https://example/sha",
                    },
                ),
                patch.object(
                    installer_release,
                    "_http_download_to_file",
                    side_effect=lambda _url, dest, _on_progress=None: dest.write_bytes(payload_zip.read_bytes()),
                ),
                patch.object(installer_release, "_http_fetch_text", return_value=f"{real_hash}  {payload_zip.name}"),
            ):
                version, staging = installer.download_launcher_payload(staging_root)

            self.assertEqual("9.9.9", version)
            self.assertTrue((staging / "WebAppLauncher.exe").is_file())
            self.assertEqual(b"launcher-bytes", (staging / "WebAppLauncher.exe").read_bytes())
            self.assertFalse((staging / payload_zip.name).exists())

    def test_download_launcher_payload_rejects_incomplete_payload_layout(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            staging_root = Path(temp) / "staging"
            staging_root.mkdir()
            payload_zip = Path(temp) / "WAPL-Launcher-v9.9.9.zip"
            with zipfile.ZipFile(payload_zip, "w") as archive:
                archive.writestr("WebAppLauncher.exe", b"launcher-bytes")
            real_hash = hashlib.sha256(payload_zip.read_bytes()).hexdigest()

            with (
                patch.object(
                    installer_release,
                    "find_latest_launcher_release",
                    return_value={
                        "version": "9.9.9",
                        "tag": "v9.9.9",
                        "zip_url": "https://example/zip",
                        "zip_name": payload_zip.name,
                        "checksum_url": "https://example/sha",
                    },
                ),
                patch.object(
                    installer_release,
                    "_http_download_to_file",
                    side_effect=lambda _url, dest, _on_progress=None: dest.write_bytes(payload_zip.read_bytes()),
                ),
                patch.object(installer_release, "_http_fetch_text", return_value=f"{real_hash}  {payload_zip.name}"),
            ):
                with self.assertRaises(installer.NetworkError):
                    installer.download_launcher_payload(staging_root)

    def test_download_runtime_bundle_verifies_and_extracts(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            staging_root = Path(temp) / "staging"
            staging_root.mkdir()
            bundle_zip = Path(temp) / "WAPL-Runtime-v0.2.zip"
            with zipfile.ZipFile(bundle_zip, "w") as archive:
                archive.writestr("runtime/python313/python.exe", b"python")
                archive.writestr("tools/git/git.exe", b"git")
                archive.writestr("LICENSES/NOTICE.txt", b"licenses")
                archive.writestr("runtime-manifest.toml", b"[runtime]\nbundle_version = \"0.2\"\n")
            real_hash = hashlib.sha256(bundle_zip.read_bytes()).hexdigest()

            with (
                patch.object(
                    installer_release,
                    "find_latest_runtime_release",
                    return_value={
                        "version": "0.2",
                        "tag": "runtime-v0.2",
                        "zip_url": "https://example/runtime.zip",
                        "zip_name": bundle_zip.name,
                        "checksum_url": "https://example/runtime.sha256",
                    },
                ),
                patch.object(
                    installer_release,
                    "_http_download_to_file",
                    side_effect=lambda _url, dest, _on_progress=None: dest.write_bytes(bundle_zip.read_bytes()),
                ),
                patch.object(installer_release, "_http_fetch_text", return_value=f"{real_hash}  {bundle_zip.name}"),
            ):
                version, staging = installer.download_runtime_bundle(staging_root)

            self.assertEqual("0.2", version)
            self.assertTrue((staging / "runtime").is_dir())
            self.assertTrue((staging / "tools").is_dir())
            self.assertTrue((staging / "LICENSES").is_dir())
            self.assertTrue((staging / "runtime-manifest.toml").is_file())

    def test_download_launcher_payload_rejects_checksum_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            staging_root = Path(temp) / "staging"
            staging_root.mkdir()
            payload_zip = Path(temp) / "WAPL-Launcher-v0.0.1.zip"
            payload_zip.write_bytes(b"x")

            with (
                patch.object(
                    installer_release,
                    "find_latest_launcher_release",
                    return_value={
                        "version": "0.0.1",
                        "tag": "v0.0.1",
                        "zip_url": "https://example/zip",
                        "zip_name": payload_zip.name,
                        "checksum_url": "https://example/sha",
                    },
                ),
                patch.object(
                    installer_release,
                    "_http_download_to_file",
                    side_effect=lambda _url, dest, _on_progress=None: dest.write_bytes(payload_zip.read_bytes()),
                ),
                patch.object(installer_release, "_http_fetch_text", return_value="0" * 64),
            ):
                with self.assertRaises(installer.NetworkError):
                    installer.download_launcher_payload(staging_root)

    def test_install_runtime_bundle_replaces_runtime_tools_and_licenses(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / ".webapp"
            staging = Path(temp) / "staging"
            (staging / "runtime").mkdir(parents=True)
            (staging / "tools").mkdir()
            (staging / "LICENSES").mkdir()
            (staging / "runtime" / "python.txt").write_text("new", encoding="utf-8")
            (staging / "tools" / "git.txt").write_text("new", encoding="utf-8")
            (staging / "LICENSES" / "NOTICE.txt").write_text("new", encoding="utf-8")
            (staging / "runtime-manifest.toml").write_text("[runtime]\n", encoding="utf-8")
            (root / "runtime").mkdir(parents=True)
            (root / "runtime" / "old.txt").write_text("old", encoding="utf-8")

            installer_runtime.install_runtime_bundle(staging, root)

            self.assertFalse(staging.exists())
            self.assertFalse((root / "runtime" / "old.txt").exists())
            self.assertEqual("new", (root / "runtime" / "python.txt").read_text(encoding="utf-8"))
            self.assertTrue((root / "runtime-manifest.toml").is_file())

    def test_copy_setup_to_bootstrapper_storage_writes_expected_file(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            home = Path(temp)
            source = home / "Source-Setup.exe"
            source.write_bytes(b"setup-content")
            with patch.object(
                installer_system,
                "bootstrapper_storage_path",
                return_value=home / ".wapk" / "bootstrapper" / "WebAppLauncher-Setup.exe",
            ):
                target = installer.copy_setup_to_bootstrapper_storage(source)
            self.assertTrue(target.is_file())
            self.assertEqual("WebAppLauncher-Setup.exe", target.name)
            self.assertEqual(b"setup-content", target.read_bytes())


class MoveStagingTests(unittest.TestCase):
    def test_move_staging_into_install_dir_moves_files(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            staging = Path(temp) / "staging"
            install = Path(temp) / "install"
            staging.mkdir()
            (staging / "WebAppLauncher.exe").write_bytes(b"new")
            (staging / "Ui").mkdir()
            (staging / "Ui" / "index.html").write_text("ok", encoding="utf-8")
            installer.move_staging_into_install_dir(staging, install)
            self.assertTrue((install / "WebAppLauncher.exe").is_file())
            self.assertEqual("ok", (install / "Ui" / "index.html").read_text(encoding="utf-8"))
            self.assertFalse(staging.exists())


if __name__ == "__main__":
    unittest.main()
