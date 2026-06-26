# Installer

The default distributions are a portable ZIP and a GUI installer built
with Python, tkinter, and PyInstaller. WiX and MSI are not used.

The installer:

- copies the self-contained launcher, CLI, and Bootstrapper into the selected
  per-user directory;
- optionally creates a Start Menu shortcut;
- optionally runs the Bootstrapper to install WebView2, Python, Node.js, Git,
  uv, and pnpm under `%USERPROFILE%\.webapp`;
- does not require Python on the destination computer.

Build both distributions:

```powershell
powershell -ExecutionPolicy Bypass -File .\Installer\build-installer.ps1
```

The script creates and maintains `Installer\.venv`, installs the pinned
PyInstaller build dependency with pip, and writes these files:

```text
artifacts/
├─ WebAppLauncher-0.1.0-portable-win-x64.zip
└─ WebAppLauncher-Setup-0.1.0-win-x64.exe
```

Runtime/tool download URLs are pinned in `runtime-catalog.toml`. Update them
deliberately and verify the resulting installer before release.

Build the GitHub Releases runtime bundle from an existing `.webapp` runtime
installation:

```powershell
powershell -ExecutionPolicy Bypass -File .\Installer\build-runtime-bundle.ps1 `
  -BundleVersion 0.1
```

Upload both generated assets to the same public release:

```text
WAPL-Runtime-v0.1.zip
WAPL-Runtime-v0.1.zip.sha256
```

Populate `Installer\runtime-licenses\` with the license files shipped by every
runtime and tool before publishing the bundle.
