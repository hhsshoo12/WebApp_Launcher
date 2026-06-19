from __future__ import annotations

from pathlib import Path
import argparse
from concurrent.futures import Future, ThreadPoolExecutor
import ctypes
import sys
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from .manifest import ManifestError, WapkManifest
from .runner import AppRunner
from .settings import GlobalSettings
from .storage import InstalledApp, delete_app, install_manifest, list_installed


class LauncherApp(tk.Tk):
    def __init__(self, initial_wapk: Path | None = None) -> None:
        super().__init__()
        self.title("WAPK Launcher")
        self.geometry("760x460")
        self.minsize(680, 380)
        self.settings = GlobalSettings.load()
        self.runner = AppRunner(self.settings)
        self.apps: list[InstalledApp] = []
        self.executor = ThreadPoolExecutor(max_workers=2, thread_name_prefix="wapk-worker")
        self.busy_count = 0

        self._build_ui()
        self.refresh()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        self.after(1000, self._poll_running_apps)

        if initial_wapk:
            self.after(100, lambda: self.install_wapk(initial_wapk, run_after=True))

    def _build_ui(self) -> None:
        notebook = ttk.Notebook(self)
        notebook.pack(fill=tk.BOTH, expand=True)

        root = ttk.Frame(notebook, padding=12)
        settings_tab = ttk.Frame(notebook, padding=12)
        notebook.add(root, text="앱")
        notebook.add(settings_tab, text="설정")

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

        self.open_button = ttk.Button(buttons, text="WAPK 열기", command=self.pick_wapk)
        self.run_button = ttk.Button(buttons, text="실행", command=self.run_selected)
        self.stop_button = ttk.Button(buttons, text="종료", command=self.stop_selected)
        self.delete_button = ttk.Button(buttons, text="삭제", command=self.delete_selected)
        self.refresh_button = ttk.Button(buttons, text="새로고침", command=self.refresh)

        self.open_button.pack(side=tk.LEFT)
        self.run_button.pack(side=tk.LEFT, padx=(8, 0))
        self.stop_button.pack(side=tk.LEFT, padx=(8, 0))
        self.delete_button.pack(side=tk.LEFT, padx=(8, 0))
        self.refresh_button.pack(side=tk.LEFT, padx=(8, 0))

        self.status = tk.StringVar(value="준비됨")
        ttk.Label(root, textvariable=self.status, anchor=tk.W).pack(fill=tk.X, pady=(10, 0))

        self.show_backend_console = tk.BooleanVar(value=self.settings.show_backend_console)
        self.show_browser_console = tk.BooleanVar(value=self.settings.show_browser_console)
        ttk.Checkbutton(
            settings_tab,
            text="콘솔이 존재한다면 띄우기",
            variable=self.show_backend_console,
            command=self._save_settings,
        ).pack(anchor=tk.W)
        ttk.Checkbutton(
            settings_tab,
            text="브라우저 콘솔 띄우기",
            variable=self.show_browser_console,
            command=self._save_settings,
        ).pack(anchor=tk.W, pady=(8, 0))

    def _save_settings(self) -> None:
        self.settings.show_backend_console = bool(self.show_backend_console.get())
        self.settings.show_browser_console = bool(self.show_browser_console.get())
        self.settings.save()
        self.runner.settings = self.settings
        self.status.set("설정 저장됨")

    def pick_wapk(self) -> None:
        file_name = filedialog.askopenfilename(
            title="WAPK 파일 열기",
            filetypes=[("WAPK files", "*.wapk"), ("TOML files", "*.toml"), ("All files", "*.*")],
        )
        if file_name:
            self.install_wapk(Path(file_name), run_after=False)

    def install_wapk(self, path: Path, run_after: bool) -> None:
        def task() -> InstalledApp:
            manifest = WapkManifest.load(path)
            self._set_status_async(f"{manifest.name} 설치 중...")
            return install_manifest(manifest)

        def done(installed: InstalledApp) -> None:
            self.refresh()
            self.status.set(f"{installed.manifest.name} 설치 완료")
            if run_after:
                self._run(installed.manifest)

        self._submit_task(task, done, "WAPK 설치 실패", "설치 실패")

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

    def _poll_running_apps(self) -> None:
        with self.runner.lock:
            before = set(self.runner.running)
        self.runner.statuses()
        with self.runner.lock:
            after = set(self.runner.running)
        if before != after:
            stopped_ids = before - after
            self.refresh()
            stopped = [app.manifest.name for app in self.apps if app.manifest.id in stopped_ids]
            if stopped:
                self.status.set(f"{', '.join(stopped)} 종료됨")
        self.after(1000, self._poll_running_apps)

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
        def task():
            return self.runner.start(manifest)

        def done(running) -> None:
            self.status.set(f"{manifest.name} 실행 중: port {running.port}")
            self.refresh()

        self.status.set(f"{manifest.name} 실행 준비 중...")
        self._submit_task(task, done, "앱 실행 실패", "실행 실패")

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

        def task() -> None:
            self.runner.stop(app.manifest.id)
            delete_app(app.manifest.id)

        def done(_: None) -> None:
            self.status.set(f"{app.manifest.name} 삭제됨")
            self.refresh()

        self.status.set(f"{app.manifest.name} 삭제 중...")
        self._submit_task(task, done, "앱 삭제 실패", "삭제 실패")

    def _submit_task(self, task, on_success, error_title: str, error_status: str) -> None:
        self._set_busy(True)
        future = self.executor.submit(task)

        def callback(done_future: Future) -> None:
            self.after(0, lambda: self._finish_task(done_future, on_success, error_title, error_status))

        future.add_done_callback(callback)

    def _finish_task(self, future: Future, on_success, error_title: str, error_status: str) -> None:
        self._set_busy(False)
        try:
            result = future.result()
        except (ManifestError, Exception) as exc:
            self.status.set(error_status)
            messagebox.showerror(error_title, str(exc))
            return
        on_success(result)

    def _set_busy(self, busy: bool) -> None:
        self.busy_count += 1 if busy else -1
        if self.busy_count < 0:
            self.busy_count = 0
        state = tk.DISABLED if self.busy_count else tk.NORMAL
        for button in (self.open_button, self.run_button, self.delete_button, self.refresh_button):
            button.configure(state=state)

    def _set_status_async(self, text: str) -> None:
        self.after(0, lambda: self.status.set(text))

    def _on_close(self) -> None:
        self.runner.stop_all()
        self.executor.shutdown(wait=False, cancel_futures=True)
        self.destroy()


def main(argv: list[str] | None = None) -> None:
    _enable_high_dpi()
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
            runner = AppRunner(GlobalSettings.load())
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


def _enable_high_dpi() -> None:
    if sys.platform != "win32":
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass
