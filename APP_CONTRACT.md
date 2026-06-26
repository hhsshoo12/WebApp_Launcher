# WebApp Launcher App Contract

## `.wapk` format 2

```toml
[wapk]
format = 2

[package]
id = "owner@repository"
name = "Example App"

[source]
provider = "github"
owner = "owner"
repo = "repository"
branch = "main"
commit = "*"
app_dir = "."

[runtime]
python = "python313"
node = "none"

[entry]
html = "app.html"
python = "app.py"
icon = "icon.png"

[window]
width = 1200
height = 800
resizable = true
devtools = false
transparent = false
borderless = false
fullscreen = false
always_on_top = false
start_maximized = false
instance_mode = "new_backend"
```

- `branch = "*"` selects the GitHub repository default branch.
- `commit = "*"` tracks the latest commit on the selected branch.
- A concrete commit pins the app and disables automatic updates.
- The installed version is the first eight characters of the resolved commit.
- `instance_mode` accepts `focus_existing`, `share_backend`, or `new_backend`.

## Window API

The launcher injects the API into the top-level app document:

```javascript
await window.webapp.window.minimize();
await window.webapp.window.maximize();
await window.webapp.window.restore();
await window.webapp.window.toggleMaximize();
await window.webapp.window.setFullscreen(true);
await window.webapp.window.toggleFullscreen();
await window.webapp.window.setAlwaysOnTop(true);
await window.webapp.window.startDrag();
await window.webapp.window.close();

const state = await window.webapp.window.getState();
window.addEventListener("webappwindowstatechange", event => {
  console.log(event.detail);
});
```

`startDrag()` should be called from the pointer-down handler of the app's
custom title bar. The API is available only when the document is hosted by
WebApp Launcher.

## Persistence

Persistent application state belongs under `WEBAPP_DATA_DIR`. Browser profile
data is ephemeral and is deleted when the WebView session ends.
