from __future__ import annotations

import base64
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from .config import INSTALL_STATE_FILE
from .state import (
    bootstrapper_storage_path,
    clear_registered_install_dir,
    default_install_dir,
    read_registered_install_dir,
    start_menu_shortcut,
)


def copy_setup_to_bootstrapper_storage(source: Path | None = None) -> Path:
    """Copy the running Setup.exe to the persistent bootstrapper storage."""
    target = bootstrapper_storage_path()
    target.parent.mkdir(parents=True, exist_ok=True)
    origin = source or Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve()
    if not origin.is_file():
        raise FileNotFoundError(f"Setup.exe 원본을 찾을 수 없습니다: {origin}")
    if origin.resolve() == target.resolve():
        return target
    temp_target = target.with_suffix(target.suffix + ".new")
    shutil.copy2(origin, temp_target)
    os.replace(temp_target, target)
    return target


def wait_for_launcher_exit(timeout_seconds: float = 15.0) -> bool:
    """Wait until no WebAppLauncher.exe processes remain."""
    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if not find_running_launcher_processes():
            return True
        time.sleep(0.25)
    return not find_running_launcher_processes()


def move_staging_into_install_dir(staging: Path, install_dir: Path) -> None:
    """Move the contents of ``staging`` into ``install_dir``.

    Callers are expected to have stopped the launcher (and any other process
    that holds files inside ``install_dir`` open) before invoking this.
    Stale files that are not part of the new payload are left untouched so
    user-installed runtime bundles and shortcuts survive a launcher-only
    update. The staging directory is removed when the move completes.
    """
    install_dir.mkdir(parents=True, exist_ok=True)
    try:
        for entry in staging.iterdir():
            target = install_dir / entry.name
            if target.exists():
                if target.is_dir() and not target.is_symlink():
                    shutil.rmtree(target, ignore_errors=True)
                else:
                    target.unlink(missing_ok=True)
            shutil.move(str(entry), str(target))
    finally:
        shutil.rmtree(staging, ignore_errors=True)


def is_installation_dir(path: Path) -> bool:
    return (
        path.is_absolute()
        and (path / "WebAppLauncher.exe").is_file()
        and (path / INSTALL_STATE_FILE).is_file()
    )


def find_existing_installation() -> Path | None:
    candidates = [read_registered_install_dir(), default_install_dir()]
    seen: set[str] = set()
    for candidate in candidates:
        if candidate is None:
            continue
        normalized = os.path.normcase(os.path.abspath(candidate))
        if normalized in seen:
            continue
        seen.add(normalized)
        if is_installation_dir(candidate):
            return candidate.resolve()
    return None


def remove_installation(destination: Path, progress: object | None = None) -> None:
    if not is_installation_dir(destination):
        raise ValueError("WebApp Launcher 설치 폴더로 확인되지 않아 삭제하지 않았습니다.")

    entries = sorted(
        destination.rglob("*"),
        key=lambda path: (len(path.parts), path.is_dir()),
        reverse=True,
    )
    total = len(entries) + 1
    completed = 0
    for entry in entries:
        if entry.is_dir() and not entry.is_symlink():
            entry.rmdir()
        else:
            entry.unlink(missing_ok=True)
        completed += 1
        if callable(progress):
            progress(completed, total, entry.name)
    destination.rmdir()
    start_menu_shortcut().unlink(missing_ok=True)
    clear_registered_install_dir()
    if callable(progress):
        progress(total, total, destination.name)


def create_shortcut(shortcut: Path, target: Path, working_dir: Path) -> None:
    shortcut.parent.mkdir(parents=True, exist_ok=True)
    script = (
        "$shell = New-Object -ComObject WScript.Shell\n"
        f"$shortcut = $shell.CreateShortcut('{escape_powershell(str(shortcut))}')\n"
        f"$shortcut.TargetPath = '{escape_powershell(str(target))}'\n"
        f"$shortcut.WorkingDirectory = '{escape_powershell(str(working_dir))}'\n"
        f"$shortcut.IconLocation = '{escape_powershell(str(target))},0'\n"
        "$shortcut.Save()\n"
    )
    encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
        check=True,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )


def escape_powershell(value: str) -> str:
    return value.replace("'", "''")


def webapp_root() -> Path:
    return Path.home() / ".webapp"


def find_running_launcher_processes() -> list[int]:
    """Return PIDs of running WebAppLauncher.exe processes (excluding the current process)."""
    current_pid = os.getpid()
    pids: list[int] = []
    try:
        result = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq WebAppLauncher.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=False,
        )
        for line in result.stdout.splitlines():
            line = line.strip()
            if not line or not line.startswith('"'):
                continue
            parts = [part.strip('"') for part in line.split('","')]
            if len(parts) < 2:
                continue
            try:
                pid = int(parts[1])
                if pid != current_pid:
                    pids.append(pid)
            except ValueError:
                continue
    except FileNotFoundError:
        pass
    return pids


def kill_running_launcher_processes() -> bool:
    """Terminate running WebAppLauncher.exe processes. Returns True if successful."""
    pids = find_running_launcher_processes()
    if not pids:
        return True
    try:
        subprocess.run(
            ["taskkill", "/F", "/IM", "WebAppLauncher.exe"],
            capture_output=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=False,
        )
        return len(find_running_launcher_processes()) == 0
    except FileNotFoundError:
        return False
