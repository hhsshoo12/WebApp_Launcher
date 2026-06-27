from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from tkinter import messagebox

from .config import INSTALL_STATE_FORMAT, PRODUCT_NAME, NetworkError
from .release import download_launcher_payload, find_latest_launcher_release
from .state import read_install_state, write_install_state
from .system import (
    copy_setup_to_bootstrapper_storage,
    is_installation_dir,
    kill_running_launcher_processes,
    move_staging_into_install_dir,
    wait_for_launcher_exit,
)
from .ui import UpdateDialog


def run_update_mode(install_dir: Path) -> int:
    """Run the launcher update flow in --update mode."""
    install_dir = install_dir.resolve()
    if not is_installation_dir(install_dir):
        print(f"error: {install_dir} 은(는) 설치된 WebApp Launcher 폴더가 아닙니다.", file=sys.stderr)
        return 2

    state = read_install_state(install_dir)
    if not isinstance(state, dict) or state.get("format") != INSTALL_STATE_FORMAT or not state.get("version"):
        print("error: 설치 상태 파일이 현재 형식이 아닙니다.", file=sys.stderr)
        return 2

    installed_version = str(state["version"])
    print(f"설치된 런처 버전: v{installed_version}")

    dialog = UpdateDialog(install_dir)
    exit_code = 0
    try:
        dialog.report_status("GitHub Release에서 새 버전을 확인하는 중...")
        dialog.report_overall(5)
        dialog.pump()

        release = find_latest_launcher_release()
        new_version = release["version"]
        print(f"최신 런처 버전: v{new_version}")
        if new_version == installed_version and not _env_flag("--force"):
            dialog.report_status(f"v{installed_version}은(는) 이미 최신 버전입니다.")
            dialog.report_overall(100)
            dialog.pump()
            time.sleep(1.0)
            return 0

        dialog.report_detail(f"v{installed_version} → v{new_version}")
        dialog.report_status("런처 실행을 정리하는 중...")
        dialog.pump()
        kill_running_launcher_processes()
        if not wait_for_launcher_exit(timeout_seconds=10.0):
            raise NetworkError("실행 중인 WebAppLauncher.exe가 종료되지 않았습니다.")

        with tempfile.TemporaryDirectory(prefix="wapl-update-") as staging_dir_str:
            staging_dir = Path(staging_dir_str)

            def on_progress(current: int, total: int, percent: int) -> None:
                dialog.report_status(f"런처 페이로드 다운로드 중 {percent}%")
                dialog.report_overall(10 + percent * 0.6)
                dialog.report_detail(f"{_format_bytes(current)} / {_format_bytes(total)}")
                dialog.pump()

            new_version, staging = download_launcher_payload(staging_dir, on_progress)
            dialog.report_status("런처 파일을 교체하는 중...")
            dialog.report_overall(75)
            dialog.pump()
            move_staging_into_install_dir(staging, install_dir)

        dialog.report_status("Setup.exe를 보관하는 중...")
        dialog.report_overall(85)
        dialog.pump()
        setup_path = copy_setup_to_bootstrapper_storage()

        write_install_state(install_dir, version=new_version, setup_path=setup_path)
        dialog.report_status("업데이트가 끝났습니다. 런처를 다시 시작합니다.")
        dialog.report_detail(f"v{new_version} 설치 완료")
        dialog.report_overall(100)
        dialog.pump()
        time.sleep(1.0)

        launcher = install_dir / "WebAppLauncher.exe"
        if launcher.is_file():
            subprocess.Popen([str(launcher)], cwd=str(install_dir))
    except NetworkError as exc:
        messagebox.showerror(
            PRODUCT_NAME,
            f"업데이트에 실패했습니다.\n\n{exc}",
        )
        print(f"error: {exc}", file=sys.stderr)
        exit_code = 3
    except Exception as exc:
        messagebox.showerror(PRODUCT_NAME, f"업데이트에 실패했습니다.\n\n{exc}")
        print(f"error: {exc}", file=sys.stderr)
        exit_code = 1
    finally:
        dialog.close()

    return exit_code


def _env_flag(name: str) -> bool:
    return os.environ.get(name, "").lower() in {"1", "true", "yes"}


def _format_bytes(value: int) -> str:
    size = float(max(0, value))
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.1f} {unit}" if unit != "B" else f"{int(size)} B"
        size /= 1024
    return f"{size:.1f} GB"
