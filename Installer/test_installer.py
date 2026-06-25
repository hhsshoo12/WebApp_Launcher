from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import installer


class InstallerStateTests(unittest.TestCase):
    def make_installation(self, root: Path, *, marker: bool = True) -> Path:
        destination = root / "WebAppLauncher"
        destination.mkdir()
        (destination / "WebAppLauncher.exe").write_bytes(b"launcher")
        (destination / "WebAppLauncher.Bootstrapper.exe").write_bytes(b"bootstrapper")
        if marker:
            (destination / installer.INSTALL_STATE_FILE).write_text(
                json.dumps({"format": 1}),
                encoding="utf-8",
            )
        return destination

    def test_is_installation_dir_rejects_unrelated_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "WebAppLauncher.exe").write_bytes(b"launcher")
            self.assertFalse(installer.is_installation_dir(root))

    def test_find_existing_installation_prefers_registered_location(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            destination = self.make_installation(Path(temp))
            with (
                patch.object(installer, "read_registered_install_dir", return_value=destination),
                patch.object(installer, "default_install_dir", return_value=Path(temp) / "missing"),
            ):
                self.assertEqual(
                    installer.find_existing_installation(),
                    destination.resolve(),
                )

    def test_remove_installation_removes_program_files_not_webapp_data(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            destination = self.make_installation(root)
            (destination / "nested").mkdir()
            (destination / "nested" / "file.txt").write_text("data", encoding="utf-8")
            user_data = root / ".webapp" / "app" / "sample"
            user_data.mkdir(parents=True)
            (user_data / "data.txt").write_text("keep", encoding="utf-8")
            shortcut = root / "WebApp Launcher.lnk"
            shortcut.write_bytes(b"shortcut")
            progress: list[tuple[int, int, str]] = []

            with (
                patch.object(installer, "start_menu_shortcut", return_value=shortcut),
                patch.object(installer, "clear_registered_install_dir") as clear_registry,
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


if __name__ == "__main__":
    unittest.main()
