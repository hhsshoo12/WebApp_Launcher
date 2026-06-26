from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import installer


def make_installation(root: Path, *, marker: bool = True) -> Path:
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


class InstallerStateTests(unittest.TestCase):
    def make_installation(self, root: Path, *, marker: bool = True) -> Path:
        return make_installation(root, marker=marker)

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


class FakeWinreg:
    """Tiny in-memory stand-in for the winreg module used in tests."""

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
    def make_installation(self, root: Path, *, marker: bool = True) -> Path:
        return make_installation(root, marker=marker)

    def test_register_associations_writes_user_classes(self) -> None:
        fake = FakeWinreg()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            destination = self.make_installation(root)
            with patch.object(installer, "winreg", fake), \
                 patch.object(installer, "_notify_shell_associations_changed") as notify:
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
            root = Path(temp)
            destination = root / "WebAppLauncher"
            destination.mkdir()
            with patch.object(installer, "winreg", fake):
                result = installer.register_file_associations(destination)
        self.assertFalse(result)
        self.assertEqual(fake.trees, {})

    def test_unregister_associations_removes_keys(self) -> None:
        fake = FakeWinreg()
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            destination = self.make_installation(root)
            with patch.object(installer, "winreg", fake), \
                 patch.object(installer, "_notify_shell_associations_changed"):
                installer.register_file_associations(destination)
                installer.unregister_file_associations()
        for ext in installer.ASSOC_EXTENSIONS:
            self.assertNotIn(
                f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{ext}", fake.trees
            )
        for progid in (installer.ASSOC_WAPK_PROGID, installer.ASSOC_WEBAPP_PROGID):
            self.assertNotIn(
                f"HKCU\\{installer.ASSOC_CLASSES_ROOT}\\{progid}", fake.trees
            )


if __name__ == "__main__":
    unittest.main()
