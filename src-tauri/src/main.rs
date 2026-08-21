#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod codex;
mod config;
mod logging;
mod models;
mod tray_icon;

use config::AppConfig;
use models::AppPayload;
use std::time::Duration;
use tauri::{
    menu::{Menu, MenuItem, PredefinedMenuItem},
    tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent},
    App, AppHandle, Emitter, LogicalSize, Manager, Size, State,
};
use tokio::sync::{Mutex, RwLock};

struct AppCore {
    config: RwLock<AppConfig>,
    payload: RwLock<AppPayload>,
    codex: Mutex<codex::CodexManager>,
    refresh_gate: Mutex<()>,
}

#[tauri::command]
async fn get_app_state(state: State<'_, AppCore>) -> Result<AppPayload, String> {
    Ok(state.payload.read().await.clone())
}

#[tauri::command]
async fn get_config(state: State<'_, AppCore>) -> Result<AppConfig, String> {
    Ok(state.config.read().await.clone())
}

#[tauri::command]
async fn refresh_now(app: AppHandle) -> Result<AppPayload, String> {
    Ok(refresh_application(&app).await)
}

#[tauri::command]
async fn save_config(
    app: AppHandle,
    state: State<'_, AppCore>,
    config: AppConfig,
) -> Result<AppConfig, String> {
    let normalized = config.normalize();
    if normalized
        .codex_binary_path
        .as_deref()
        .is_some_and(|path| path.to_ascii_lowercase().contains("\\windowsapps\\"))
    {
        return Err("不能选择 WindowsApps 内置 Codex；请使用独立 Codex CLI".into());
    }
    config::set_autostart(normalized.autostart).map_err(|error| error.to_string())?;
    config::save(&normalized).map_err(|error| error.to_string())?;
    *state.config.write().await = normalized.clone();

    let payload = state.payload.read().await.clone();
    update_tray(&app, &payload, normalized.light_glyphs);
    logging::info("configuration saved");
    Ok(normalized)
}

#[tauri::command]
async fn choose_codex_binary() -> Result<Option<String>, String> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("选择独立 Codex CLI")
            .add_filter("Codex CLI", &["exe", "cmd", "bat"])
            .pick_file()
            .map(|path| path.to_string_lossy().into_owned())
    })
    .await
    .map_err(|error| error.to_string())
}

#[tauri::command]
fn open_url(url: String) -> Result<(), String> {
    let normalized = url.to_ascii_lowercase();
    if !(normalized.starts_with("https://github.com/mors-lee/metriklite")
        || normalized.starts_with("https://api.github.com/repos/mors-lee/metriklite"))
    {
        return Err("只允许打开 MetrikLite 官方 GitHub 地址".into());
    }
    open::that(url).map_err(|error| error.to_string())
}

#[tauri::command]
fn hide_panel(app: AppHandle) -> Result<(), String> {
    if let Some(window) = app.get_webview_window("main") {
        window.hide().map_err(|error| error.to_string())?;
    }
    Ok(())
}

#[tauri::command]
fn set_panel_expanded(app: AppHandle, expanded: bool) -> Result<(), String> {
    let Some(window) = app.get_webview_window("main") else {
        return Ok(());
    };
    let height = if expanded { 470.0 } else { 300.0 };
    window
        .set_size(Size::Logical(LogicalSize::new(380.0, height)))
        .map_err(|error| error.to_string())?;
    let (x, y) = tray_icon::cursor_position();
    tray_icon::position_panel(&window, x, y).map_err(|error| error.to_string())
}

#[tauri::command]
async fn quit_app(app: AppHandle) -> Result<(), String> {
    shutdown_and_exit(app).await;
    Ok(())
}

async fn refresh_application(app: &AppHandle) -> AppPayload {
    let state = app.state::<AppCore>();
    let _refresh_guard = state.refresh_gate.lock().await;
    let configuration = state.config.read().await.clone();
    let result = state
        .codex
        .lock()
        .await
        .read(configuration.codex_binary_path.as_deref())
        .await;
    let payload = match result {
        Ok(windows) => AppPayload::ready(windows),
        Err(error) => {
            logging::error(format!("refresh failed: {error:#}"));
            AppPayload::error(error.to_string())
        }
    };
    *state.payload.write().await = payload.clone();
    update_tray(app, &payload, configuration.light_glyphs);
    let _ = app.emit("quota-updated", &payload);
    logging::info(format!(
        "refreshed: windows={}, status={}",
        payload.windows.len(),
        payload.status
    ));
    payload
}

fn update_tray(app: &AppHandle, payload: &AppPayload, light_glyphs: bool) {
    let Some(tray) = app.tray_by_id("main") else {
        return;
    };
    let _ = tray.set_icon(Some(tray_icon::render(payload.tray_percent, light_glyphs)));
    let tooltip = tray_tooltip(payload);
    let _ = tray.set_tooltip(Some(tooltip));
}

fn tray_tooltip(payload: &AppPayload) -> String {
    let Some(percent) = payload.tray_percent else {
        return "MetrikLite：点击查看并设置 Codex CLI".into();
    };
    let reset = payload
        .windows
        .iter()
        .find(|window| window.window_key == "secondary")
        .and_then(|window| window.resets_at_ms)
        .and_then(chrono::DateTime::<chrono::Utc>::from_timestamp_millis)
        .map(|timestamp| {
            timestamp
                .with_timezone(&chrono::Local)
                .format("%m-%d %H:%M")
                .to_string()
        });
    match reset {
        Some(reset) => format!("Codex 剩余 {percent}%\n重置 {reset}"),
        None => format!("Codex 剩余 {percent}%\n重置时间暂未提供"),
    }
}

fn show_panel(app: &AppHandle, position: Option<(f64, f64)>) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    let (x, y) = position.unwrap_or_else(tray_icon::cursor_position);
    let _ = tray_icon::position_panel(&window, x, y);
    let _ = window.show();
    let _ = window.set_focus();
}

fn toggle_panel(app: &AppHandle, position: (f64, f64)) {
    let Some(window) = app.get_webview_window("main") else {
        return;
    };
    if window.is_visible().unwrap_or(false) {
        let _ = window.hide();
    } else {
        show_panel(app, Some(position));
    }
}

fn setup_tray(app: &App) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "显示配额", true, None::<&str>)?;
    let refresh = MenuItem::with_id(app, "refresh", "立即刷新", true, None::<&str>)?;
    let settings = MenuItem::with_id(app, "settings", "设置", true, None::<&str>)?;
    let check_update = MenuItem::with_id(app, "check-update", "检查更新", true, None::<&str>)?;
    let releases = MenuItem::with_id(app, "releases", "打开下载页面", true, None::<&str>)?;
    let separator_one = PredefinedMenuItem::separator(app)?;
    let separator_two = PredefinedMenuItem::separator(app)?;
    let quit = MenuItem::with_id(app, "quit", "退出", true, None::<&str>)?;
    let menu = Menu::with_items(
        app,
        &[
            &show,
            &refresh,
            &separator_one,
            &settings,
            &check_update,
            &releases,
            &separator_two,
            &quit,
        ],
    )?;

    TrayIconBuilder::with_id("main")
        .icon(tray_icon::render(None, false))
        .tooltip("MetrikLite 正在连接 Codex")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id.as_ref() {
            "show" => show_panel(app, None),
            "refresh" => {
                let app = app.clone();
                tauri::async_runtime::spawn(async move {
                    refresh_application(&app).await;
                });
            }
            "settings" => {
                show_panel(app, None);
                let _ = app.emit("open-settings", ());
            }
            "check-update" => {
                show_panel(app, None);
                let _ = app.emit("check-update", ());
            }
            "releases" => {
                let _ = open::that("https://github.com/mors-lee/MetrikLite/releases/latest");
            }
            "quit" => {
                let app = app.clone();
                tauri::async_runtime::spawn(async move {
                    shutdown_and_exit(app).await;
                });
            }
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                position,
                ..
            } = event
            {
                toggle_panel(tray.app_handle(), (position.x, position.y));
            }
        })
        .build(app)?;
    Ok(())
}

fn start_refresh_loop(app: AppHandle) {
    tauri::async_runtime::spawn(async move {
        loop {
            refresh_application(&app).await;
            let seconds = app
                .state::<AppCore>()
                .config
                .read()
                .await
                .refresh_seconds
                .clamp(10, 300);
            tokio::time::sleep(Duration::from_secs(seconds)).await;
        }
    });
}

async fn shutdown_and_exit(app: AppHandle) {
    let state = app.state::<AppCore>();
    state.codex.lock().await.shutdown().await;
    logging::info("MetrikLite exiting");
    app.exit(0);
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            show_panel(app, None);
        }))
        .setup(|app| {
            logging::init();
            logging::info(format!(
                "MetrikLite v{} starting",
                env!("CARGO_PKG_VERSION")
            ));
            let mut configuration = config::load();
            configuration.autostart = config::is_autostart_enabled();
            app.manage(AppCore {
                config: RwLock::new(configuration),
                payload: RwLock::new(AppPayload::connecting()),
                codex: Mutex::new(codex::CodexManager::new()),
                refresh_gate: Mutex::new(()),
            });
            setup_tray(app)?;
            start_refresh_loop(app.handle().clone());
            Ok(())
        })
        .on_window_event(|window, event| {
            if window.label() == "main" {
                if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .invoke_handler(tauri::generate_handler![
            get_app_state,
            get_config,
            refresh_now,
            save_config,
            choose_codex_binary,
            open_url,
            hide_panel,
            set_panel_expanded,
            quit_app
        ])
        .run(tauri::generate_context!())
        .expect("failed to run MetrikLite");
}

#[cfg(test)]
mod tests {
    use super::tray_tooltip;
    use crate::models::{AppPayload, QuotaWindow};

    #[test]
    fn tooltip_has_percentage_and_reset_on_separate_lines() {
        let payload = AppPayload::ready(vec![QuotaWindow {
            adapter_id: "codex".into(),
            window_key: "secondary".into(),
            remaining_percent: 42.0,
            resets_at_ms: Some(1_900_000_000_000),
            collected_at_ms: 1_800_000_000_000,
            quality: "official_live".into(),
            source_label: "Codex app-server".into(),
        }]);
        let tooltip = tray_tooltip(&payload);
        assert!(tooltip.starts_with("Codex 剩余 42%\n重置 "));
    }
}
