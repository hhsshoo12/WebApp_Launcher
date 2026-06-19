from __future__ import annotations

import argparse
from pathlib import Path
import sys
import ctypes

from .manifest import WapkManifest
from .runner import AppRunner
from .settings import GlobalSettings
from .storage import install_manifest, list_installed


def main(argv: list[str] | None = None) -> None:
    _enable_high_dpi()
    parser = argparse.ArgumentParser(description="WAPK Launcher MVP")
    parser.add_argument("wapk", nargs="?", help="열거나 설치할 .wapk TOML 파일")
    parser.add_argument("--install", action="store_true", help="GUI 없이 .wapk를 설치하고 종료")
    parser.add_argument("--run", action="store_true", help="GUI 없이 .wapk를 설치한 뒤 실행")
    parser.add_argument("--list", action="store_true", help="설치된 앱 목록을 출력하고 종료")
    parsed = parser.parse_args(sys.argv[1:] if argv is None else argv)

    if parsed.list:
        for app in list_installed():
            print(f"{app.manifest.id}\t{app.manifest.name}\t{app.manifest.version}\t{app.installed}")
        return

    if parsed.install or parsed.run:
        if not parsed.wapk:
            parser.error("--install/--run에는 .wapk 경로가 필요합니다.")
        manifest = WapkManifest.load(Path(parsed.wapk).resolve())
        installed = install_manifest(manifest)
        print(f"installed {installed.manifest.id} {installed.manifest.version}")
        if parsed.run:
            runner = AppRunner(GlobalSettings.load())
            try:
                running = runner.start(installed.manifest)
                print(f"running {installed.manifest.id} port={running.port}")
                if running.backend is not None:
                    running.backend.wait()
                elif running.webview is not None:
                    running.webview.wait()
            finally:
                runner.stop_all()
        return

    # 순환 참조 방지를 위해 지연 임포트 수행
    from .app import LauncherApp

    initial = Path(parsed.wapk).resolve() if parsed.wapk else None
    app = LauncherApp(initial)
    app.mainloop()


def _enable_high_dpi() -> None:
    if sys.platform != "win32":
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass
