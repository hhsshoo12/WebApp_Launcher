# Installer

WebApp Launcher is distributed as an online-only `Setup.exe` plus GitHub
Release assets. The release flow produces one launcher ZIP, its checksum, and
one setup executable.

`Setup.exe`:

- downloads the latest `WAPL-Launcher-v*.zip` from GitHub Releases;
- downloads the matching `.sha256`, verifies the launcher ZIP, and safely
  extracts it into the selected per-user install directory;
- optionally downloads and installs the latest `WAPL-Runtime-v*.zip` bundle
  from GitHub Releases;
- stores a copy of itself at `%USERPROFILE%\.wapk\bootstrapper\WebAppLauncher-Setup.exe`
  for in-app launcher updates;
- writes `.webapp-launcher-install.json` with the current `version`,
  `install_location`, and `setup_path`.

Build the release assets:

```powershell
powershell -ExecutionPolicy Bypass -File .\Installer\build-installer.ps1
```

The script creates and maintains `Installer\.venv`, installs the pinned
PyInstaller build dependency with pip, and writes:

```text
artifacts/
├─ WAPL-Launcher-v0.1.3.zip
├─ WAPL-Launcher-v0.1.3.zip.sha256
└─ WebAppLauncher-Setup-v0.1.3.exe
```

Upload the launcher ZIP and checksum to the `v0.1.3` GitHub release. Upload
the setup executable as the public installer.

Build the runtime bundle from an existing `.webapp` runtime installation:

```powershell
powershell -ExecutionPolicy Bypass -File .\Installer\build-runtime-bundle.ps1 `
  -BundleVersion 0.1
```

Upload both generated runtime assets to a `runtime-v0.1` GitHub release:

```text
WAPL-Runtime-v0.1.zip
WAPL-Runtime-v0.1.zip.sha256
```

Populate `Installer\runtime-licenses\` with the license files shipped by every
runtime and tool before publishing the runtime bundle.
