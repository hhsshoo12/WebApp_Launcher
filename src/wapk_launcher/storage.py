from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import shutil

from .downloader import download_to
from .manifest import WapkManifest
from .paths import APPS_DIR, app_dir, exe_path, html_path, manifest_path


@dataclass(frozen=True)
class InstalledApp:
    manifest: WapkManifest
    directory: Path
    installed: bool


def install_manifest(manifest: WapkManifest) -> InstalledApp:
    directory = app_dir(manifest.id)
    directory.mkdir(parents=True, exist_ok=True)
    target_manifest = manifest_path(manifest.id)

    existing = _load_existing(target_manifest)
    if existing and existing.version == manifest.version and exe_path(manifest.id).exists() and html_path(manifest.id).exists():
        return InstalledApp(existing, directory, True)

    download_to(manifest.exe_url, exe_path(manifest.id))
    download_to(manifest.html_url, html_path(manifest.id))
    target_manifest.write_text(manifest.to_toml(), encoding="utf-8")
    return InstalledApp(manifest, directory, True)


def list_installed() -> list[InstalledApp]:
    APPS_DIR.mkdir(parents=True, exist_ok=True)
    apps: list[InstalledApp] = []
    for path in sorted(APPS_DIR.iterdir(), key=lambda item: item.name.lower()):
        if not path.is_dir():
            continue
        manifest_file = path / "manifest.toml"
        if not manifest_file.exists():
            continue
        try:
            manifest = WapkManifest.load(manifest_file)
        except Exception:
            continue
        apps.append(
            InstalledApp(
                manifest=manifest,
                directory=path,
                installed=(path / "app.exe").exists() and (path / "ui.html").exists(),
            )
        )
    return apps


def delete_app(app_id: str) -> None:
    directory = app_dir(app_id)
    if directory.exists():
        shutil.rmtree(directory)


def _load_existing(path: Path) -> WapkManifest | None:
    if not path.exists():
        return None
    try:
        return WapkManifest.load(path)
    except Exception:
        return None
