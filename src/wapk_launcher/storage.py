from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import shutil
import tempfile
import time
import tomllib
from urllib.parse import urlparse
import zipfile

from .downloader import download_to
from .manifest import ManifestError, WapkManifest
from .paths import APPS_DIR, app_dir, exe_path, html_path, manifest_path
from .processes import terminate_processes_by_executable


@dataclass(frozen=True)
class InstalledApp:
    manifest: WapkManifest
    directory: Path
    installed: bool


def install_manifest(manifest: WapkManifest) -> InstalledApp:
    if manifest.repository:
        manifest = _resolve_repository_manifest(manifest)

    directory = app_dir(manifest.id)
    directory.mkdir(parents=True, exist_ok=True)
    target_manifest = manifest_path(manifest.id)

    existing = _load_existing(target_manifest)
    if existing and existing.version == manifest.version and exe_path(manifest.id).exists() and html_path(manifest.id).exists():
        return InstalledApp(existing, directory, True)

    terminate_processes_by_executable(exe_path(manifest.id))
    if manifest.repository:
        _install_from_repository(manifest)
    else:
        if manifest.exe_url is None or manifest.html_url is None:
            raise ManifestError("exe_url/html_url 설정이 필요합니다.")
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
        terminate_processes_by_executable(exe_path(app_id))
        _remove_tree(directory)


def _load_existing(path: Path) -> WapkManifest | None:
    if not path.exists():
        return None
    try:
        return WapkManifest.load(path)
    except Exception:
        return None


def _resolve_repository_manifest(manifest: WapkManifest) -> WapkManifest:
    with tempfile.TemporaryDirectory() as temp_dir:
        repo_root = _download_repository(manifest, Path(temp_dir))
        metadata_path = _find_metadata(repo_root)
        if metadata_path is None:
            raise ManifestError("레포 zip 안에서 metadata.toml을 찾을 수 없습니다.")
        metadata = tomllib.loads(metadata_path.read_text(encoding="utf-8"))
        merged = _manifest_to_dict(manifest)
        merged.update(metadata)
        merged.setdefault("repository", manifest.repository)
        merged.setdefault("ref", manifest.ref)
        return WapkManifest.from_dict(merged)


def _install_from_repository(manifest: WapkManifest) -> None:
    with tempfile.TemporaryDirectory() as temp_dir:
        repo_root = _download_repository(manifest, Path(temp_dir))
        metadata_path = _find_metadata(repo_root)
        if metadata_path is None:
            raise ManifestError("레포 zip 안에서 metadata.toml을 찾을 수 없습니다.")

        source_exe = _safe_repo_path(repo_root, manifest.app_exe)
        source_html = _safe_repo_path(repo_root, manifest.app_html)
        if not source_exe.is_file():
            raise ManifestError(f"metadata.toml의 app_exe 파일을 찾을 수 없습니다: {manifest.app_exe}")
        if not source_html.is_file():
            raise ManifestError(f"metadata.toml의 app_html 파일을 찾을 수 없습니다: {manifest.app_html}")

        app_dir(manifest.id).mkdir(parents=True, exist_ok=True)
        _copy_file(source_exe, exe_path(manifest.id))
        shutil.copy2(source_html, html_path(manifest.id))


def _download_repository(manifest: WapkManifest, temp_dir: Path) -> Path:
    if manifest.repository is None:
        raise ManifestError("repository 설정이 필요합니다.")
    archive_url = _repository_archive_url(manifest.repository, manifest.ref)
    archive_path = temp_dir / "repo.zip"
    extract_dir = temp_dir / "repo"
    download_to(archive_url, archive_path)
    extract_dir.mkdir(parents=True, exist_ok=True)

    with zipfile.ZipFile(archive_path) as archive:
        _safe_extract(archive, extract_dir)

    items = list(extract_dir.iterdir())
    children = [path for path in items if path.is_dir()]
    files = [path for path in items if path.is_file()]
    if len(children) == 1 and not files:
        return children[0]
    return extract_dir


def _repository_archive_url(repository: str, ref: str) -> str:
    parsed = urlparse(repository)
    if parsed.scheme in ("", "file") or repository.lower().endswith(".zip"):
        return repository
    if parsed.netloc.lower() != "github.com":
        raise ManifestError(f"지원하지 않는 repository 호스트입니다: {parsed.netloc}")

    parts = [part for part in parsed.path.strip("/").split("/") if part]
    if len(parts) < 2:
        raise ManifestError("GitHub repository URL은 owner/repo 형식이어야 합니다.")
    owner = parts[0]
    repo = parts[1].removesuffix(".git")
    return f"https://github.com/{owner}/{repo}/archive/{ref}.zip"


def _safe_extract(archive: zipfile.ZipFile, destination: Path) -> None:
    destination = destination.resolve()
    for member in archive.infolist():
        target = (destination / member.filename).resolve()
        if destination != target and destination not in target.parents:
            raise ManifestError(f"zip 경로가 대상 폴더 밖을 가리킵니다: {member.filename}")
    archive.extractall(destination)


def _find_metadata(repo_root: Path) -> Path | None:
    direct = repo_root / "metadata.toml"
    if direct.is_file():
        return direct
    matches = sorted(repo_root.rglob("metadata.toml"), key=lambda path: len(path.parts))
    return matches[0] if matches else None


def _safe_repo_path(repo_root: Path, relative_path: str) -> Path:
    root = repo_root.resolve()
    target = (root / relative_path).resolve()
    if root != target and root not in target.parents:
        raise ManifestError(f"상대경로가 레포 밖을 가리킵니다: {relative_path}")
    return target


def _manifest_to_dict(manifest: WapkManifest) -> dict[str, object]:
    data: dict[str, object] = {
        "id": manifest.id,
        "name": manifest.name,
        "version": manifest.version,
        "repository": manifest.repository,
        "ref": manifest.ref,
        "app_exe": manifest.app_exe,
        "app_html": manifest.app_html,
        "args": list(manifest.args),
        "port_range": list(manifest.port_range),
        "api_base": manifest.api_base,
        "window": {
            "borderless": manifest.window.borderless,
            "fullscreen": manifest.window.fullscreen,
            "transparent": manifest.window.transparent,
            "level": manifest.window.level,
        },
    }
    if manifest.ready_url is not None:
        data["ready_url"] = manifest.ready_url
    if manifest.exe_url is not None:
        data["exe_url"] = manifest.exe_url
    if manifest.html_url is not None:
        data["html_url"] = manifest.html_url
    return data


def _copy_file(source: Path, destination: Path) -> None:
    last_error: OSError | None = None
    for _ in range(10):
        try:
            shutil.copy2(source, destination)
            return
        except OSError as exc:
            last_error = exc
            if getattr(exc, "winerror", None) != 32:
                raise
            terminate_processes_by_executable(destination)
            time.sleep(0.2)
    raise ManifestError(f"파일을 덮어쓸 수 없습니다. 실행 중인 프로세스를 종료한 뒤 다시 시도하세요: {destination}") from last_error


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
