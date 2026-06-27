from __future__ import annotations

import argparse
import sys
import tkinter as tk
from pathlib import Path

from wapl_installer.config import *  # re-exported for installer tests and tooling
from wapl_installer.release import *
from wapl_installer.runtime import *
from wapl_installer.state import *
from wapl_installer.system import *
from wapl_installer.ui import InstallerApp, UpdateDialog
from wapl_installer.orchestration import run_update_mode


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="WebAppLauncher-Setup")
    parser.add_argument("--update", action="store_true", help="기존 설치 폴더를 새 버전으로 업데이트합니다.")
    parser.add_argument(
        "--install-dir",
        type=Path,
        help="업데이트 대상 설치 폴더 (--update와 함께 사용).",
    )
    parser.add_argument(
        "--check-update",
        action="store_true",
        help="GitHub Release의 최신 런처 버전을 출력하고 종료합니다.",
    )
    parser.add_argument(
        "--copy-self",
        type=Path,
        help="현재 Setup.exe를 지정한 경로로 복사하고 종료합니다 (디버그용).",
    )
    return parser.parse_args(argv)


def main() -> None:
    args = parse_args(sys.argv[1:])

    if args.copy_self is not None:
        target = copy_setup_to_bootstrapper_storage(args.copy_self)
        print(str(target))
        return

    if args.check_update:
        try:
            release = find_latest_launcher_release()
        except NetworkError as exc:
            print(f"error: {exc}", file=sys.stderr)
            sys.exit(3)
        print(f"{release['version']}\t{release['zip_url']}")
        return

    if args.update:
        if args.install_dir is None:
            print("error: --update에는 --install-dir 옵션이 필요합니다.", file=sys.stderr)
            sys.exit(2)
        exit_code = run_update_mode(args.install_dir)
        sys.exit(exit_code)

    root = tk.Tk()
    try:
        root.iconbitmap(default=str(bundled_path("assets/installer.ico")))
    except tk.TclError:
        pass
    try:
        root.window_icon = tk.PhotoImage(file=str(bundled_path("assets/logo.png")))
        root.iconphoto(True, root.window_icon)
    except tk.TclError:
        pass
    InstallerApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
