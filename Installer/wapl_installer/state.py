from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path

from .config import (
    ASSOC_CLASSES_ROOT,
    ASSOC_DESCRIPTIONS,
    ASSOC_EXTENSIONS,
    ASSOC_WAPK_PROGID,
    ASSOC_WEBAPP_PROGID,
    BOOTSTRAPPER_STORAGE_DIR,
    BOOTSTRAPPER_STORAGE_FILENAME,
    INSTALL_STATE_FILE,
    INSTALL_STATE_FORMAT,
    PRODUCT_NAME,
    REGISTRY_INSTALL_VALUE,
    REGISTRY_KEY,
)

try:
    import winreg
except ImportError:
    winreg = None


def default_install_dir() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / "Programs" / "WebAppLauncher"
    return Path.home() / "AppData" / "Local" / "Programs" / "WebAppLauncher"


def start_menu_shortcut() -> Path:
    app_data = os.environ.get("APPDATA")
    if app_data:
        programs = Path(app_data) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    else:
        programs = Path.home() / "AppData" / "Roaming" / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    return programs / f"{PRODUCT_NAME}.lnk"


def bootstrapper_storage_path() -> Path:
    return BOOTSTRAPPER_STORAGE_DIR / BOOTSTRAPPER_STORAGE_FILENAME


def read_registered_install_dir() -> Path | None:
    if winreg is None:
        return None
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY) as key:
            value, _ = winreg.QueryValueEx(key, REGISTRY_INSTALL_VALUE)
    except OSError:
        return None
    return Path(value) if isinstance(value, str) and value.strip() else None


def read_install_state(destination: Path) -> dict | None:
    state_path = destination / INSTALL_STATE_FILE
    if not state_path.is_file():
        return None
    try:
        return json.loads(state_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def write_install_state(
    destination: Path,
    *,
    version: str,
    setup_path: Path | None = None,
) -> None:
    state: dict[str, object] = {
        "format": INSTALL_STATE_FORMAT,
        "product": PRODUCT_NAME,
        "version": version,
        "install_location": str(destination.resolve()),
    }
    if setup_path is not None:
        state["setup_path"] = str(setup_path.resolve())
    (destination / INSTALL_STATE_FILE).write_text(
        json.dumps(state, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    if winreg is not None:
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY) as key:
            winreg.SetValueEx(
                key,
                REGISTRY_INSTALL_VALUE,
                0,
                winreg.REG_SZ,
                str(destination.resolve()),
            )


def clear_registered_install_dir() -> None:
    if winreg is None:
        return
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY)
    except FileNotFoundError:
        pass


def _assoc_command(install_dir: Path) -> str:
    launcher = install_dir / "WebAppLauncher.exe"
    return f'"{launcher}" "%1"'


def register_file_associations(install_dir: Path) -> bool:
    if winreg is None:
        return False
    launcher = install_dir / "WebAppLauncher.exe"
    if not launcher.is_file():
        return False
    command = _assoc_command(install_dir)
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, ASSOC_CLASSES_ROOT) as root:
        for ext, progid in ASSOC_EXTENSIONS.items():
            with winreg.CreateKey(root, ext) as ext_key:
                winreg.SetValueEx(ext_key, "", 0, winreg.REG_SZ, progid)
            with winreg.CreateKey(root, progid) as progid_key:
                winreg.SetValueEx(
                    progid_key, "", 0, winreg.REG_SZ, ASSOC_DESCRIPTIONS[progid]
                )
                with winreg.CreateKey(progid_key, r"shell\open\command") as cmd_key:
                    winreg.SetValueEx(cmd_key, "", 0, winreg.REG_SZ, command)
    _notify_shell_associations_changed()
    return True


def unregister_file_associations() -> None:
    if winreg is None:
        return
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            ASSOC_CLASSES_ROOT,
            0,
            winreg.KEY_ALL_ACCESS,
        ) as root:
            for progid in (ASSOC_WAPK_PROGID, ASSOC_WEBAPP_PROGID):
                _delete_key_recursive(root, progid)
            for ext in ASSOC_EXTENSIONS:
                try:
                    winreg.DeleteKey(root, ext)
                except FileNotFoundError:
                    pass
    except FileNotFoundError:
        pass
    _notify_shell_associations_changed()


def _delete_key_recursive(root, subkey_path: str) -> None:
    try:
        with winreg.OpenKey(root, subkey_path, 0, winreg.KEY_ALL_ACCESS) as key:
            subkeys: list[str] = []
            index = 0
            while True:
                try:
                    subkeys.append(winreg.EnumKey(key, index))
                except OSError:
                    break
                index += 1
            for sub in subkeys:
                _delete_key_recursive(key, sub)
    except FileNotFoundError:
        return
    try:
        winreg.DeleteKey(root, subkey_path)
    except FileNotFoundError:
        pass


def _notify_shell_associations_changed() -> None:
    try:
        ctypes.windll.shell32.SHChangeNotify(0x08000000, 0x0000, 0, 0)
    except Exception:
        pass
