use std::{env, error::Error, fs, path::PathBuf};

use winit::{
    application::ApplicationHandler,
    event::WindowEvent,
    event_loop::{ActiveEventLoop, EventLoop},
    window::{Fullscreen, Window, WindowAttributes, WindowId, WindowLevel},
};
use wry::{WebView, WebViewBuilder};

struct Args {
    html: PathBuf,
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

        let webview = WebViewBuilder::new()
            .with_transparent(self.options.transparent)
            .with_devtools(self.options.devtools)
            .with_html(&self.html)
            .build(&window)
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

    let source = fs::read_to_string(&args.html)?;
    let injected = inject_runtime(&source, &runtime_json);

    let event_loop = EventLoop::new()?;
    let mut app = App {
        title: args.title.clone(),
        html: injected,
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

    Ok(Args {
        html: html.ok_or("--html is required")?,
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

fn inject_runtime(html: &str, runtime_json: &str) -> String {
    let script = format!(
        r#"<script>window.__WAPK__=Object.freeze({runtime_json});</script>"#
    );

    if let Some(index) = find_case_insensitive(html, "<head>") {
        let insert_at = index + "<head>".len();
        let mut output = String::with_capacity(html.len() + script.len());
        output.push_str(&html[..insert_at]);
        output.push_str(&script);
        output.push_str(&html[insert_at..]);
        return output;
    }

    format!("{script}{html}")
}

fn find_case_insensitive(haystack: &str, needle: &str) -> Option<usize> {
    haystack.to_lowercase().find(&needle.to_lowercase())
}
