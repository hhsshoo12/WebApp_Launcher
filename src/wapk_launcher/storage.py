from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import shutil
import time

from .manifest import ManifestError, WapkManifest
from .paths import APPS_DIR, app_dir, exe_path, html_path, manifest_path
from .processes import terminate_processes_by_executable
from .package import resolve_repository_manifest, install_from_repository


@dataclass(frozen=True)
class InstalledApp:
    manifest: WapkManifest
    directory: Path
    installed: bool


def install_manifest(manifest: WapkManifest) -> InstalledApp:
    manifest = resolve_repository_manifest(manifest)

    directory = app_dir(manifest.id)
    directory.mkdir(parents=True, exist_ok=True)
    target_manifest = manifest_path(manifest.id)

    existing = _load_existing(target_manifest)
    if existing and existing.version == manifest.version and _installed_files_exist(manifest):
        return InstalledApp(existing, directory, True)

    if manifest.mode == "backend":
        terminate_processes_by_executable(exe_path(manifest.id))
    install_from_repository(manifest)
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
                installed=_installed_files_exist(manifest),
            )
        )
    return apps


def delete_app(app_id: str) -> None:
    directory = app_dir(app_id)
    if directory.exists():
        terminate_processes_by_executable(exe_path(app_id))
        _remove_tree(directory)


def _load_existing(path: Path) -> WapkManifest | None:
    if not path.exists():
        return None
    try:
        return WapkManifest.load(path)
    except Exception:
        return None


def _installed_files_exist(manifest: WapkManifest) -> bool:
    if manifest.mode == "online":
        return True
    if manifest.mode == "html":
        return html_path(manifest.id).exists()
    return exe_path(manifest.id).exists() and html_path(manifest.id).exists()




def _remove_tree(directory: Path) -> None:
    last_error: OSError | None = None
    for _ in range(10):
        try:
            shutil.rmtree(directory)
            return
        except OSError as exc:
            last_error = exc
            if getattr(exc, "winerror", None) != 32:
                raise
            terminate_processes_by_executable(directory / "app.exe")
            time.sleep(0.2)
    raise ManifestError(f"앱 폴더를 삭제할 수 없습니다. 실행 중인 프로세스를 종료한 뒤 다시 시도하세요: {directory}") from last_error
