from __future__ import annotations

import queue
import os
import subprocess
import shutil
import tempfile
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

from .config import (
    InstallCancelled,
    NETWORK_ERROR_MESSAGE,
    PRODUCT_NAME,
    SIDEBAR_WIDTH,
    WINDOW_HEIGHT,
    WINDOW_WIDTH,
    bundled_path,
)
from .release import download_launcher_payload, download_runtime_bundle
from .runtime import install_runtime_bundle, install_webview2, is_runtime_installed, remove_runtime_data, remove_webapp_apps
from .state import register_file_associations, start_menu_shortcut, unregister_file_associations, write_install_state
from .system import (
    copy_setup_to_bootstrapper_storage,
    create_shortcut,
    default_install_dir,
    find_existing_installation,
    move_staging_into_install_dir,
    remove_installation,
    webapp_root,
)


class InstallerApp:
    def __init__(self, root: tk.Tk) -> None:
        self.root = root
        self.root.title(f"{PRODUCT_NAME} 설치")
        self.root.resizable(False, False)

        self.messages: queue.Queue[tuple[str, object]] = queue.Queue()
        self.cancel_event = threading.Event()
        self.running = False
        self.bootstrapper = None
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
        self.file_associations = tk.BooleanVar(value=True)
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
            text=".wapk / .webapp 파일 연결 등록 (관리자 권한 불필요)",
            variable=self.file_associations,
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

        if not destination.is_absolute():
            messagebox.showerror(PRODUCT_NAME, "설치 위치는 절대 경로여야 합니다.")
            self._show_page(1)
            return

        create_start_menu = self.start_menu.get()
        install_runtimes = self.install_runtimes.get()
        register_associations = self.file_associations.get()
        self.running = True
        self.cancel_event.clear()
        self.log_messages.clear()
        self._reset_progress()
        self.status.set("런처 페이로드를 다운로드하는 중...")
        self._show_page(3)
        self._write_log(f"설치 위치: {destination}")
        self._write_log(f"Setup 버전: v{SETUP_VERSION}")

        threading.Thread(
            target=self._install_worker,
            args=(destination, create_start_menu, install_runtimes, register_associations),
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
        destination: Path,
        create_start_menu: bool,
        install_runtimes: bool,
        register_associations: bool,
    ) -> None:
        try:
            self._check_cancelled()
            destination.mkdir(parents=True, exist_ok=True)

            self.messages.put(("status", "GitHub Release에서 런처 페이로드를 받는 중..."))
            self.messages.put(
                (
                    "progress",
                    {"overall": 1.0, "phase": "download", "item": "런처 페이로드"},
                )
            )

            def on_progress(current: int, total: int, percent: int) -> None:
                self.messages.put(
                    (
                        "bootstrap_progress",
                        {
                            "type": "progress",
                            "phase": "download",
                            "item": "런처 페이로드",
                            "current": current,
                            "total": total,
                            "overallPercent": max(1.0, percent * 0.6),
                            "message": f"런처 다운로드 중 {percent}%",
                        },
                    )
                )

            with tempfile.TemporaryDirectory(prefix="wapl-setup-") as staging_dir_str:
                staging_dir = Path(staging_dir_str)
                version, staging = download_launcher_payload(staging_dir, on_progress)
                self._check_cancelled()
                self.messages.put(("log", f"런처 v{version} 다운로드와 검증이 끝났습니다."))
                self._copy_payload(staging, destination)
                self.messages.put(
                    ("log", f"런처 v{version} 파일을 {destination}에 배치했습니다.")
                )

            launcher = destination / "WebAppLauncher.exe"
            if create_start_menu:
                self.messages.put(("status", "시작 메뉴 바로가기를 만드는 중..."))
                self.messages.put(
                    (
                        "progress",
                        {"overall": 70.0, "phase": "shortcut", "item": "시작 메뉴"},
                    )
                )
                create_shortcut(start_menu_shortcut(), launcher, destination)
                self.messages.put(("log", "시작 메뉴 바로가기를 만들었습니다."))

            if install_runtimes:
                self.messages.put(
                    (
                        "progress",
                        {"overall": 75.0, "phase": "prepare", "item": "실행 환경"},
                    )
                )
                self._install_runtime_environment()

            self._check_cancelled()
            self.messages.put(
                (
                    "status",
                    "Setup.exe를 업데이트 디렉터리에 보관하는 중...",
                )
            )
            setup_path = copy_setup_to_bootstrapper_storage()
            self.messages.put(("log", f"업데이트용 Setup.exe: {setup_path}"))

            self._check_cancelled()
            write_install_state(destination, version=version, setup_path=setup_path)

            if register_associations:
                self.messages.put(("status", ".wapk / .webapp 파일 연결을 등록하는 중..."))
                if register_file_associations(destination):
                    self.messages.put(("log", ".wapk / .webapp 파일 연결을 등록했습니다."))
                else:
                    self.messages.put(
                        ("log", "파일 연결을 등록하지 못했습니다. (WebAppLauncher.exe 없음)")
                    )

            self.messages.put(
                (
                    "progress",
                    {"overall": 100.0, "phase": "complete", "item": PRODUCT_NAME},
                )
            )
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

            self.messages.put(("status", "파일 연결을 해제하는 중..."))
            unregister_file_associations()
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

    def _install_runtime_environment(self) -> None:
        self.messages.put(("status", "WebView2 설치 관리자를 받는 중..."))

        def webview_progress(current: int, total: int, percent: int) -> None:
            self.messages.put(
                (
                    "bootstrap_progress",
                    {
                        "type": "progress",
                        "phase": "download",
                        "item": "WebView2",
                        "current": current,
                        "total": total,
                        "overallPercent": 75 + percent * 0.05,
                        "message": f"WebView2 다운로드 중 {percent}%",
                    },
                )
            )

        install_webview2(webview_progress)
        self._check_cancelled()
        self.messages.put(("log", "WebView2 설치 확인이 끝났습니다."))
        self.messages.put(("status", "GitHub Release에서 런타임 번들을 받는 중..."))

        def runtime_progress(current: int, total: int, percent: int) -> None:
            self.messages.put(
                (
                    "bootstrap_progress",
                    {
                        "type": "progress",
                        "phase": "download",
                        "item": "런타임 번들",
                        "current": current,
                        "total": total,
                        "overallPercent": 80 + percent * 0.15,
                        "message": f"런타임 다운로드 중 {percent}%",
                    },
                )
            )

        with tempfile.TemporaryDirectory(prefix="wapl-runtime-") as staging_dir_str:
            runtime_version, staging = download_runtime_bundle(Path(staging_dir_str), runtime_progress)
            self._check_cancelled()
            self.messages.put(("status", "런타임 번들을 배치하는 중..."))
            self.messages.put(
                (
                    "bootstrap_progress",
                    {
                        "type": "progress",
                        "phase": "extract",
                        "item": "런타임 번들",
                        "current": 1,
                        "total": 1,
                        "overallPercent": 97,
                        "message": "런타임 배치 중",
                    },
                )
            )
            install_runtime_bundle(staging, webapp_root())
            self.messages.put(("log", f"런타임 번들 v{runtime_version} 설치가 끝났습니다."))

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


class UpdateDialog:
    """Small progress dialog used when Setup.exe is launched in --update mode."""

    def __init__(self, install_dir: Path) -> None:
        self.install_dir = install_dir
        self.root = tk.Tk()
        self.root.title(f"{PRODUCT_NAME} 업데이트")
        self.root.resizable(False, False)
        self.root.protocol("WM_DELETE_WINDOW", self._on_close)
        self.cancelled = False

        self.status = tk.StringVar(value="업데이트를 준비하는 중...")
        self.detail = tk.StringVar(value="-")
        self.overall_percent = tk.DoubleVar(value=0)
        self.overall_text = tk.StringVar(value="0%")

        frame = tk.Frame(self.root, background="#ffffff", padx=24, pady=20)
        frame.pack(fill=tk.BOTH, expand=True)
        frame.columnconfigure(0, weight=1)

        tk.Label(
            frame,
            text="WebApp Launcher 업데이트",
            anchor=tk.W,
            background="#ffffff",
            foreground="#171719",
            font=("Segoe UI Semibold", 13),
        ).grid(row=0, column=0, sticky=tk.EW)

        tk.Label(
            frame,
            textvariable=self.status,
            anchor=tk.W,
            background="#ffffff",
            foreground="#4b4b4f",
            font=("Segoe UI", 10),
        ).grid(row=1, column=0, sticky=tk.EW, pady=(8, 0))

        progress_row = tk.Frame(frame, background="#ffffff")
        progress_row.grid(row=2, column=0, sticky=tk.EW, pady=(14, 0))
        progress_row.columnconfigure(0, weight=1)
        ttk.Progressbar(
            progress_row,
            mode="determinate",
            maximum=100,
            variable=self.overall_percent,
        ).grid(row=0, column=0, sticky=tk.EW)
        tk.Label(
            progress_row,
            textvariable=self.overall_text,
            width=5,
            anchor=tk.E,
            background="#ffffff",
            font=("Segoe UI Semibold", 10),
        ).grid(row=0, column=1, padx=(10, 0))

        tk.Label(
            frame,
            textvariable=self.detail,
            anchor=tk.W,
            background="#ffffff",
            foreground="#6a6a6f",
            font=("Segoe UI", 9),
        ).grid(row=3, column=0, sticky=tk.EW, pady=(10, 0))

        self.cancel_button = ttk.Button(frame, text="취소", command=self._on_close)
        self.cancel_button.grid(row=4, column=0, sticky=tk.E, pady=(16, 0))

        self.root.update_idletasks()
        width = max(self.root.winfo_reqwidth(), 420)
        height = max(self.root.winfo_reqheight(), 200)
        x = max(0, (self.root.winfo_screenwidth() - width) // 2)
        y = max(0, (self.root.winfo_screenheight() - height) // 2)
        self.root.geometry(f"{width}x{height}+{x}+{y}")

    def _on_close(self) -> None:
        if self.cancelled:
            return
        self.cancelled = True
        self.status.set("업데이트를 취소하는 중...")
        self.cancel_button.configure(state=tk.DISABLED)

    def report_overall(self, percent: float) -> None:
        clamped = max(0.0, min(100.0, percent))
        self.overall_percent.set(clamped)
        self.overall_text.set(f"{clamped:.0f}%")

    def report_status(self, message: str) -> None:
        self.status.set(message)

    def report_detail(self, message: str) -> None:
        self.detail.set(message)

    def pump(self) -> None:
        try:
            self.root.update()
        except tk.TclError:
            pass

    def close(self) -> None:
        try:
            self.root.destroy()
        except tk.TclError:
            pass
