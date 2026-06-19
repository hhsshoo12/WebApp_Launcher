from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re
import tomllib
from typing import Any
import zipfile


APP_ID_RE = re.compile(r"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,79}$")
GITHUB_REPO_RE = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
PLACEHOLDER = "{PORT}"
WINDOW_LEVELS = {"normal", "top", "bottom"}
APP_MODES = {"backend", "html", "online"}


class ManifestError(ValueError):
    pass


@dataclass(frozen=True)
class WindowOptions:
    borderless: bool = False
    fullscreen: bool = False
    transparent: bool = False
    level: str = "normal"

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "WindowOptions":
        window = data.get("window", {})
        if window is None:
            window = {}
        if not isinstance(window, dict):
            raise ManifestError("window는 TOML 테이블이어야 합니다.")

        borderless = _optional_bool(data, window, "borderless", False)
        fullscreen = _optional_bool(data, window, "fullscreen", False)
        transparent = _optional_bool(data, window, "transparent", False)
        level = _optional_str(data, window, "window_level", "level", "normal")
        if level not in WINDOW_LEVELS:
            raise ManifestError("window.level은 normal, top, bottom 중 하나여야 합니다.")

        return cls(
            borderless=borderless,
            fullscreen=fullscreen,
            transparent=transparent,
            level=level,
        )


@dataclass(frozen=True)
class WapkManifest:
    id: str
    name: str
    version: str
    mode: str
    repository: str | None
    ref: str
    app_exe: str | None
    app_html: str | None
    url: str | None
    args: tuple[str, ...]
    port_range: tuple[int, int]
    ready_url: str | None
    api_base: str
    window: WindowOptions

    @classmethod
    def load(cls, path: Path) -> "WapkManifest":
        if zipfile.is_zipfile(path):
            try:
                with zipfile.ZipFile(path) as archive:
                    metadata_name = _find_zip_metadata(archive)
                    if metadata_name is None:
                        raise ManifestError("zip .wapk 안에서 metadata.toml을 찾을 수 없습니다.")
                    data = tomllib.loads(archive.read(metadata_name).decode("utf-8"))
            except tomllib.TOMLDecodeError as exc:
                raise ManifestError(f"metadata.toml 파싱 실패: {exc}") from exc
            except UnicodeDecodeError as exc:
                raise ManifestError(f"metadata.toml은 UTF-8 TOML이어야 합니다: {exc}") from exc
            return cls.from_dict(data)

        try:
            data = tomllib.loads(path.read_text(encoding="utf-8"))
        except tomllib.TOMLDecodeError as exc:
            raise ManifestError(f"TOML 파싱 실패: {exc}") from exc
        except UnicodeDecodeError as exc:
            raise ManifestError(f"WAPK 파일은 UTF-8 TOML이어야 합니다: {exc}") from exc
        except OSError as exc:
            raise ManifestError(f"WAPK 파일을 읽을 수 없습니다: {exc}") from exc

        return cls.from_dict(data)

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> "WapkManifest":
        mode = _optional_plain_str(data, "mode", "backend")
        if mode not in APP_MODES:
            raise ManifestError("mode는 backend, html, online 중 하나여야 합니다.")

        repository = _optional_plain_str(data, "repository", None)
        if repository is None and mode != "online":
            raise ManifestError('repository는 필수이며 "owner/repo" 형식이어야 합니다.')
        if repository is not None:
            repository = _normalize_repository(repository)

        app_id = _optional_plain_str(data, "id", None)
        if app_id is None:
            name_for_id = _optional_plain_str(data, "name", None)
            if name_for_id is not None:
                app_id = _slugify(name_for_id)
            elif repository is not None:
                app_id = _slugify(repository.split("/", 1)[1])
            else:
                url_for_id = _optional_plain_str(data, "url", None)
                app_id = _slugify(url_for_id or "online-app")
        if not APP_ID_RE.fullmatch(app_id):
            raise ManifestError("id는 영문/숫자로 시작하고 영문, 숫자, _, ., - 만 사용할 수 있습니다.")

        name = _optional_plain_str(data, "name", app_id)
        version = _optional_plain_str(data, "version", "0.0.0")
        ref = _optional_plain_str(data, "ref", "main")
        app_exe = _optional_plain_str(data, "app_exe", "app.exe" if mode == "backend" else None)
        app_html = _optional_plain_str(data, "app_html", "app.html" if mode in {"backend", "html"} else None)
        url = _optional_plain_str(data, "url", None)
        if mode == "backend" and (app_exe is None or app_html is None):
            raise ManifestError("backend 모드는 app_exe와 app_html이 필요합니다.")
        if mode == "html" and app_html is None:
            raise ManifestError("html 모드는 app_html이 필요합니다.")
        if mode == "online" and url is None:
            raise ManifestError("online 모드는 url이 필요합니다.")

        raw_args = data.get("args", [])
        if not isinstance(raw_args, list) or not all(isinstance(item, str) for item in raw_args):
            raise ManifestError("args는 문자열 배열이어야 합니다.")
        args = tuple(raw_args)

        raw_range = data.get("port_range", [52000, 52500])
        if (
            not isinstance(raw_range, list)
            or len(raw_range) != 2
            or not all(isinstance(item, int) for item in raw_range)
        ):
            raise ManifestError("port_range는 정수 2개 배열이어야 합니다.")

        port_start, port_end = raw_range
        if not (1 <= port_start <= port_end <= 65535):
            raise ManifestError("port_range는 1~65535 사이의 오름차순 범위여야 합니다.")

        ready_url = data.get("ready_url")
        if ready_url is not None and not isinstance(ready_url, str):
            raise ManifestError("ready_url은 문자열이어야 합니다.")

        api_base = data.get("api_base", f"http://127.0.0.1:{PLACEHOLDER}")
        if not isinstance(api_base, str):
            raise ManifestError("api_base는 문자열이어야 합니다.")

        return cls(
            id=app_id,
            name=name,
            version=version,
            mode=mode,
            repository=repository,
            ref=ref,
            app_exe=app_exe,
            app_html=app_html,
            url=url,
            args=args,
            port_range=(port_start, port_end),
            ready_url=ready_url,
            api_base=api_base,
            window=WindowOptions.from_dict(data),
        )

    def to_toml(self) -> str:
        lines = [
            f'id = "{_toml_escape(self.id)}"',
            f'name = "{_toml_escape(self.name)}"',
            f'version = "{_toml_escape(self.version)}"',
            f'mode = "{_toml_escape(self.mode)}"',
        ]
        if self.repository is not None:
            lines.append(f'repository = "{_toml_escape(self.repository)}"')
            lines.append(f'ref = "{_toml_escape(self.ref)}"')
        if self.app_exe is not None:
            lines.append(f'app_exe = "{_toml_escape(self.app_exe)}"')
        if self.app_html is not None:
            lines.append(f'app_html = "{_toml_escape(self.app_html)}"')
        if self.url is not None:
            lines.append(f'url = "{_toml_escape(self.url)}"')

        lines.extend(
            [
                "",
                "args = [" + ", ".join(f'"{_toml_escape(arg)}"' for arg in self.args) + "]",
                f"port_range = [{self.port_range[0]}, {self.port_range[1]}]",
                f'api_base = "{_toml_escape(self.api_base)}"',
            ]
        )
        if self.ready_url:
            lines.append(f'ready_url = "{_toml_escape(self.ready_url)}"')
        lines.extend(
            [
                "",
                "[window]",
                f"borderless = {_toml_bool(self.window.borderless)}",
                f"fullscreen = {_toml_bool(self.window.fullscreen)}",
                f"transparent = {_toml_bool(self.window.transparent)}",
                f'level = "{self.window.level}"',
            ]
        )
        return "\n".join(lines) + "\n"

    def args_for_port(self, port: int) -> list[str]:
        return [arg.replace(PLACEHOLDER, str(port)) for arg in self.args]

    def api_base_for_port(self, port: int) -> str:
        return self.api_base.replace(PLACEHOLDER, str(port))

    def ready_url_for_port(self, port: int) -> str | None:
        if self.ready_url is None:
            return None
        return self.ready_url.replace(PLACEHOLDER, str(port))


def _optional_plain_str(data: dict[str, Any], key: str, default: str | None) -> str | None:
    value = data.get(key, default)
    if value is None:
        return None
    if not isinstance(value, str) or not value.strip():
        raise ManifestError(f"{key}는 문자열이어야 합니다.")
    return value


def _normalize_repository(repository: str) -> str:
    repository = repository.strip().removesuffix(".git").strip("/")
    if repository.startswith("https://github.com/") or repository.startswith("http://github.com/"):
        raise ManifestError('repository는 URL이 아니라 "owner/repo" 형식이어야 합니다.')
    if "/" not in repository or not GITHUB_REPO_RE.fullmatch(repository):
        raise ManifestError('repository는 "owner/repo" 형식이어야 합니다.')
    return repository


def _optional_bool(root: dict[str, Any], window: dict[str, Any], key: str, default: bool) -> bool:
    value = window.get(key, root.get(key, default))
    if not isinstance(value, bool):
        raise ManifestError(f"window.{key}은 true 또는 false여야 합니다.")
    return value


def _optional_str(
    root: dict[str, Any],
    window: dict[str, Any],
    root_key: str,
    window_key: str,
    default: str,
) -> str:
    value = window.get(window_key, root.get(root_key, default))
    if not isinstance(value, str):
        raise ManifestError(f"window.{window_key}은 문자열이어야 합니다.")
    return value


def _toml_escape(value: str) -> str:
    return value.replace("\\", "\\\\").replace('"', '\\"')


def _toml_bool(value: bool) -> str:
    return "true" if value else "false"


def _slugify(value: str) -> str:
    slug = re.sub(r"[^a-zA-Z0-9_.-]+", "-", value.strip()).strip("-_.")
    if not slug:
        raise ManifestError("id를 자동 생성할 수 없습니다.")
    if not slug[0].isalnum():
        slug = f"app-{slug}"
    return slug[:80]


def _find_zip_metadata(archive: zipfile.ZipFile) -> str | None:
    names = [name for name in archive.namelist() if not name.endswith("/")]
    for name in names:
        if Path(name).name.lower() == "metadata.toml":
            return name
    return None
