from __future__ import annotations

from pathlib import Path
import sys


if getattr(sys, "frozen", False):
    PROJECT_ROOT = Path(sys.executable).resolve().parent
else:
    PROJECT_ROOT = Path(__file__).resolve().parents[2]
APPS_DIR = PROJECT_ROOT / "apps"
RUST_WEBVIEW_DIR = PROJECT_ROOT / "rust" / "wapk-webview"


def app_dir(app_id: str) -> Path:
    return APPS_DIR / app_id


def manifest_path(app_id: str) -> Path:
    return app_dir(app_id) / "manifest.toml"


def exe_path(app_id: str) -> Path:
    return app_dir(app_id) / "app.exe"


def html_path(app_id: str) -> Path:
    return app_dir(app_id) / "ui.html"
