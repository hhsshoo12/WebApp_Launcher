from __future__ import annotations

from pathlib import Path
import shutil
import tempfile
import time
import tomllib
import zipfile

from .downloader import download_to
from .manifest import ManifestError, WapkManifest
from .paths import app_dir, exe_path, html_path
from .processes import terminate_processes_by_executable


def resolve_repository_manifest(manifest: WapkManifest) -> WapkManifest:
    if manifest.repository is None:
        return manifest
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


def install_from_repository(manifest: WapkManifest) -> None:
    if manifest.repository is None:
        app_dir(manifest.id).mkdir(parents=True, exist_ok=True)
        return
    with tempfile.TemporaryDirectory() as temp_dir:
        repo_root = _download_repository(manifest, Path(temp_dir))
        metadata_path = _find_metadata(repo_root)
        if metadata_path is None:
            raise ManifestError("레포 zip 안에서 metadata.toml을 찾을 수 없습니다.")

        app_dir(manifest.id).mkdir(parents=True, exist_ok=True)
        if manifest.mode == "backend":
            if manifest.app_exe is None or manifest.app_html is None:
                raise ManifestError("backend 모드는 app_exe와 app_html이 필요합니다.")
            source_exe = _safe_repo_path(repo_root, manifest.app_exe)
            source_html = _safe_repo_path(repo_root, manifest.app_html)
            if not source_exe.is_file():
                raise ManifestError(f"metadata.toml의 app_exe 파일을 찾을 수 없습니다: {manifest.app_exe}")
            if not source_html.is_file():
                raise ManifestError(f"metadata.toml의 app_html 파일을 찾을 수 없습니다: {manifest.app_html}")
            _copy_file(source_exe, exe_path(manifest.id))
            shutil.copy2(source_html, html_path(manifest.id))
        elif manifest.mode == "html":
            if manifest.app_html is None:
                raise ManifestError("html 모드는 app_html이 필요합니다.")
            source_html = _safe_repo_path(repo_root, manifest.app_html)
            if not source_html.is_file():
                raise ManifestError(f"metadata.toml의 app_html 파일을 찾을 수 없습니다: {manifest.app_html}")
            shutil.copy2(source_html, html_path(manifest.id))


def _download_repository(manifest: WapkManifest, temp_dir: Path) -> Path:
    if manifest.repository is None:
        raise ManifestError('repository는 "owner/repo" 형식이어야 합니다.')
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
    parts = repository.split("/", 1)
    if len(parts) != 2:
        raise ManifestError('repository는 "owner/repo" 형식이어야 합니다.')
    owner, repo = parts
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
        "mode": manifest.mode,
        "ref": manifest.ref,
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
    if manifest.repository is not None:
        data["repository"] = manifest.repository
    if manifest.app_exe is not None:
        data["app_exe"] = manifest.app_exe
    if manifest.app_html is not None:
        data["app_html"] = manifest.app_html
    if manifest.url is not None:
        data["url"] = manifest.url
    if manifest.ready_url is not None:
        data["ready_url"] = manifest.ready_url
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
