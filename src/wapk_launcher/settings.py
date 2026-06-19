from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import json

from .paths import PROJECT_ROOT


SETTINGS_PATH = PROJECT_ROOT / "settings.json"


@dataclass
class GlobalSettings:
    show_backend_console: bool = False
    show_browser_console: bool = False

    @classmethod
    def load(cls) -> "GlobalSettings":
        if not SETTINGS_PATH.exists():
            return cls()
        try:
            data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
        except Exception:
            return cls()
        return cls(
            show_backend_console=bool(data.get("show_backend_console", False)),
            show_browser_console=bool(data.get("show_browser_console", False)),
        )

    def save(self) -> None:
        SETTINGS_PATH.parent.mkdir(parents=True, exist_ok=True)
        SETTINGS_PATH.write_text(
            json.dumps(
                {
                    "show_backend_console": self.show_backend_console,
                    "show_browser_console": self.show_browser_console,
                },
                ensure_ascii=False,
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )
