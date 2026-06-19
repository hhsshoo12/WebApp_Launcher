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
            if parsed.scheme in ("", "file"):
                source = Path(unquote(parsed.path if parsed.scheme else url))
                if parsed.netloc:
                    source = Path(f"//{parsed.netloc}{unquote(parsed.path)}")
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
