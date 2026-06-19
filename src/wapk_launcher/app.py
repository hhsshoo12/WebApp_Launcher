from __future__ import annotations

from pathlib import Path
import argparse
import sys
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from .manifest import ManifestError, WapkManifest
from .runner import AppRunner
from .storage import InstalledApp, delete_app, install_manifest, list_installed


class LauncherApp(tk.Tk):
    def __init__(self, initial_wapk: Path | None = None) -> None:
        super().__init__()
        self.title("WAPK Launcher")
        self.geometry("760x460")
        self.minsize(680, 380)
        self.runner = AppRunner()
        self.apps: list[InstalledApp] = []

        self._build_ui()
        self.refresh()
        self.protocol("WM_DELETE_WINDOW", self._on_close)

        if initial_wapk:
            self.after(100, lambda: self.install_wapk(initial_wapk, run_after=True))

    def _build_ui(self) -> None:
        root = ttk.Frame(self, padding=12)
        root.pack(fill=tk.BOTH, expand=True)

        self.tree = ttk.Treeview(
            root,
            columns=("name", "version", "installed", "running"),
            show="headings",
            selectmode="browse",
        )
        self.tree.heading("name", text="앱 이름")
        self.tree.heading("version", text="버전")
        self.tree.heading("installed", text="설치 상태")
        self.tree.heading("running", text="실행 상태")
        self.tree.column("name", width=300)
        self.tree.column("version", width=110, anchor=tk.CENTER)
        self.tree.column("installed", width=120, anchor=tk.CENTER)
        self.tree.column("running", width=120, anchor=tk.CENTER)
        self.tree.pack(fill=tk.BOTH, expand=True)

        buttons = ttk.Frame(root)
        buttons.pack(fill=tk.X, pady=(10, 0))

        ttk.Button(buttons, text="WAPK 열기", command=self.pick_wapk).pack(side=tk.LEFT)
        ttk.Button(buttons, text="실행", command=self.run_selected).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(buttons, text="종료", command=self.stop_selected).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(buttons, text="삭제", command=self.delete_selected).pack(side=tk.LEFT, padx=(8, 0))
        ttk.Button(buttons, text="새로고침", command=self.refresh).pack(side=tk.LEFT, padx=(8, 0))

        self.status = tk.StringVar(value="준비됨")
        ttk.Label(root, textvariable=self.status, anchor=tk.W).pack(fill=tk.X, pady=(10, 0))

    def pick_wapk(self) -> None:
        file_name = filedialog.askopenfilename(
            title="WAPK 파일 열기",
            filetypes=[("WAPK files", "*.wapk"), ("TOML files", "*.toml"), ("All files", "*.*")],
        )
        if file_name:
            self.install_wapk(Path(file_name), run_after=False)

    def install_wapk(self, path: Path, run_after: bool) -> None:
        try:
            manifest = WapkManifest.load(path)
            self.status.set(f"{manifest.name} 설치 중...")
            self.update_idletasks()
            installed = install_manifest(manifest)
            self.refresh()
            self.status.set(f"{installed.manifest.name} 설치 완료")
            if run_after:
                self._run(installed.manifest)
        except (ManifestError, Exception) as exc:
            self.status.set("설치 실패")
            messagebox.showerror("WAPK 설치 실패", str(exc))

    def refresh(self) -> None:
        self.runner.statuses()
        self.apps = list_installed()
        self.tree.delete(*self.tree.get_children())
        for app in self.apps:
            self.tree.insert(
                "",
                tk.END,
                iid=app.manifest.id,
                values=(
                    app.manifest.name,
                    app.manifest.version,
                    "설치됨" if app.installed else "불완전",
                    "실행 중" if self.runner.is_running(app.manifest.id) else "정지",
                ),
            )

    def selected_app(self) -> InstalledApp | None:
        selected = self.tree.selection()
        if not selected:
            messagebox.showinfo("선택 필요", "앱을 먼저 선택하세요.")
            return None
        app_id = selected[0]
        return next((app for app in self.apps if app.manifest.id == app_id), None)

    def run_selected(self) -> None:
        app = self.selected_app()
        if app:
            self._run(app.manifest)

    def _run(self, manifest: WapkManifest) -> None:
        try:
            running = self.runner.start(manifest)
            self.status.set(f"{manifest.name} 실행 중: port {running.port}")
            self.refresh()
        except Exception as exc:
            self.status.set("실행 실패")
            messagebox.showerror("앱 실행 실패", str(exc))

    def stop_selected(self) -> None:
        app = self.selected_app()
        if not app:
            return
        self.runner.stop(app.manifest.id)
        self.status.set(f"{app.manifest.name} 종료됨")
        self.refresh()

    def delete_selected(self) -> None:
        app = self.selected_app()
        if not app:
            return
        if not messagebox.askyesno("앱 삭제", f"{app.manifest.name}을 삭제할까요?"):
            return
        self.runner.stop(app.manifest.id)
        delete_app(app.manifest.id)
        self.status.set(f"{app.manifest.name} 삭제됨")
        self.refresh()

    def _on_close(self) -> None:
        self.runner.stop_all()
        self.destroy()


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description="WAPK Launcher MVP")
    parser.add_argument("wapk", nargs="?", help="열거나 설치할 .wapk TOML 파일")
    parser.add_argument("--install", action="store_true", help="GUI 없이 .wapk를 설치하고 종료")
    parser.add_argument("--run", action="store_true", help="GUI 없이 .wapk를 설치한 뒤 실행")
    parser.add_argument("--list", action="store_true", help="설치된 앱 목록을 출력하고 종료")
    parsed = parser.parse_args(sys.argv[1:] if argv is None else argv)

    if parsed.list:
        for app in list_installed():
            print(f"{app.manifest.id}\t{app.manifest.name}\t{app.manifest.version}\t{app.installed}")
        return

    if parsed.install or parsed.run:
        if not parsed.wapk:
            parser.error("--install/--run에는 .wapk 경로가 필요합니다.")
        manifest = WapkManifest.load(Path(parsed.wapk).resolve())
        installed = install_manifest(manifest)
        print(f"installed {installed.manifest.id} {installed.manifest.version}")
        if parsed.run:
            runner = AppRunner()
            try:
                running = runner.start(installed.manifest)
                print(f"running {installed.manifest.id} port={running.port}")
                running.backend.wait()
            finally:
                runner.stop_all()
        return

    initial = Path(parsed.wapk).resolve() if parsed.wapk else None
    app = LauncherApp(initial)
    app.mainloop()
