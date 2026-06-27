from __future__ import annotations

import hashlib
import json
import re
import shutil
import urllib.error
import urllib.request
import zipfile
from datetime import datetime
from pathlib import Path

from .config import (
    GITHUB_API_BASE,
    GITHUB_REPO,
    HTTP_TIMEOUT_SECONDS,
    HTTP_USER_AGENT,
    LAUNCHER_ASSET_PATTERN,
    LAUNCHER_TAG_PREFIX,
    NETWORK_ERROR_MESSAGE,
    RUNTIME_ASSET_PATTERN,
    RUNTIME_TAG_PREFIX,
    NetworkError,
)


def _http_get_json(url: str) -> object:
    request = urllib.request.Request(url, headers={"User-Agent": HTTP_USER_AGENT, "Accept": "application/vnd.github+json"})
    try:
        with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT_SECONDS) as response:
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
        raise NetworkError(f"{NETWORK_ERROR_MESSAGE} ({exc})") from exc


def _http_download_to_file(
    url: str, destination: Path, on_progress: object | None = None
) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": HTTP_USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT_SECONDS) as response:
            total_header = response.headers.get("Content-Length")
            total_bytes = int(total_header) if total_header and total_header.isdigit() else -1
            downloaded = 0
            last_reported = -1
            with destination.open("wb") as target:
                while True:
                    chunk = response.read(64 * 1024)
                    if not chunk:
                        break
                    target.write(chunk)
                    downloaded += len(chunk)
                    if callable(on_progress) and total_bytes > 0:
                        percent = int(downloaded * 100 / total_bytes)
                        if percent != last_reported:
                            on_progress(downloaded, total_bytes, percent)
                            last_reported = percent
    except (urllib.error.URLError, TimeoutError) as exc:
        if destination.exists():
            try:
                destination.unlink()
            except OSError:
                pass
        raise NetworkError(f"{NETWORK_ERROR_MESSAGE} ({exc})") from exc


def _http_fetch_text(url: str) -> str:
    request = urllib.request.Request(url, headers={"User-Agent": HTTP_USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=HTTP_TIMEOUT_SECONDS) as response:
            return response.read().decode("utf-8")
    except (urllib.error.URLError, TimeoutError) as exc:
        raise NetworkError(f"{NETWORK_ERROR_MESSAGE} ({exc})") from exc


def find_latest_launcher_release() -> dict:
    """Return the latest v* release with a WAPL-Launcher-v*.zip asset."""
    url = f"{GITHUB_API_BASE}/repos/{GITHUB_REPO}/releases?per_page=30"
    try:
        payload = _http_get_json(url)
    except NetworkError:
        raise
    if not isinstance(payload, list):
        raise NetworkError(NETWORK_ERROR_MESSAGE)

    candidates: list[tuple[datetime, dict]] = []
    for release in payload:
        if not isinstance(release, dict):
            continue
        if release.get("draft"):
            continue
        tag = release.get("tag_name")
        if not isinstance(tag, str):
            continue
        if not tag.lower().startswith(LAUNCHER_TAG_PREFIX):
            continue
        if tag.lower().startswith(RUNTIME_TAG_PREFIX):
            continue
        published = release.get("published_at") or release.get("created_at")
        timestamp = _parse_iso8601(published) or datetime.min
        candidates.append((timestamp, release))

    if not candidates:
        raise NetworkError("업데이트할 런처 릴리스를 찾지 못했습니다.")

    candidates.sort(key=lambda pair: pair[0], reverse=True)
    for _, release in candidates:
        resolved = _resolve_asset_pair(release, LAUNCHER_ASSET_PATTERN)
        if resolved is not None:
            return resolved

    raise NetworkError("완성된 WAPL-Launcher-v*.zip 릴리스 자산을 찾지 못했습니다.")


def find_latest_runtime_release() -> dict:
    """Return the latest runtime-* release with a WAPL-Runtime-v*.zip asset."""
    url = f"{GITHUB_API_BASE}/repos/{GITHUB_REPO}/releases?per_page=30"
    payload = _http_get_json(url)
    if not isinstance(payload, list):
        raise NetworkError(NETWORK_ERROR_MESSAGE)

    candidates: list[tuple[datetime, dict]] = []
    for release in payload:
        if not isinstance(release, dict) or release.get("draft"):
            continue
        tag = release.get("tag_name")
        if not isinstance(tag, str) or not tag.lower().startswith(RUNTIME_TAG_PREFIX):
            continue
        published = release.get("published_at") or release.get("created_at")
        candidates.append((_parse_iso8601(published) or datetime.min, release))

    if not candidates:
        raise NetworkError("런타임 릴리스를 찾지 못했습니다.")

    candidates.sort(key=lambda pair: pair[0], reverse=True)
    for _, release in candidates:
        resolved = _resolve_asset_pair(release, RUNTIME_ASSET_PATTERN)
        if resolved is not None:
            return resolved

    raise NetworkError("완성된 WAPL-Runtime-v*.zip 릴리스 자산을 찾지 못했습니다.")


def _resolve_asset_pair(release: dict, pattern: re.Pattern[str]) -> dict | None:
    assets = release.get("assets") or []
    zip_asset = None
    for asset in assets:
        if not isinstance(asset, dict):
            continue
        name = asset.get("name")
        if isinstance(name, str) and pattern.match(name):
            zip_asset = asset
            break
    if zip_asset is None:
        return None

    zip_name = zip_asset["name"]
    checksum_name = f"{zip_name}.sha256"
    checksum_asset = next(
        (
            asset
            for asset in assets
            if isinstance(asset, dict) and asset.get("name") == checksum_name
        ),
        None,
    )
    if checksum_asset is None:
        return None

    return {
        "version": pattern.match(zip_name).group("version"),
        "tag": release["tag_name"],
        "zip_url": zip_asset["browser_download_url"],
        "zip_name": zip_name,
        "checksum_url": checksum_asset["browser_download_url"],
    }


def _parse_iso8601(value: object) -> datetime | None:
    if not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def verify_sha256(archive_path: Path, expected: str) -> None:
    expected_digest = expected.strip().lower()
    if not re.fullmatch(r"[0-9a-f]{64}", expected_digest):
        raise NetworkError("체크섬 형식이 올바르지 않습니다.")
    digest = hashlib.sha256()
    with archive_path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(64 * 1024), b""):
            digest.update(chunk)
    if digest.hexdigest() != expected_digest:
        raise NetworkError(
            f"SHA-256 검증 실패 (기대 {expected_digest}, 실제 {digest.hexdigest()})"
        )


def _safe_extract_zip(archive_path: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    absolute_destination = destination.resolve()
    with zipfile.ZipFile(archive_path) as archive:
        for member in archive.infolist():
            member_path = (destination / member.filename).resolve()
            try:
                member_path.relative_to(absolute_destination)
            except ValueError as exc:
                raise NetworkError("압축 파일에 잘못된 경로가 있습니다.") from exc
            if member.is_dir():
                member_path.mkdir(parents=True, exist_ok=True)
                continue
            member_path.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(member) as source, member_path.open("wb") as target:
                shutil.copyfileobj(source, target)


def validate_launcher_payload_layout(staging: Path) -> None:
    required_files = [
        "WebAppLauncher.exe",
        "WebAppLauncher.Cli.exe",
        "Ui/index.html",
    ]
    for relative in required_files:
        if not (staging / relative).is_file():
            raise NetworkError(f"런처 페이로드에 {relative} 파일이 없습니다.")


def download_launcher_payload(
    destination_root: Path, on_progress: object | None = None
) -> tuple[str, Path]:
    """Download + verify + extract the latest launcher payload.

    Returns the resolved version string and the staging directory used for
    extraction. The caller is responsible for moving the staging files into
    their final location.
    """
    release = find_latest_launcher_release()
    staging = destination_root / f".staging-{release['version']}"
    if staging.exists():
        shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True, exist_ok=True)

    archive = staging / release["zip_name"]
    _http_download_to_file(release["zip_url"], archive, on_progress)

    checksum_text = _http_fetch_text(release["checksum_url"])
    expected_digest: str | None = None
    for token in re.findall(r"[0-9a-fA-F]{64}", checksum_text):
        expected_digest = token.lower()
        break
    if not expected_digest:
        archive.unlink(missing_ok=True)
        raise NetworkError("체크섬 파일에서 해시 값을 읽지 못했습니다.")
    try:
        verify_sha256(archive, expected_digest)
        _safe_extract_zip(archive, staging)
        validate_launcher_payload_layout(staging)
    finally:
        archive.unlink(missing_ok=True)
    return release["version"], staging


def validate_runtime_bundle_layout(staging: Path) -> None:
    required_dirs = ["runtime", "tools", "LICENSES"]
    for name in required_dirs:
        if not (staging / name).is_dir():
            raise NetworkError(f"런타임 번들에 {name}/ 폴더가 없습니다.")
    if not (staging / "runtime-manifest.toml").is_file():
        raise NetworkError("런타임 번들에 runtime-manifest.toml 파일이 없습니다.")


def download_runtime_bundle(
    destination_root: Path, on_progress: object | None = None
) -> tuple[str, Path]:
    release = find_latest_runtime_release()
    staging = destination_root / f".runtime-staging-{release['version']}"
    if staging.exists():
        shutil.rmtree(staging, ignore_errors=True)
    staging.mkdir(parents=True, exist_ok=True)

    archive = staging / release["zip_name"]
    _http_download_to_file(release["zip_url"], archive, on_progress)
    checksum_text = _http_fetch_text(release["checksum_url"])
    expected_digest = next(
        (token.lower() for token in re.findall(r"[0-9a-fA-F]{64}", checksum_text)),
        None,
    )
    if not expected_digest:
        archive.unlink(missing_ok=True)
        raise NetworkError("런타임 체크섬 파일에서 해시 값을 읽지 못했습니다.")
    try:
        verify_sha256(archive, expected_digest)
        _safe_extract_zip(archive, staging)
        validate_runtime_bundle_layout(staging)
    finally:
        archive.unlink(missing_ok=True)
    return release["version"], staging
