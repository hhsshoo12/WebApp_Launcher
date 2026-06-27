from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import time
import logging
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
from .strings import LocaleStrings

# Setup standard logging to file
try:
    appdata = Path(os.environ.get("APPDATA", str(Path.home())))
    log_dir = appdata / "WebAppLauncher"
    log_dir.mkdir(parents=True, exist_ok=True)
    log_file = log_dir / "installer.log"
    logging.basicConfig(
        filename=str(log_file),
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        encoding="utf-8"
    )
except Exception:
    pass


def run_update_mode(install_dir: Path) -> int:
    """Run the launcher update flow in --update mode."""
    logging.info("Starting update mode for directory: %s", install_dir)
    install_dir = install_dir.resolve()
    if not is_installation_dir(install_dir):
        err_msg = LocaleStrings.get("invalid_install_dir", install_dir)
        logging.error(err_msg)
        print(err_msg, file=sys.stderr)
        return 2

    state = read_install_state(install_dir)
    if not isinstance(state, dict) or state.get("format") != INSTALL_STATE_FORMAT or not state.get("version"):
        err_msg = LocaleStrings.get("invalid_state_format")
        logging.error(err_msg)
        print(err_msg, file=sys.stderr)
        return 2

    installed_version = str(state["version"])
    print(LocaleStrings.get("installed_version", installed_version))

    dialog = UpdateDialog(install_dir)
    exit_code = 0
    try:
        dialog.report_status(LocaleStrings.get("checking_new_version"))
        dialog.report_overall(5)
        dialog.pump()

        release = find_latest_launcher_release()
        new_version = release["version"]
        print(LocaleStrings.get("latest_version", new_version))
        if new_version == installed_version and not _env_flag("--force"):
            dialog.report_status(LocaleStrings.get("already_latest", installed_version))
            dialog.report_overall(100)
            dialog.pump()
            time.sleep(1.0)
            logging.info("Launcher is already up to date (v%s).", installed_version)
            return 0

        dialog.report_detail(f"v{installed_version} → v{new_version}")
        dialog.report_status(LocaleStrings.get("cleaning_runtime"))
        dialog.pump()
        kill_running_launcher_processes()
        if not wait_for_launcher_exit(timeout_seconds=10.0):
            raise NetworkError(LocaleStrings.get("process_not_terminated"))

        with tempfile.TemporaryDirectory(prefix="wapl-update-") as staging_dir_str:
            staging_dir = Path(staging_dir_str)

            def on_progress(current: int, total: int, percent: int) -> None:
                dialog.report_status(LocaleStrings.get("downloading_payload", percent))
                dialog.report_overall(10 + percent * 0.6)
                dialog.report_detail(f"{_format_bytes(current)} / {_format_bytes(total)}")
                dialog.pump()

            new_version, staging = download_launcher_payload(staging_dir, on_progress)
            dialog.report_status(LocaleStrings.get("replacing_files"))
            dialog.report_overall(75)
            dialog.pump()
            move_staging_into_install_dir(staging, install_dir)

        dialog.report_status(LocaleStrings.get("archiving_setup"))
        dialog.report_overall(85)
        dialog.pump()
        setup_path = copy_setup_to_bootstrapper_storage()

        write_install_state(install_dir, version=new_version, setup_path=setup_path)
        logging.info(LocaleStrings.get("setup_path_log", setup_path))
        dialog.report_status(LocaleStrings.get("update_complete_msg"))
        dialog.report_detail(LocaleStrings.get("update_complete_detail", new_version))
        dialog.report_overall(100)
        dialog.pump()
        time.sleep(1.0)

        launcher = install_dir / "WebAppLauncher.exe"
        if launcher.is_file():
            logging.info("Launching updated WebAppLauncher.exe")
            subprocess.Popen([str(launcher)], cwd=str(install_dir))
    except NetworkError as exc:
        logging.exception("Update failed due to NetworkError")
        messagebox.showerror(
            PRODUCT_NAME,
            f"{LocaleStrings.get('update_failed')}\n\n{exc}",
        )
        print(f"error: {exc}", file=sys.stderr)
        exit_code = 3
    except Exception as exc:
        logging.exception("Update failed with unexpected exception")
        messagebox.showerror(
            PRODUCT_NAME,
            f"{LocaleStrings.get('update_failed')}\n\n{exc}"
        )
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
