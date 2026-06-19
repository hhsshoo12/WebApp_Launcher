from __future__ import annotations

from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from .manifest import ManifestError, WapkManifest, WindowOptions


class WebAppCreator(ttk.Frame):
    def __init__(self, master, status: tk.StringVar) -> None:
        super().__init__(master, padding=12)
        self.status = status
        self.mode = tk.StringVar(value="backend")
        self.repository = tk.StringVar()
        self.ref = tk.StringVar(value="main")
        self.app_id = tk.StringVar()
        self.name = tk.StringVar()
        self.version = tk.StringVar(value="0.1.0")
        self.app_exe = tk.StringVar(value="dist/app.exe")
        self.app_html = tk.StringVar(value="app.html")
        self.url = tk.StringVar(value="https://google.com")
        self.args = tk.StringVar(value="--port {PORT}")
        self.port_start = tk.StringVar(value="52000")
        self.port_end = tk.StringVar(value="52500")
        self.ready_url = tk.StringVar(value="http://127.0.0.1:{PORT}/health")
        self.api_base = tk.StringVar(value="http://127.0.0.1:{PORT}")
        self.borderless = tk.BooleanVar(value=False)
        self.fullscreen = tk.BooleanVar(value=False)
        self.transparent = tk.BooleanVar(value=False)
        self.window_level = tk.StringVar(value="normal")
        self._build()
        self._mode_changed()

    def _build(self) -> None:
        form = ttk.Frame(self)
        form.pack(fill=tk.BOTH, expand=True)
        self._row(form, 0, "모드", self._mode_widget(form))
        self._row(form, 1, "GitHub 저장소", ttk.Entry(form, textvariable=self.repository), "owner/repo")
        self._row(form, 2, "브랜치/태그", ttk.Entry(form, textvariable=self.ref))
        self._row(form, 3, "앱 ID", ttk.Entry(form, textvariable=self.app_id), "비워두면 이름/저장소에서 자동 생성")
        self._row(form, 4, "앱 이름", ttk.Entry(form, textvariable=self.name))
        self._row(form, 5, "버전", ttk.Entry(form, textvariable=self.version))
        self.exe_row = self._row(form, 6, "실행 파일", ttk.Entry(form, textvariable=self.app_exe), "레포 안 상대경로")
        self.html_row = self._row(form, 7, "HTML 파일", ttk.Entry(form, textvariable=self.app_html), "레포 안 상대경로")
        self.url_row = self._row(form, 8, "온라인 URL", ttk.Entry(form, textvariable=self.url))
        self.args_row = self._row(form, 9, "실행 인자", ttk.Entry(form, textvariable=self.args), "공백으로 구분, 포트는 {PORT}")
        ports = ttk.Frame(form)
        ttk.Entry(ports, textvariable=self.port_start, width=10).pack(side=tk.LEFT)
        ttk.Label(ports, text=" ~ ").pack(side=tk.LEFT)
        ttk.Entry(ports, textvariable=self.port_end, width=10).pack(side=tk.LEFT)
        self.port_row = self._row(form, 10, "포트 범위", ports)
        self.ready_row = self._row(form, 11, "준비 URL", ttk.Entry(form, textvariable=self.ready_url))
        self.api_row = self._row(form, 12, "API Base", ttk.Entry(form, textvariable=self.api_base))

        window = ttk.LabelFrame(form, text="창 옵션", padding=8)
        window.grid(row=13, column=0, columnspan=3, sticky="ew", pady=(10, 0))
        ttk.Checkbutton(window, text="테두리 없는 창", variable=self.borderless).pack(side=tk.LEFT)
        ttk.Checkbutton(window, text="전체화면", variable=self.fullscreen).pack(side=tk.LEFT, padx=(10, 0))
        ttk.Checkbutton(window, text="투명 배경", variable=self.transparent).pack(side=tk.LEFT, padx=(10, 0))
        ttk.Combobox(window, textvariable=self.window_level, values=("normal", "top", "bottom"), width=10, state="readonly").pack(side=tk.LEFT, padx=(10, 0))

        ttk.Button(self, text="WAPK 저장", command=self.save_wapk).pack(anchor=tk.E, pady=(10, 0))

    def _mode_widget(self, master) -> ttk.Frame:
        frame = ttk.Frame(master)
        for label, value in (("백엔드+HTML", "backend"), ("HTML", "html"), ("온라인", "online")):
            ttk.Radiobutton(frame, text=label, value=value, variable=self.mode, command=self._mode_changed).pack(side=tk.LEFT)
        return frame

    def _row(self, master, row: int, label: str, widget, hint: str = "") -> tuple[ttk.Label, object, ttk.Label]:
        label_widget = ttk.Label(master, text=label)
        label_widget.grid(row=row, column=0, sticky="w", pady=3)
        widget.grid(row=row, column=1, sticky="ew", pady=3)
        hint_widget = ttk.Label(master, text=hint, foreground="#666")
        hint_widget.grid(row=row, column=2, sticky="w", padx=(8, 0), pady=3)
        master.columnconfigure(1, weight=1)
        return label_widget, widget, hint_widget

    def _mode_changed(self) -> None:
        mode = self.mode.get()
        self._show_row(self.exe_row, mode == "backend")
        self._show_row(self.html_row, mode in {"backend", "html"})
        self._show_row(self.url_row, mode == "online")
        for row in (self.args_row, self.port_row, self.ready_row, self.api_row):
            self._show_row(row, mode == "backend")

    def _show_row(self, row: tuple[ttk.Label, object, ttk.Label], visible: bool) -> None:
        for widget in row:
            if visible:
                widget.grid()
            else:
                widget.grid_remove()

    def save_wapk(self) -> None:
        try:
            manifest = self._manifest()
        except (ManifestError, ValueError) as exc:
            messagebox.showerror("WAPK 제작 실패", str(exc))
            return
        file_name = filedialog.asksaveasfilename(
            title="WAPK 저장",
            defaultextension=".wapk",
            filetypes=[("WAPK files", "*.wapk"), ("TOML files", "*.toml"), ("All files", "*.*")],
            initialfile=f"{manifest.id}.wapk",
        )
        if not file_name:
            return
        Path(file_name).write_text(manifest.to_toml(), encoding="utf-8")
        self.status.set(f"{manifest.name} WAPK 저장됨")

    def _manifest(self) -> WapkManifest:
        mode = self.mode.get()
        data: dict[str, object] = {
            "mode": mode,
            "repository": self.repository.get(),
            "ref": self.ref.get(),
            "name": self.name.get(),
            "version": self.version.get(),
            "window": {
                "borderless": self.borderless.get(),
                "fullscreen": self.fullscreen.get(),
                "transparent": self.transparent.get(),
                "level": self.window_level.get(),
            },
        }
        if self.app_id.get().strip():
            data["id"] = self.app_id.get()
        if mode == "backend":
            data.update(
                {
                    "app_exe": self.app_exe.get(),
                    "app_html": self.app_html.get(),
                    "args": self.args.get().split(),
                    "port_range": [int(self.port_start.get()), int(self.port_end.get())],
                    "ready_url": self.ready_url.get(),
                    "api_base": self.api_base.get(),
                }
            )
        elif mode == "html":
            data["app_html"] = self.app_html.get()
        else:
            data["url"] = self.url.get()
        return WapkManifest.from_dict(data)
