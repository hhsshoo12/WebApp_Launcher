from __future__ import annotations

from pathlib import Path
import json
import subprocess
import sys

from .paths import PROJECT_ROOT, RUST_WEBVIEW_DIR
from .manifest import WindowOptions


class WebViewError(RuntimeError):
    pass


def launch_webview(
    html_file: Path,
    runtime: dict[str, object],
    title: str,
    window: WindowOptions,
) -> subprocess.Popen:
    binary = _ensure_webview_binary()
    command = [
        str(binary),
        "--html",
        str(html_file),
        "--runtime-json",
        json.dumps(runtime, ensure_ascii=False, separators=(",", ":")),
        "--title",
        title,
        "--window-level",
        window.level,
    ]
    if window.borderless:
        command.append("--borderless")
    if window.fullscreen:
        command.append("--fullscreen")
    if window.transparent:
        command.append("--transparent")

    return subprocess.Popen(
        command,
        cwd=_runtime_cwd(),
    )


def _ensure_webview_binary() -> Path:
    bundled_binary = _bundled_webview_binary()
    if bundled_binary is not None:
        return bundled_binary

    debug_binary = RUST_WEBVIEW_DIR / "target" / "debug" / "wapk-webview.exe"
    release_binary = RUST_WEBVIEW_DIR / "target" / "release" / "wapk-webview.exe"
    if release_binary.exists():
        return release_binary
    if debug_binary.exists():
        return debug_binary

    result = subprocess.run(
        ["cargo", "build"],
        cwd=RUST_WEBVIEW_DIR,
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        raise WebViewError(f"Rust WebView 빌드 실패:\n{result.stderr}")
    if not debug_binary.exists():
        raise WebViewError("Rust WebView 바이너리를 찾을 수 없습니다.")
    return debug_binary


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
