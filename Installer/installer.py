from __future__ import annotations

import base64
import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

try:
    import winreg
except ImportError:
    winreg = None


PRODUCT_NAME = "WebApp Launcher"
PAYLOAD_DIR_NAME = "payload"
WINDOW_WIDTH = 680
WINDOW_HEIGHT = 430
SIDEBAR_WIDTH = 150
INSTALL_STATE_FILE = ".webapp-launcher-install.json"
REGISTRY_KEY = r"Software\WebAppLauncher"
REGISTRY_INSTALL_VALUE = "InstallLocation"


class InstallCancelled(Exception):
    pass


def bundled_path(name: str) -> Path:
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
    return base / name


def default_install_dir() -> Path:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / "Programs" / "WebAppLauncher"
    return Path.home() / "AppData" / "Local" / "Programs" / "WebAppLauncher"


def start_menu_shortcut() -> Path:
    app_data = os.environ.get("APPDATA")
    if app_data:
        programs = Path(app_data) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    else:
        programs = Path.home() / "AppData" / "Roaming" / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    return programs / f"{PRODUCT_NAME}.lnk"


def read_registered_install_dir() -> Path | None:
    if winreg is None:
        return None
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY) as key:
            value, _ = winreg.QueryValueEx(key, REGISTRY_INSTALL_VALUE)
    except OSError:
        return None
    return Path(value) if isinstance(value, str) and value.strip() else None


def write_install_state(destination: Path) -> None:
    state = {
        "format": 1,
        "product": PRODUCT_NAME,
        "install_location": str(destination.resolve()),
    }
    (destination / INSTALL_STATE_FILE).write_text(
        json.dumps(state, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    if winreg is not None:
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY) as key:
            winreg.SetValueEx(
                key,
                REGISTRY_INSTALL_VALUE,
                0,
                winreg.REG_SZ,
                str(destination.resolve()),
            )


def clear_registered_install_dir() -> None:
    if winreg is None:
        return
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, REGISTRY_KEY)
    except FileNotFoundError:
        pass


def is_installation_dir(path: Path) -> bool:
    return (
        path.is_absolute()
        and (path / "WebAppLauncher.exe").is_file()
        and (
            (path / INSTALL_STATE_FILE).is_file()
            or (path / "WebAppLauncher.Bootstrapper.exe").is_file()
        )
    )


def find_existing_installation() -> Path | None:
    candidates = [read_registered_install_dir(), default_install_dir()]
    seen: set[str] = set()
    for candidate in candidates:
        if candidate is None:
            continue
        normalized = os.path.normcase(os.path.abspath(candidate))
        if normalized in seen:
            continue
        seen.add(normalized)
        if is_installation_dir(candidate):
            return candidate.resolve()
    return None


def remove_installation(destination: Path, progress: object | None = None) -> None:
    if not is_installation_dir(destination):
        raise ValueError("WebApp Launcher 설치 폴더로 확인되지 않아 삭제하지 않았습니다.")

    entries = sorted(
        destination.rglob("*"),
        key=lambda path: (len(path.parts), path.is_dir()),
        reverse=True,
    )
    total = len(entries) + 1
    completed = 0
    for entry in entries:
        if entry.is_dir() and not entry.is_symlink():
            entry.rmdir()
        else:
            entry.unlink(missing_ok=True)
        completed += 1
        if callable(progress):
            progress(completed, total, entry.name)
    destination.rmdir()
    start_menu_shortcut().unlink(missing_ok=True)
    clear_registered_install_dir()
    if callable(progress):
        progress(total, total, destination.name)


def create_shortcut(shortcut: Path, target: Path, working_dir: Path) -> None:
    shortcut.parent.mkdir(parents=True, exist_ok=True)
    script = (
        "$shell = New-Object -ComObject WScript.Shell\n"
        f"$shortcut = $shell.CreateShortcut('{escape_powershell(str(shortcut))}')\n"
        f"$shortcut.TargetPath = '{escape_powershell(str(target))}'\n"
        f"$shortcut.WorkingDirectory = '{escape_powershell(str(working_dir))}'\n"
        f"$shortcut.IconLocation = '{escape_powershell(str(target))},0'\n"
        "$shortcut.Save()\n"
    )
    encoded = base64.b64encode(script.encode("utf-16le")).decode("ascii")
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
        check=True,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )


def escape_powershell(value: str) -> str:
    return value.replace("'", "''")


def webapp_root() -> Path:
    return Path.home() / ".webapp"


def find_running_launcher_processes() -> list[int]:
    """Return PIDs of running WebAppLauncher.exe processes (excluding the current process)."""
    current_pid = os.getpid()
    pids: list[int] = []
    try:
        result = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq WebAppLauncher.exe", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=False,
        )
        for line in result.stdout.splitlines():
            line = line.strip()
            if not line or not line.startswith('"'):
                continue
            parts = [part.strip('"') for part in line.split('","')]
            if len(parts) < 2:
                continue
            try:
                pid = int(parts[1])
                if pid != current_pid:
                    pids.append(pid)
            except ValueError:
                continue
    except FileNotFoundError:
        pass
    return pids


def kill_running_launcher_processes() -> bool:
    """Terminate running WebAppLauncher.exe processes. Returns True if successful."""
    pids = find_running_launcher_processes()
    if not pids:
        return True
    try:
        subprocess.run(
            ["taskkill", "/F", "/IM", "WebAppLauncher.exe"],
            capture_output=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
            check=False,
        )
        return len(find_running_launcher_processes()) == 0
    except FileNotFoundError:
        return False


def is_runtime_installed() -> bool:
    """Check whether .webapp runtime or tools directories are populated."""
    root = webapp_root()
    for name in ("runtime", "tools"):
        path = root / name
        if path.is_dir():
            try:
                if any(path.iterdir()):
                    return True
            except OSError:
                pass
    return False


def remove_runtime_data(progress: object | None = None) -> None:
    """Remove .webapp/runtime and .webapp/tools directories."""
    root = webapp_root()
    targets = [root / "runtime", root / "tools"]
    for target in targets:
        if target.is_dir():
            shutil.rmtree(target, ignore_errors=True)
            if callable(progress):
                progress(target.name)


def remove_webapp_apps(progress: object | None = None) -> None:
    """Remove installed web apps under .webapp/app."""
    app_dir = webapp_root() / "app"
    if app_dir.is_dir():
        shutil.rmtree(app_dir, ignore_errors=True)
        if callable(progress):
            progress("app")


class InstallerApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title(f"{PRODUCT_NAME} 설치")
        self.root.resizable(False, False)

        self.messages: queue.Queue[tuple[str, object]] = queue.Queue()
        self.bootstrapper: subprocess.Popen[str] | None = None
        self.cancel_event = threading.Event()
        self.running = False
        self.close_after_cancel = False
        self.current_page = 0
        self.operation = "install"
        self.installed_launcher: Path | None = None
        self.log_messages: list[str] = []
        self.log_window: tk.Toplevel | None = None
        self.log_text: tk.Text | None = None
        self.sidebar_logo: tk.PhotoImage | None = None
        self.overall_percent = tk.DoubleVar(value=0)
        self.overall_percent_text = tk.StringVar(value="0%")
        self.detail_item = tk.StringVar(value="-")
        self.detail_phase = tk.StringVar(value="대기 중")
        self.download_percent = tk.DoubleVar(value=0)
        self.download_text = tk.StringVar(value="-")
        self.extract_percent = tk.DoubleVar(value=0)
        self.extract_text = tk.StringVar(value="-")

        self.existing_install_dir = find_existing_installation()
        self.install_dir = tk.StringVar(
            value=str(self.existing_install_dir or default_install_dir())
        )
        self.install_runtimes = tk.BooleanVar(value=True)
        self.start_menu = tk.BooleanVar(value=True)
        self.launch_after_install = tk.BooleanVar(value=True)
        self.status = tk.StringVar(value="설치를 준비하고 있습니다.")
        self.cleanup_option = tk.StringVar(value="keep")

        self._configure_styles()
        self._build_shell()
        if self.existing_install_dir is not None:
            self._show_maintenance_page()
        else:
            self._show_page(0)
        self._center_window()
        self.root.protocol("WM_DELETE_WINDOW", self._close)
        self.root.after(100, self._drain_messages)

    def _configure_styles(self) -> None:
        style = ttk.Style(self.root)
        style.configure("Wizard.TButton", padding=(12, 4))
        style.configure("Wizard.Horizontal.TProgressbar", thickness=14)

    def _build_shell(self) -> None:
        self.root.configure(background="#f4f4f4")

        body = tk.Frame(self.root, background="#ffffff")
        body.pack(fill=tk.BOTH, expand=True)

        self.sidebar = tk.Canvas(
            body,
            width=SIDEBAR_WIDTH,
            height=364,
            highlightthickness=0,
            borderwidth=0,
        )
        self.sidebar.pack(side=tk.LEFT, fill=tk.Y)
        self._draw_sidebar()

        self.content = tk.Frame(body, background="#ffffff")
        self.content.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)

        separator = ttk.Separator(self.root, orient=tk.HORIZONTAL)
        separator.pack(fill=tk.X)

        self.footer = tk.Frame(self.root, height=64, background="#f4f4f4")
        self.footer.pack(fill=tk.X)
        self.footer.pack_propagate(False)

        self.step_text_id = self.sidebar.create_text(
            18,
            334,
            anchor=tk.W,
            fill="#eeeeee",
            font=("Segoe UI", 9),
            text="1 / 5",
        )

    def _draw_sidebar(self) -> None:
        top = (24, 24, 26)
        bottom = (145, 145, 148)
        height = 364
        for y in range(height):
            ratio = y / max(1, height - 1)
            eased = ratio * ratio * (3 - 2 * ratio)
            color = tuple(round(a + (b - a) * eased) for a, b in zip(top, bottom))
            self.sidebar.create_line(
                0,
                y,
                SIDEBAR_WIDTH,
                y,
                fill=f"#{color[0]:02x}{color[1]:02x}{color[2]:02x}",
            )

        logo_path = bundled_path("assets/logo.png")
        if logo_path.is_file():
            self.sidebar_logo = tk.PhotoImage(file=str(logo_path)).subsample(4, 4)
            self.sidebar.create_image(75, 72, image=self.sidebar_logo)
        self.sidebar.create_text(
            75,
            132,
            text="WEBAPP",
            fill="#f5f5f5",
            font=("Segoe UI Semibold", 9),
        )
        self.sidebar.create_text(
            75,
            148,
            text="LAUNCHER",
            fill="#d5d5d5",
            font=("Segoe UI", 8),
        )

    def _center_window(self) -> None:
        self.root.update_idletasks()
        x = max(0, (self.root.winfo_screenwidth() - WINDOW_WIDTH) // 2)
        y = max(0, (self.root.winfo_screenheight() - WINDOW_HEIGHT) // 2)
        self.root.geometry(f"{WINDOW_WIDTH}x{WINDOW_HEIGHT}+{x}+{y}")

    def _clear_content(self) -> None:
        for child in self.content.winfo_children():
            child.destroy()
        for child in self.footer.winfo_children():
            child.destroy()

    def _show_page(self, page: int) -> None:
        self.current_page = page
        self._clear_content()
        self.sidebar.itemconfigure(self.step_text_id, text=f"{page + 1} / 5")

        pages = (
            self._build_welcome_page,
            self._build_location_page,
            self._build_options_page,
            self._build_progress_page,
            self._build_complete_page,
        )
        pages[page]()

    def _show_maintenance_page(self) -> None:
        self.current_page = -1
        self._clear_content()
        self.sidebar.itemconfigure(self.step_text_id, text="유지 관리")
        frame = self._page_frame(
            f"{PRODUCT_NAME} 유지 관리",
            "WebApp Launcher가 이미 설치되어 있습니다. 수행할 작업을 선택하십시오.",
        )
        tk.Label(
            frame,
            text=f"설치 위치\n{self.install_dir.get()}",
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=430,
            background="#ffffff",
            foreground="#4b4b4f",
            font=("Segoe UI", 10),
        ).pack(fill=tk.X, pady=(30, 0))
        tk.Label(
            frame,
            text="복구는 프로그램 파일과 선택한 실행 환경을 다시 설치합니다.\n"
            "삭제 시 .webapp의 웹앱과 런타임 정리 옵션을 선택할 수 있습니다.",
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=430,
            background="#ffffff",
            foreground="#6a6a6f",
            font=("Segoe UI", 9),
        ).pack(fill=tk.X, pady=(18, 0))

        self._footer_button("마치기", self._close)
        self._footer_button("삭제", self._start_remove)
        self._footer_button("복구(재설치)", self._start_repair, default=True)

    def _page_frame(self, title: str, description: str = "") -> tk.Frame:
        frame = tk.Frame(self.content, background="#ffffff")
        frame.pack(fill=tk.BOTH, expand=True, padx=30, pady=(28, 20))
        tk.Label(
            frame,
            text=title,
            anchor=tk.W,
            background="#ffffff",
            foreground="#171719",
            font=("Segoe UI Semibold", 15),
        ).pack(fill=tk.X)
        if description:
            tk.Label(
                frame,
                text=description,
                anchor=tk.W,
                justify=tk.LEFT,
                wraplength=430,
                background="#ffffff",
                foreground="#4b4b4f",
                font=("Segoe UI", 10),
            ).pack(fill=tk.X, pady=(8, 0))
        return frame

    def _footer_button(
        self,
        text: str,
        command: object,
        *,
        side: str = tk.RIGHT,
        state: str = tk.NORMAL,
        default: bool = False,
    ) -> ttk.Button:
        button = ttk.Button(
            self.footer,
            text=text,
            command=command,
            state=state,
            style="Wizard.TButton",
        )
        button.pack(side=side, padx=(0, 10) if side == tk.RIGHT else (10, 0), pady=16)
        if default:
            button.focus_set()
        return button

    def _build_welcome_page(self) -> None:
        frame = self._page_frame(
            f"{PRODUCT_NAME} 설치 마법사",
            "이 마법사는 WebApp Launcher와 앱 실행에 필요한 환경을 설치합니다.",
        )
        tk.Label(
            frame,
            text="계속하려면 다음을 클릭하십시오.",
            anchor=tk.W,
            background="#ffffff",
            foreground="#4b4b4f",
            font=("Segoe UI", 10),
        ).pack(fill=tk.X, pady=(42, 0))

        self._footer_button("취소", self._close)
        self._footer_button("다음 >", lambda: self._show_page(1), default=True)

    def _build_location_page(self) -> None:
        frame = self._page_frame(
            "설치 위치 선택",
            "WebApp Launcher 프로그램 파일을 설치할 폴더를 선택하십시오.",
        )

        field = tk.Frame(frame, background="#ffffff")
        field.pack(fill=tk.X, pady=(32, 0))
        self.location_entry = ttk.Entry(field, textvariable=self.install_dir)
        self.location_entry.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Button(field, text="찾아보기...", command=self._browse).pack(side=tk.LEFT, padx=(8, 0))

        tk.Label(
            frame,
            text="앱과 런타임 데이터는 사용자 폴더의 .webapp에 별도로 저장됩니다.",
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=430,
            background="#ffffff",
            foreground="#6a6a6f",
            font=("Segoe UI", 9),
        ).pack(fill=tk.X, pady=(12, 0))

        self._footer_button("취소", self._close)
        self._footer_button("다음 >", self._validate_location, default=True)
        self._footer_button("< 뒤로", lambda: self._show_page(0))

    def _build_options_page(self) -> None:
        frame = self._page_frame(
            "설치 옵션",
            "필요한 항목을 선택한 다음 설치를 클릭하십시오.",
        )

        options = tk.Frame(frame, background="#ffffff")
        options.pack(fill=tk.X, pady=(26, 0))
        ttk.Checkbutton(
            options,
            text="전용 런타임 및 도구 설치",
            variable=self.install_runtimes,
        ).pack(anchor=tk.W, pady=5)
        ttk.Checkbutton(
            options,
            text="시작 메뉴 바로가기 만들기",
            variable=self.start_menu,
        ).pack(anchor=tk.W, pady=5)
        ttk.Checkbutton(
            options,
            text="설치 완료 후 런처 실행",
            variable=self.launch_after_install,
        ).pack(anchor=tk.W, pady=5)

        tk.Label(
            frame,
            text="전용 환경에는 WebView2, Python, Node.js, Git, uv 및 pnpm이 포함됩니다.",
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=430,
            background="#ffffff",
            foreground="#6a6a6f",
            font=("Segoe UI", 9),
        ).pack(fill=tk.X, pady=(16, 0))

        self._footer_button("취소", self._close)
        self._footer_button("설치", self._start_install, default=True)
        self._footer_button("< 뒤로", lambda: self._show_page(1))

    def _build_progress_page(self) -> None:
        removing = self.operation == "remove"
        frame = self._page_frame(
            "삭제 진행" if removing else "설치 진행",
            (
                "WebApp Launcher 프로그램 파일을 삭제하고 있습니다. 잠시 기다리십시오."
                if removing
                else "선택한 구성 요소를 설치하고 있습니다. 잠시 기다리십시오."
            ),
        )

        progress_row = tk.Frame(frame, background="#ffffff")
        progress_row.pack(fill=tk.X, pady=(34, 10))
        self.progress = ttk.Progressbar(
            progress_row,
            mode="determinate",
            maximum=100,
            variable=self.overall_percent,
            style="Wizard.Horizontal.TProgressbar",
        )
        self.progress.pack(side=tk.LEFT, fill=tk.X, expand=True)
        tk.Label(
            progress_row,
            textvariable=self.overall_percent_text,
            width=5,
            anchor=tk.E,
            background="#ffffff",
            foreground="#202024",
            font=("Segoe UI Semibold", 10),
        ).pack(side=tk.LEFT, padx=(10, 0))
        self.status_label = tk.Label(
            frame,
            textvariable=self.status,
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=430,
            background="#ffffff",
            foreground="#333337",
            font=("Segoe UI", 10),
        )
        self.status_label.pack(fill=tk.X)

        if not removing:
            ttk.Button(frame, text="세부 정보 보기", command=self._show_log).pack(anchor=tk.W, pady=(22, 0))

        self.cancel_button = self._footer_button(
            "취소",
            self._cancel_install,
            state=tk.DISABLED if removing else tk.NORMAL,
            default=not removing,
        )

    def _build_complete_page(self) -> None:
        removed = self.operation == "remove"
        frame = self._page_frame(
            "삭제 완료" if removed else "설치 완료",
            (
                f"{PRODUCT_NAME}가 삭제되었습니다."
                if removed
                else f"{PRODUCT_NAME} 설치가 완료되었습니다."
            ),
        )
        tk.Label(
            frame,
            text="마침을 클릭하여 설치 마법사를 종료하십시오.",
            anchor=tk.W,
            background="#ffffff",
            foreground="#4b4b4f",
            font=("Segoe UI", 10),
        ).pack(fill=tk.X, pady=(32, 0))
        if removed:
            tk.Label(
                frame,
                text="설치된 앱과 데이터가 저장된 사용자 폴더의 .webapp은 삭제하지 않았습니다.",
                anchor=tk.W,
                justify=tk.LEFT,
                wraplength=430,
                background="#ffffff",
                foreground="#6a6a6f",
                font=("Segoe UI", 9),
            ).pack(fill=tk.X, pady=(28, 0))
        else:
            ttk.Checkbutton(
                frame,
                text="지금 WebApp Launcher 실행",
                variable=self.launch_after_install,
            ).pack(anchor=tk.W, pady=(28, 0))

        self._footer_button("마침", self._finish, default=True)

    def _validate_location(self) -> None:
        destination = Path(os.path.expandvars(self.install_dir.get())).expanduser()
        if not destination.is_absolute():
            messagebox.showerror(PRODUCT_NAME, "설치 위치는 절대 경로여야 합니다.")
            return
        self.install_dir.set(str(destination))
        self._show_page(2)

    def _browse(self) -> None:
        selected = filedialog.askdirectory(
            title="설치 폴더 선택",
            initialdir=self.install_dir.get(),
        )
        if selected:
            self.install_dir.set(selected)

    def _start_install(self) -> None:
        self.operation = "install"
        destination = Path(os.path.expandvars(self.install_dir.get())).expanduser()
        self._begin_install(destination)

    def _start_repair(self) -> None:
        if self.existing_install_dir is None:
            messagebox.showerror(PRODUCT_NAME, "기존 설치 위치를 찾을 수 없습니다.")
            return

        if find_running_launcher_processes():
            if not messagebox.askyesno(
                PRODUCT_NAME,
                "앱이 실행되어 있습니다.\n앱을 종료하고 진행할까요?",
                icon="warning",
            ):
                return
            if not kill_running_launcher_processes():
                messagebox.showerror(
                    PRODUCT_NAME,
                    "실행 중인 앱을 종료할 수 없습니다.\n"
                    "직접 종료한 뒤 다시 시도하십시오.",
                )
                return

        self.operation = "repair"
        self.start_menu.set(True)

        if is_runtime_installed():
            answer = messagebox.askyesnocancel(
                PRODUCT_NAME,
                "이미 런타임이 설치되어 있습니다. 재설치하시겠습니까?\n\n"
                "예: 기존 런타임을 지우고 다시 설치\n"
                "아니오: 런타임 설치를 건너뛰고 프로그램만 복구\n"
                "취소: 복구를 취소",
                icon="question",
            )
            if answer is None:
                return
            self.install_runtimes.set(bool(answer))
            if answer:
                remove_runtime_data()
        else:
            self.install_runtimes.set(True)

        self._begin_install(self.existing_install_dir)

    def _begin_install(self, destination: Path) -> None:
        if self.running:
            return

        payload = bundled_path(PAYLOAD_DIR_NAME)
        if not destination.is_absolute():
            messagebox.showerror(PRODUCT_NAME, "설치 위치는 절대 경로여야 합니다.")
            self._show_page(1)
            return
        if not (payload / "WebAppLauncher.exe").is_file():
            messagebox.showerror(PRODUCT_NAME, "설치 파일 묶음이 손상되었습니다.")
            return

        create_start_menu = self.start_menu.get()
        install_runtimes = self.install_runtimes.get()
        self.running = True
        self.cancel_event.clear()
        self.log_messages.clear()
        self._reset_progress()
        self.status.set("프로그램 파일을 복사하는 중...")
        self._show_page(3)
        self._write_log(f"설치 위치: {destination}")

        threading.Thread(
            target=self._install_worker,
            args=(payload, destination, create_start_menu, install_runtimes),
            daemon=True,
        ).start()

    def _show_cleanup_dialog(self) -> str | None:
        """Show a modal dropdown asking how to clean up .webapp data. Returns 'all', 'runtime', or 'keep'; None if cancelled."""
        result: list[str | None] = [None]

        window = tk.Toplevel(self.root)
        window.title(PRODUCT_NAME)
        window.resizable(False, False)
        window.transient(self.root)
        window.grab_set()

        frame = tk.Frame(window, background="#ffffff", padx=24, pady=24)
        frame.pack(fill=tk.BOTH, expand=True)

        tk.Label(
            frame,
            text="설치된 웹앱과 런타임을 지우겠습니까?",
            anchor=tk.W,
            background="#ffffff",
            foreground="#171719",
            font=("Segoe UI Semibold", 12),
        ).pack(fill=tk.X)
        tk.Label(
            frame,
            text="프로그램 파일은 항상 삭제됩니다. 사용자 폴더의 .webapp 데이터 처리 방식을 선택하십시오.",
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=360,
            background="#ffffff",
            foreground="#4b4b4f",
            font=("Segoe UI", 10),
        ).pack(fill=tk.X, pady=(8, 16))

        options = [
            ("모두 삭제하기", "all", "설치된 웹앱과 런타임, 도구를 모두 삭제합니다."),
            ("런타임만 삭제하기", "runtime", "실행 환경과 도구만 삭제하고 웹앱은 유지합니다."),
            ("삭제하지 않기", "keep", ".webapp 폴더의 앱과 데이터를 그대로 둡니다."),
        ]
        selected = tk.StringVar(value="keep")
        combobox = ttk.Combobox(
            frame,
            values=[label for label, _, _ in options],
            state="readonly",
            width=38,
        )
        combobox.current(2)
        combobox.pack(fill=tk.X)

        description = tk.Label(
            frame,
            text=options[2][2],
            anchor=tk.W,
            justify=tk.LEFT,
            wraplength=360,
            background="#ffffff",
            foreground="#6a6a6f",
            font=("Segoe UI", 9),
        )
        description.pack(fill=tk.X, pady=(10, 0))

        def on_select(_event: object | None = None) -> None:
            label = combobox.get()
            for opt_label, opt_value, opt_desc in options:
                if opt_label == label:
                    selected.set(opt_value)
                    description.config(text=opt_desc)
                    break

        combobox.bind("<<ComboboxSelected>>", on_select)

        button_frame = tk.Frame(frame, background="#ffffff")
        button_frame.pack(fill=tk.X, pady=(20, 0))

        def on_ok() -> None:
            result[0] = selected.get()
            window.destroy()

        def on_cancel() -> None:
            result[0] = None
            window.destroy()

        ttk.Button(button_frame, text="아니오(취소)", command=on_cancel).pack(side=tk.RIGHT, padx=(8, 0))
        ttk.Button(button_frame, text="예", command=on_ok, default="active").pack(side=tk.RIGHT)

        window.protocol("WM_DELETE_WINDOW", on_cancel)
        window.update_idletasks()
        x = self.root.winfo_x() + max(0, (self.root.winfo_width() - window.winfo_reqwidth()) // 2)
        y = self.root.winfo_y() + max(0, (self.root.winfo_height() - window.winfo_reqheight()) // 2)
        window.geometry(f"{window.winfo_reqwidth()}x{window.winfo_reqheight()}+{x}+{y}")
        self.root.wait_window(window)
        return result[0]

    def _start_remove(self) -> None:
        if self.running or self.existing_install_dir is None:
            return

        if find_running_launcher_processes():
            if not messagebox.askyesno(
                PRODUCT_NAME,
                "앱이 실행되어 있습니다.\n앱을 종료하고 진행할까요?",
                icon="warning",
            ):
                return
            if not kill_running_launcher_processes():
                messagebox.showerror(
                    PRODUCT_NAME,
                    "실행 중인 앱을 종료할 수 없습니다.\n"
                    "직접 종료한 뒤 다시 시도하십시오.",
                )
                return

        cleanup = self._show_cleanup_dialog()
        if cleanup is None:
            return
        self.cleanup_option.set(cleanup)

        self.operation = "remove"
        self.running = True
        self.cancel_event.clear()
        self.log_messages.clear()
        self._reset_progress()
        self.status.set("프로그램 파일을 삭제하는 중...")
        self._show_page(3)
        threading.Thread(
            target=self._remove_worker,
            args=(self.existing_install_dir,),
            daemon=True,
        ).start()


    def _install_worker(
        self,
        payload: Path,
        destination: Path,
        create_start_menu: bool,
        install_runtimes: bool,
    ) -> None:
        try:
            self._check_cancelled()
            destination.mkdir(parents=True, exist_ok=True)
            self._copy_payload(payload, destination)

            launcher = destination / "WebAppLauncher.exe"
            if create_start_menu:
                self.messages.put(("status", "시작 메뉴 바로가기를 만드는 중..."))
                self.messages.put(("progress", {"overall": 8.0, "phase": "shortcut", "item": "시작 메뉴"}))
                create_shortcut(start_menu_shortcut(), launcher, destination)
                self.messages.put(("log", "시작 메뉴 바로가기를 만들었습니다."))

            if install_runtimes:
                self.messages.put(("progress", {"overall": 10.0, "phase": "prepare", "item": "실행 환경"}))
                self._run_bootstrapper(destination)

            self._check_cancelled()
            write_install_state(destination)
            self.messages.put(("progress", {"overall": 100.0, "phase": "complete", "item": PRODUCT_NAME}))
            self.messages.put(("complete", launcher))
        except InstallCancelled:
            self.messages.put(("cancelled", None))
        except Exception as exc:
            self.messages.put(("error", str(exc)))

    def _remove_worker(self, destination: Path) -> None:
        try:
            def report(current: int, total: int, item: str) -> None:
                self.messages.put(
                    (
                        "progress",
                        {
                            "overall": current * 100 / max(1, total),
                            "phase": "remove",
                            "item": item,
                            "current": current,
                            "total": total,
                        },
                    )
                )

            remove_installation(destination, report)

            cleanup = self.cleanup_option.get()
            if cleanup in {"all", "runtime"}:
                self.messages.put(("status", "런타임과 도구를 정리하는 중..."))
                remove_runtime_data(lambda name: self.messages.put(("log", f"정리: .webapp/{name}")))
            if cleanup == "all":
                self.messages.put(("status", "설치된 웹앱을 정리하는 중..."))
                remove_webapp_apps(lambda name: self.messages.put(("log", f"정리: .webapp/{name}")))

            self.messages.put(("removed", None))
        except Exception as exc:
            self.messages.put(("error", str(exc)))

    def _copy_payload(self, payload: Path, destination: Path) -> None:
        self.messages.put(("log", "프로그램 파일을 복사합니다."))
        sources = list(payload.rglob("*"))
        files = [source for source in sources if source.is_file()]
        total_files = len(files)
        copied_files = 0
        for source in sources:
            self._check_cancelled()
            relative = source.relative_to(payload)
            target = destination / relative
            if source.is_dir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source, target)
                copied_files += 1
                percent = copied_files * 100 / max(1, total_files)
                self.messages.put(
                    (
                        "progress",
                        {
                            "overall": percent * 0.08,
                            "phase": "copy",
                            "item": source.name,
                            "current": copied_files,
                            "total": total_files,
                        },
                    )
                )

    def _run_bootstrapper(self, destination: Path) -> None:
        bootstrapper = destination / "WebAppLauncher.Bootstrapper.exe"
        catalog = destination / "runtime-catalog.toml"
        if not bootstrapper.is_file() or not catalog.is_file():
            raise FileNotFoundError("Bootstrapper 또는 runtime-catalog.toml이 없습니다.")

        self.messages.put(("status", "WebView2와 전용 런타임을 준비하는 중..."))
        command = [str(bootstrapper), "install", "--catalog", str(catalog)]
        self.bootstrapper = subprocess.Popen(
            command,
            cwd=destination,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        assert self.bootstrapper.stdout is not None
        for line in self.bootstrapper.stdout:
            self._check_cancelled()
            message = line.rstrip()
            if message.startswith("@@WEBAPP_PROGRESS "):
                try:
                    event = json.loads(message.removeprefix("@@WEBAPP_PROGRESS "))
                    self.messages.put(("bootstrap_progress", event))
                except json.JSONDecodeError:
                    self.messages.put(("log", message))
            else:
                self.messages.put(("log", message))
            if message.startswith("Download "):
                self.messages.put(("status", f"다운로드 중: {message.removeprefix('Download ')}"))
            elif message.startswith("Skip existing "):
                self.messages.put(("status", f"이미 설치됨: {message.removeprefix('Skip existing ')}"))

        exit_code = self.bootstrapper.wait()
        self.bootstrapper = None
        self._check_cancelled()
        if exit_code != 0:
            raise RuntimeError(f"런타임 설치가 실패했습니다. 종료 코드: {exit_code}")

    def _check_cancelled(self) -> None:
        if self.cancel_event.is_set():
            raise InstallCancelled()

    def _cancel_install(self) -> None:
        if not self.running:
            return
        if not messagebox.askyesno(PRODUCT_NAME, "설치를 취소하시겠습니까?"):
            return

        self.cancel_event.set()
        self.status.set("설치를 취소하는 중...")
        self.cancel_button.configure(state=tk.DISABLED)
        if self.bootstrapper is not None:
            self.bootstrapper.terminate()

    def _drain_messages(self) -> None:
        try:
            while True:
                kind, value = self.messages.get_nowait()
                if kind == "log":
                    self._write_log(str(value))
                elif kind == "status":
                    self.status.set(str(value))
                elif kind == "progress":
                    self._apply_local_progress(dict(value))
                elif kind == "bootstrap_progress":
                    self._apply_bootstrap_progress(dict(value))
                elif kind == "complete":
                    self._finish_success(Path(value))
                elif kind == "removed":
                    self._finish_removed()
                elif kind == "cancelled":
                    self._finish_cancelled()
                elif kind == "error":
                    self._finish_error(str(value))
        except queue.Empty:
            pass
        finally:
            if self.root.winfo_exists():
                self.root.after(100, self._drain_messages)

    def _finish_success(self, launcher: Path) -> None:
        self.running = False
        self.existing_install_dir = launcher.parent
        self.installed_launcher = launcher
        self._set_overall_progress(100)
        self._write_log("설치가 완료되었습니다.")
        self._show_page(4)

    def _finish_removed(self) -> None:
        self.running = False
        self.existing_install_dir = None
        self.installed_launcher = None
        self._set_overall_progress(100)
        self._show_page(4)

    def _finish_cancelled(self) -> None:
        self.running = False
        if self.close_after_cancel:
            self.root.destroy()
            return
        messagebox.showinfo(PRODUCT_NAME, "설치가 취소되었습니다.")
        if self.operation == "repair":
            self._show_maintenance_page()
        else:
            self._show_page(2)

    def _finish_error(self, error: str) -> None:
        self.running = False
        self._write_log(f"오류: {error}")
        messagebox.showerror(
            PRODUCT_NAME,
            f"설치에 실패했습니다.\n\n{error}\n\n설치 기록에서 자세한 내용을 확인할 수 있습니다.",
        )
        if self.operation in {"repair", "remove"}:
            self._show_maintenance_page()
        else:
            self._show_page(2)
        if self.operation != "remove":
            self._show_log()

    def _reset_progress(self) -> None:
        self._set_overall_progress(0)
        self.detail_item.set("-")
        self.detail_phase.set("대기 중")
        self.download_percent.set(0)
        self.download_text.set("-")
        self.extract_percent.set(0)
        self.extract_text.set("-")

    def _set_overall_progress(self, percent: float) -> None:
        value = max(0.0, min(100.0, percent))
        self.overall_percent.set(value)
        self.overall_percent_text.set(f"{value:.0f}%")

    def _apply_local_progress(self, event: dict[str, object]) -> None:
        self._set_overall_progress(float(event.get("overall", 0)))
        phase = str(event.get("phase", ""))
        item = str(event.get("item", "-"))
        self.detail_item.set(item)
        if phase == "copy":
            current = int(event.get("current", 0))
            total = int(event.get("total", 0))
            self.detail_phase.set(f"프로그램 파일 복사 ({current}/{total})")
        elif phase == "shortcut":
            self.detail_phase.set("시작 메뉴 바로가기 생성")
        elif phase == "prepare":
            self.detail_phase.set("실행 환경 준비")
        elif phase == "complete":
            self.detail_phase.set("설치 완료")
        elif phase == "remove":
            current = int(event.get("current", 0))
            total = int(event.get("total", 0))
            self.detail_phase.set(f"프로그램 파일 삭제 ({current}/{total})")

    def _apply_bootstrap_progress(self, event: dict[str, object]) -> None:
        bootstrap_percent = float(event.get("overallPercent", 0))
        self._set_overall_progress(10 + (bootstrap_percent * 0.89))

        phase = str(event.get("phase", ""))
        item = str(event.get("item", "-"))
        message = str(event.get("message", ""))
        current = int(event.get("current", 0))
        total = int(event.get("total", 0))
        if item != self.detail_item.get():
            self.download_percent.set(0)
            self.download_text.set("-")
            self.extract_percent.set(0)
            self.extract_text.set("-")
        self.detail_item.set(item)
        self.detail_phase.set(message)

        if phase == "download":
            percent = current * 100 / total if total > 0 else 0
            self.download_percent.set(percent)
            if total > 0:
                self.download_text.set(
                    f"{percent:.0f}%  ({self._format_bytes(current)} / {self._format_bytes(total)})"
                )
            else:
                self.download_text.set(f"{self._format_bytes(current)} 다운로드됨")
            self.status.set(f"다운로드 중: {item} {percent:.0f}%")
        elif phase == "extract":
            percent = current * 100 / total if total > 0 else 0
            self.extract_percent.set(percent)
            self.extract_text.set(f"{percent:.0f}%  ({current} / {total} 항목)")
            self.status.set(f"압축 해제 중: {item} {percent:.0f}%")
        elif phase in {"install", "copy"}:
            self.status.set(f"{message}: {item}")
        elif phase == "skip":
            self.status.set(f"이미 설치됨: {item}")

    @staticmethod
    def _format_bytes(value: int) -> str:
        size = float(max(0, value))
        for unit in ("B", "KB", "MB", "GB"):
            if size < 1024 or unit == "GB":
                return f"{size:.1f} {unit}" if unit != "B" else f"{int(size)} B"
            size /= 1024
        return f"{size:.1f} GB"

    def _finish(self) -> None:
        if (
            self.operation != "remove"
            and self.launch_after_install.get()
            and self.installed_launcher is not None
        ):
            subprocess.Popen([str(self.installed_launcher)], cwd=self.installed_launcher.parent)
        self.root.destroy()

    def _write_log(self, message: str) -> None:
        self.log_messages.append(message)
        if self.log_text is not None and self.log_text.winfo_exists():
            self.log_text.configure(state=tk.NORMAL)
            self.log_text.insert(tk.END, message + "\n")
            self.log_text.see(tk.END)
            self.log_text.configure(state=tk.DISABLED)

    def _show_log(self) -> None:
        if self.log_window is not None and self.log_window.winfo_exists():
            self.log_window.deiconify()
            self.log_window.lift()
            self.log_window.focus_force()
            return

        window = tk.Toplevel(self.root)
        window.title(f"{PRODUCT_NAME} 설치 세부 정보")
        window.resizable(False, False)
        window.transient(self.root)

        frame = ttk.Frame(window, padding=12)
        frame.pack(fill=tk.BOTH, expand=True)
        summary = ttk.LabelFrame(frame, text="현재 작업", padding=10)
        summary.pack(fill=tk.X, pady=(0, 10))
        summary.columnconfigure(1, weight=1)

        ttk.Label(summary, text="항목").grid(row=0, column=0, sticky=tk.W, padx=(0, 12))
        ttk.Label(summary, textvariable=self.detail_item).grid(row=0, column=1, sticky=tk.W)
        ttk.Label(summary, text="상태").grid(row=1, column=0, sticky=tk.W, padx=(0, 12), pady=(4, 0))
        ttk.Label(summary, textvariable=self.detail_phase).grid(row=1, column=1, sticky=tk.W, pady=(4, 0))

        ttk.Label(summary, text="다운로드").grid(row=2, column=0, sticky=tk.W, padx=(0, 12), pady=(10, 0))
        ttk.Progressbar(
            summary,
            maximum=100,
            variable=self.download_percent,
            mode="determinate",
        ).grid(row=2, column=1, sticky=tk.EW, pady=(10, 0))
        ttk.Label(summary, textvariable=self.download_text, width=26, anchor=tk.E).grid(
            row=2,
            column=2,
            sticky=tk.E,
            padx=(10, 0),
            pady=(10, 0),
        )

        ttk.Label(summary, text="압축 해제").grid(row=3, column=0, sticky=tk.W, padx=(0, 12), pady=(7, 0))
        ttk.Progressbar(
            summary,
            maximum=100,
            variable=self.extract_percent,
            mode="determinate",
        ).grid(row=3, column=1, sticky=tk.EW, pady=(7, 0))
        ttk.Label(summary, textvariable=self.extract_text, width=26, anchor=tk.E).grid(
            row=3,
            column=2,
            sticky=tk.E,
            padx=(10, 0),
            pady=(7, 0),
        )

        ttk.Label(frame, text="설치 기록").pack(anchor=tk.W)
        log_frame = ttk.Frame(frame)
        log_frame.pack(fill=tk.BOTH, expand=True, pady=(5, 0))
        log_text = tk.Text(
            log_frame,
            width=76,
            height=12,
            wrap=tk.WORD,
            state=tk.NORMAL,
            font=("Consolas", 9),
        )
        scrollbar = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, command=log_text.yview)
        log_text.configure(yscrollcommand=scrollbar.set)
        log_text.grid(row=0, column=0, sticky=tk.NSEW)
        scrollbar.grid(row=0, column=1, sticky=tk.NS)
        log_text.insert(tk.END, "\n".join(self.log_messages))
        if self.log_messages:
            log_text.insert(tk.END, "\n")
        log_text.see(tk.END)
        log_text.configure(state=tk.DISABLED)

        close_button = ttk.Button(frame, text="닫기")
        close_button.pack(anchor=tk.E, pady=(10, 0))

        def close_log() -> None:
            self.log_window = None
            self.log_text = None
            window.destroy()

        close_button.configure(command=close_log)
        window.protocol("WM_DELETE_WINDOW", close_log)
        window.update_idletasks()
        width = window.winfo_reqwidth()
        height = window.winfo_reqheight()
        x = self.root.winfo_x() + max(0, (self.root.winfo_width() - width) // 2)
        y = self.root.winfo_y() + max(0, (self.root.winfo_height() - height) // 2)
        window.geometry(f"{width}x{height}+{x}+{y}")
        self.log_window = window
        self.log_text = log_text

    def _close(self) -> None:
        if self.running:
            if self.operation == "remove":
                messagebox.showinfo(PRODUCT_NAME, "삭제가 끝난 뒤 설치 마법사를 종료할 수 있습니다.")
                return
            if not messagebox.askyesno(PRODUCT_NAME, "설치가 진행 중입니다. 취소하고 종료하시겠습니까?"):
                return
            self.close_after_cancel = True
            self.cancel_event.set()
            if self.bootstrapper is not None:
                self.bootstrapper.terminate()
            return
        self.root.destroy()


def main() -> None:
    root = tk.Tk()
    try:
        root.iconbitmap(default=str(bundled_path("assets/installer.ico")))
    except tk.TclError:
        pass
    try:
        root.window_icon = tk.PhotoImage(file=str(bundled_path("assets/logo.png")))
        root.iconphoto(True, root.window_icon)
    except tk.TclError:
        pass
    InstallerApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
