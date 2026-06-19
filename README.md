# WAPK Launcher MVP

WAPK Launcher installs and runs local web apps from `.wapk` TOML manifests.

The MVP is split by responsibility:

- Python: manifest parsing, downloads, app storage, port selection, process lifecycle, installed-app GUI.
- Rust: WebView window creation, single HTML loading, `window.__WAPK__` runtime injection.

## Run

```powershell
cd A:\dev\webapp\webapp-launcher
uv run --project . python -m wapk_launcher
```

Open a `.wapk` directly:

```powershell
uv run --project . python -m wapk_launcher A:\dev\webapp\webapp_test\TestIsolatedApp.wapk
```

The Rust WebView binary is built on first launch if it is missing.

## Build

```powershell
cd A:\dev\webapp\webapp-launcher
.\build.ps1 -Clean
```

The build script compiles `rust/wapk-webview` in release mode, then bundles that helper exe and the Python launcher code with PyInstaller. The default output is:

```txt
dist/wapk-launcher.exe
```

Use `.\build.ps1 -OneDir` to create a one-directory PyInstaller build instead.

## WAPK Example

```toml
repository = "https://github.com/example/mini-timetable"
ref = "main"
```

The launcher downloads the repository zip for `ref`, finds `metadata.toml`, and copies the app files by relative path.

Repository `metadata.toml` example:

```toml
id = "mini-timetable"
name = "Mini Timetable"
version = "0.1.0"
app_exe = "dist/app.exe"
app_html = "app.html"
args = ["--port", "{PORT}"]
port_range = [52000, 52500]
ready_url = "http://127.0.0.1:{PORT}/health"
api_base = "http://127.0.0.1:{PORT}"

[window]
borderless = true
fullscreen = false
transparent = true
level = "bottom" # normal, top, bottom
```

`transparent = true`는 WebView 배경을 완전 투명하게 열기 위한 옵션입니다. blur, mica, acrylic 같은 시각 효과는 런처가 입히지 않고 HTML/CSS에서 직접 처리합니다.
