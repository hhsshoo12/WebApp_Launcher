"""WebApp Launcher version metadata.

The build script (build-installer.ps1) writes the resolved version string
into this file before PyInstaller bundles installer.py, so the installer
records the same version it was built as.
"""

from __future__ import annotations

__version__ = "0.0.0+local"
