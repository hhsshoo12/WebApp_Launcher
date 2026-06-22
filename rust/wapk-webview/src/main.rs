#![cfg_attr(windows, windows_subsystem = "windows")]

use std::{env, error::Error, fs, path::PathBuf};

use winit::{
    application::ApplicationHandler,
    event::WindowEvent,
    event_loop::{ActiveEventLoop, EventLoop},
    window::{Fullscreen, Window, WindowAttributes, WindowId, WindowLevel},
};
use wry::{WebView, WebViewBuilder};

struct Args {
    html: Option<PathBuf>,
    url: Option<String>,
    runtime_json: String,
    title: String,
    borderless: bool,
    fullscreen: bool,
    transparent: bool,
    window_level: WindowLevel,
    devtools: bool,
}

struct App {
    title: String,
    html: String,
    url: Option<String>,
    runtime_script: String,
    options: Args,
    window: Option<Window>,
    webview: Option<WebView>,
}

impl ApplicationHandler for App {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.window.is_some() {
            return;
        }

        let attrs = WindowAttributes::default()
            .with_title(self.title.clone())
            .with_inner_size(winit::dpi::LogicalSize::new(1100.0, 760.0))
            .with_decorations(!self.options.borderless)
            .with_transparent(self.options.transparent)
            .with_window_level(self.options.window_level)
            .with_fullscreen(if self.options.fullscreen {
                Some(Fullscreen::Borderless(None))
            } else {
                None
            });

        let window = event_loop
            .create_window(attrs)
            .expect("failed to create window");

        let builder = WebViewBuilder::new()
            .with_transparent(self.options.transparent)
            .with_initialization_script(self.runtime_script.clone())
            .with_devtools(self.options.devtools);
        let webview = if let Some(url) = &self.url {
            builder.with_url(url).build(&window)
        } else {
            builder.with_html(&self.html).build(&window)
        }
        .expect("failed to build webview");

        if self.options.devtools {
            webview.open_devtools();
        }

        self.webview = Some(webview);
        self.window = Some(window);
    }

    fn window_event(&mut self, event_loop: &ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        if matches!(event, WindowEvent::CloseRequested) {
            event_loop.exit();
        }
    }
}

fn main() -> Result<(), Box<dyn Error>> {
    enable_high_dpi();

    let args = parse_args()?;
    let runtime_value: serde_json::Value = serde_json::from_str(&args.runtime_json)?;
    let runtime_json = serde_json::to_string(&runtime_value)?;

    let html_source = if let Some(html) = &args.html {
        fs::read_to_string(html)?
    } else {
        String::new()
    };
    let runtime_script = runtime_initialization_script(&runtime_json);

    let event_loop = EventLoop::new()?;
    let mut app = App {
        title: args.title.clone(),
        html: html_source,
        url: args.url.clone(),
        runtime_script,
        options: args,
        window: None,
        webview: None,
    };
    event_loop.run_app(&mut app)?;
    Ok(())
}

fn enable_high_dpi() {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::UI::HiDpi::{
            SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
        };

        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}

fn parse_args() -> Result<Args, Box<dyn Error>> {
    let mut html: Option<PathBuf> = None;
    let mut url: Option<String> = None;
    let mut runtime_json: Option<String> = None;
    let mut title = String::from("WAPK App");
    let mut borderless = false;
    let mut fullscreen = false;
    let mut transparent = false;
    let mut window_level = WindowLevel::Normal;
    let mut devtools = false;

    let mut iter = env::args().skip(1);
    while let Some(arg) = iter.next() {
        match arg.as_str() {
            "--html" => html = iter.next().map(PathBuf::from),
            "--url" => url = iter.next(),
            "--runtime-json" => runtime_json = iter.next(),
            "--title" => {
                title = iter
                    .next()
                    .ok_or("--title requires a value")?;
            }
            "--borderless" => borderless = true,
            "--fullscreen" => fullscreen = true,
            "--transparent" => transparent = true,
            "--devtools" => devtools = true,
            "--window-level" => {
                let value = iter.next().ok_or("--window-level requires a value")?;
                window_level = parse_window_level(&value)?;
            }
            _ => return Err(format!("unknown argument: {arg}").into()),
        }
    }

    if html.is_some() == url.is_some() {
        return Err("exactly one of --html or --url is required".into());
    }

    Ok(Args {
        html,
        url,
        runtime_json: runtime_json.ok_or("--runtime-json is required")?,
        title,
        borderless,
        fullscreen,
        transparent,
        window_level,
        devtools,
    })
}

fn parse_window_level(value: &str) -> Result<WindowLevel, Box<dyn Error>> {
    match value {
        "normal" => Ok(WindowLevel::Normal),
        "top" => Ok(WindowLevel::AlwaysOnTop),
        "bottom" => Ok(WindowLevel::AlwaysOnBottom),
        _ => Err(format!("unknown window level: {value}").into()),
    }
}

fn runtime_initialization_script(runtime_json: &str) -> String {
    format!(
        r#"<script>window.__WAPK__=Object.freeze({runtime_json});</script>"#
    )
    .trim_start_matches("<script>")
    .trim_end_matches("</script>")
    .to_string()
}
