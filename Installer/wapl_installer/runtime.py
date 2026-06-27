from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
from pathlib import Path

from .config import WEBVIEW2_BOOTSTRAPPER_URL
from .release import _http_download_to_file
from .system import webapp_root

try:
    import winreg
except ImportError:
    winreg = None


def install_runtime_bundle(staging: Path, destination_root: Path) -> None:
    destination_root.mkdir(parents=True, exist_ok=True)
    for name in ("runtime", "tools", "LICENSES", "runtime-manifest.toml"):
        source = staging / name
        target = destination_root / name
        if target.exists():
            if target.is_dir() and not target.is_symlink():
                shutil.rmtree(target, ignore_errors=True)
            else:
                target.unlink(missing_ok=True)
        shutil.move(str(source), str(target))
    shutil.rmtree(staging, ignore_errors=True)


def is_webview2_runtime_installed() -> bool:
    roots = [
        os.environ.get("PROGRAMFILES(X86)"),
        os.environ.get("PROGRAMFILES"),
        os.environ.get("LOCALAPPDATA"),
    ]
    for root in roots:
        if not root:
            continue
        base = Path(root) / "Microsoft" / "EdgeWebView" / "Application"
        if base.is_dir() and any(base.glob("*/msedgewebview2.exe")):
            return True

    if winreg is None:
        return False
    registry_roots = [
        (winreg.HKEY_CURRENT_USER, r"Software\Microsoft\EdgeUpdate\Clients"),
        (winreg.HKEY_CURRENT_USER, r"Software\WOW6432Node\Microsoft\EdgeUpdate\Clients"),
    ]
    try:
        registry_roots.extend(
            [
                (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\EdgeUpdate\Clients"),
                (winreg.HKEY_LOCAL_MACHINE, r"Software\WOW6432Node\Microsoft\EdgeUpdate\Clients"),
            ]
        )
    except AttributeError:
        pass

    for root, subkey in registry_roots:
        try:
            with winreg.OpenKey(root, subkey) as key:
                index = 0
                while True:
                    try:
                        child = winreg.EnumKey(key, index)
                    except OSError:
                        break
                    index += 1
                    try:
                        with winreg.OpenKey(key, child) as child_key:
                            name, _ = winreg.QueryValueEx(child_key, "name")
                            pv, _ = winreg.QueryValueEx(child_key, "pv")
                        if (
                            isinstance(name, str)
                            and "webview" in name.lower()
                            and isinstance(pv, str)
                            and pv.strip()
                        ):
                            return True
                    except OSError:
                        continue
        except OSError:
            continue
    return False


def install_webview2(progress: object | None = None) -> None:
    if is_webview2_runtime_installed():
        return

    with tempfile.TemporaryDirectory(prefix="wapl-webview2-") as temp:
        installer_path = Path(temp) / "MicrosoftEdgeWebView2Setup.exe"
        _http_download_to_file(
            WEBVIEW2_BOOTSTRAPPER_URL,
            installer_path,
            progress,
        )
        subprocess.run(
            [str(installer_path), "/silent", "/install"],
            check=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )

def is_runtime_installed() -> bool:
    """Check whether .webapp runtime or tools directories are populated."""
    root = webapp_root()
    for name in ("runtime", "tools"):
        path = root / name
        if path.is_dir():
            try:
                if any(path.iterdir()):
                    return True
            except OSError:
                pass
    return False


def remove_runtime_data(progress: object | None = None) -> None:
    """Remove .webapp/runtime and .webapp/tools directories."""
    root = webapp_root()
    targets = [root / "runtime", root / "tools"]
    for target in targets:
        if target.is_dir():
            shutil.rmtree(target, ignore_errors=True)
            if callable(progress):
                progress(target.name)


def remove_webapp_apps(progress: object | None = None) -> None:
    """Remove installed web apps under .webapp/app."""
    app_dir = webapp_root() / "app"
    if app_dir.is_dir():
        shutil.rmtree(app_dir, ignore_errors=True)
        if callable(progress):
            progress("app")
