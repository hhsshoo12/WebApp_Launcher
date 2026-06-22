from __future__ import annotations

from pathlib import Path
import json
import os
import subprocess
import sys
import tempfile

from .paths import PROJECT_ROOT, RUST_WEBVIEW_DIR
from .manifest import WindowOptions
from .settings import GlobalSettings


class WebViewError(RuntimeError):
    pass


def launch_webview(
    html_file: Path | None,
    url: str | None,
    runtime: dict[str, object],
    title: str,
    window: WindowOptions,
    settings: GlobalSettings,
) -> subprocess.Popen:
    binary = _ensure_webview_binary()
    command = [
        str(binary),
        "--runtime-json",
        json.dumps(runtime, ensure_ascii=False, separators=(",", ":")),
        "--title",
        title,
        "--window-level",
        window.level,
    ]
    if url is not None:
        command.extend(["--url", url])
    elif html_file is not None:
        command.extend(["--html", str(html_file)])
    else:
        raise WebViewError("html_file 또는 url이 필요합니다.")
    if settings.show_browser_console:
        command.append("--devtools")
    if window.borderless:
        command.append("--borderless")
    if window.fullscreen:
        command.append("--fullscreen")
    if window.transparent:
        command.append("--transparent")

    return subprocess.Popen(
        command,
        cwd=_runtime_cwd(),
        creationflags=_webview_creation_flags(),
    )


def _ensure_webview_binary() -> Path:
    bundled_binary = _bundled_webview_binary()
    if bundled_binary is not None:
        return bundled_binary

    debug_binary = RUST_WEBVIEW_DIR / "target" / "debug" / "wapk-webview.exe"
    release_binary = RUST_WEBVIEW_DIR / "target" / "release" / "wapk-webview.exe"
    external_target = _rust_target_dir()
    external_debug_binary = external_target / "debug" / "wapk-webview.exe"
    external_release_binary = external_target / "release" / "wapk-webview.exe"
    if external_release_binary.exists():
        return external_release_binary
    if external_debug_binary.exists():
        return external_debug_binary
    if release_binary.exists():
        return release_binary
    if debug_binary.exists():
        return debug_binary

    env = os.environ.copy()
    env["CARGO_TARGET_DIR"] = str(external_target)
    result = subprocess.run(
        ["cargo", "build"],
        cwd=RUST_WEBVIEW_DIR,
        env=env,
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        raise WebViewError(f"Rust WebView 빌드 실패:\n{result.stderr}")
    if not external_debug_binary.exists():
        raise WebViewError("Rust WebView 바이너리를 찾을 수 없습니다.")
    return external_debug_binary


def _bundled_webview_binary() -> Path | None:
    if not getattr(sys, "frozen", False):
        return None

    candidates = [
        Path(getattr(sys, "_MEIPASS", "")) / "wapk-webview.exe",
        Path(sys.executable).resolve().parent / "wapk-webview.exe",
    ]
    for candidate in candidates:
        if candidate.exists():
            return candidate

    raise WebViewError("번들에 wapk-webview.exe가 포함되어 있지 않습니다.")


def _runtime_cwd() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return PROJECT_ROOT


def _webview_creation_flags() -> int:
    if hasattr(subprocess, "CREATE_NO_WINDOW"):
        return subprocess.CREATE_NO_WINDOW
    return 0


def _rust_target_dir() -> Path:
    configured = os.environ.get("CARGO_TARGET_DIR")
    if configured:
        return Path(configured)
    return Path(tempfile.gettempdir()) / "wapk-webview-target"
