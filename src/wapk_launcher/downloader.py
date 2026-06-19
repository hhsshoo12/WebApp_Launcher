from __future__ import annotations

from pathlib import Path
import shutil
import tempfile
from urllib.parse import urlparse, unquote
from urllib.request import urlopen


class DownloadError(RuntimeError):
    pass


def download_to(url: str, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    parsed = urlparse(url)

    try:
        with tempfile.NamedTemporaryFile(delete=False, dir=destination.parent) as temp:
            temp_path = Path(temp.name)
            if _is_local_path(url, parsed.scheme):
                source = _local_source_path(url, parsed)
                with source.open("rb") as src:
                    shutil.copyfileobj(src, temp)
            elif parsed.scheme in ("http", "https"):
                with urlopen(url, timeout=60) as response:
                    shutil.copyfileobj(response, temp)
            else:
                raise DownloadError(f"지원하지 않는 URL 스킴입니다: {parsed.scheme}")

        temp_path.replace(destination)
    except Exception as exc:
        if "temp_path" in locals() and temp_path.exists():
            temp_path.unlink(missing_ok=True)
        if isinstance(exc, DownloadError):
            raise
        raise DownloadError(f"{url} 다운로드 실패: {exc}") from exc


def _is_local_path(url: str, scheme: str) -> bool:
    return scheme in ("", "file") or (len(scheme) == 1 and url[1:3] in (":\\", ":/"))


def _local_source_path(url: str, parsed) -> Path:
    if parsed.scheme == "file":
        if parsed.netloc:
            return Path(f"//{parsed.netloc}{unquote(parsed.path)}")
        return Path(unquote(parsed.path))
    return Path(url)
