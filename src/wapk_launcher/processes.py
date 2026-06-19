from __future__ import annotations

from pathlib import Path
import subprocess
import sys
import time


def terminate_processes_by_executable(executable: Path, timeout_seconds: float = 3.0) -> None:
    if sys.platform != "win32" or not executable.exists():
        return

    pids = _find_pids_by_executable(executable)
    for pid in pids:
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )

    deadline = time.monotonic() + timeout_seconds
    while time.monotonic() < deadline:
        if not _find_pids_by_executable(executable):
            return
        time.sleep(0.1)


def _find_pids_by_executable(executable: Path) -> list[int]:
    resolved = str(executable.resolve()).lower()
    escaped = resolved.replace("'", "''")
    script = (
        f"$target = '{escaped}'; "
        "Get-CimInstance Win32_Process | "
        "Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLowerInvariant() -eq $target } | "
        "ForEach-Object { $_.ProcessId }"
    )
    result = subprocess.run(
        ["powershell", "-NoProfile", "-Command", script],
        text=True,
        capture_output=True,
        check=False,
    )
    if result.returncode != 0:
        return []

    pids: list[int] = []
    for line in result.stdout.splitlines():
        try:
            pids.append(int(line.strip()))
        except ValueError:
            continue
    return pids
