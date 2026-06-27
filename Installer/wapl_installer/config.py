from __future__ import annotations

import re
import sys
from pathlib import Path

try:
    from version import __version__ as SETUP_VERSION
except ImportError:
    SETUP_VERSION = "0.0.0+local"


PRODUCT_NAME = "WebApp Launcher"
WINDOW_WIDTH = 680
WINDOW_HEIGHT = 430
SIDEBAR_WIDTH = 150
INSTALL_STATE_FILE = ".webapp-launcher-install.json"
INSTALL_STATE_FORMAT = 2
REGISTRY_KEY = r"Software\WebAppLauncher"
REGISTRY_INSTALL_VALUE = "InstallLocation"
ASSOC_CLASSES_ROOT = r"Software\Classes"
ASSOC_WAPK_PROGID = "WebAppLauncher.Wapk"
ASSOC_WEBAPP_PROGID = "WebAppLauncher.Webapp"
ASSOC_DESCRIPTIONS = {
    ASSOC_WAPK_PROGID: "WebAppLauncher 패키지",
    ASSOC_WEBAPP_PROGID: "WebAppLauncher 앱",
}
ASSOC_EXTENSIONS = {
    ".wapk": ASSOC_WAPK_PROGID,
    ".webapp": ASSOC_WEBAPP_PROGID,
}

GITHUB_REPO = "hhsshoo12/WebApp_Launcher"
GITHUB_API_BASE = "https://api.github.com"
LAUNCHER_TAG_PREFIX = "v"
RUNTIME_TAG_PREFIX = "runtime-"
LAUNCHER_ASSET_PATTERN = re.compile(
    r"^WAPL-Launcher-v(?P<version>[^/]+)\.zip$", re.IGNORECASE
)
RUNTIME_ASSET_PATTERN = re.compile(
    r"^WAPL-Runtime-v(?P<version>[^/]+)\.zip$", re.IGNORECASE
)
WEBVIEW2_BOOTSTRAPPER_URL = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
BOOTSTRAPPER_STORAGE_DIR = Path.home() / ".wapk" / "bootstrapper"
BOOTSTRAPPER_STORAGE_FILENAME = "WebAppLauncher-Setup.exe"
HTTP_USER_AGENT = "WebAppLauncher-Setup/{0}".format(SETUP_VERSION)
HTTP_TIMEOUT_SECONDS = 30
NETWORK_ERROR_MESSAGE = "인터넷 연결 또는 GitHub 접근이 필요합니다"


class InstallCancelled(Exception):
    pass


class NetworkError(RuntimeError):
    pass


def bundled_path(name: str) -> Path:
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent.parent))
    return base / name
