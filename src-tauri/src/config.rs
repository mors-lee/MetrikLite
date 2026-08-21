use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use std::{fs, path::PathBuf};

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct AppConfig {
    #[serde(alias = "CodexBinaryPath")]
    pub codex_binary_path: Option<String>,
    #[serde(alias = "RefreshSeconds")]
    pub refresh_seconds: u64,
    #[serde(alias = "LightGlyphs")]
    pub light_glyphs: bool,
    #[serde(alias = "AutoStart")]
    pub autostart: bool,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            codex_binary_path: None,
            refresh_seconds: 30,
            light_glyphs: false,
            autostart: false,
        }
    }
}

impl AppConfig {
    pub fn normalize(mut self) -> Self {
        self.refresh_seconds = self.refresh_seconds.clamp(10, 300);
        self.codex_binary_path = self
            .codex_binary_path
            .take()
            .map(|path| path.trim().trim_matches('"').to_owned())
            .filter(|path| !path.is_empty());
        self
    }
}

pub fn app_dir() -> PathBuf {
    dirs::config_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("MetrikLite")
}

pub fn load() -> AppConfig {
    let path = app_dir().join("config.json");
    fs::read_to_string(path)
        .ok()
        .and_then(|text| serde_json::from_str::<AppConfig>(&text).ok())
        .unwrap_or_default()
        .normalize()
}

pub fn save(config: &AppConfig) -> Result<()> {
    let directory = app_dir();
    fs::create_dir_all(&directory).context("create MetrikLite config directory")?;
    let path = directory.join("config.json");
    let json = serde_json::to_string_pretty(config).context("serialize MetrikLite config")?;
    fs::write(path, json).context("write MetrikLite config")
}

#[cfg(windows)]
pub fn set_autostart(enabled: bool) -> Result<()> {
    use winreg::{enums::HKEY_CURRENT_USER, RegKey};

    let hkcu = RegKey::predef(HKEY_CURRENT_USER);
    let (run_key, _) = hkcu
        .create_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")
        .context("open current-user Run registry key")?;
    if enabled {
        let executable = std::env::current_exe().context("resolve MetrikLite executable")?;
        run_key
            .set_value("MetrikLite", &format!("\"{}\"", executable.display()))
            .context("register MetrikLite autostart")?;
    } else {
        let _ = run_key.delete_value("MetrikLite");
    }
    Ok(())
}

#[cfg(windows)]
pub fn is_autostart_enabled() -> bool {
    use winreg::{enums::HKEY_CURRENT_USER, RegKey};

    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey("Software\\Microsoft\\Windows\\CurrentVersion\\Run")
        .ok()
        .and_then(|key| key.get_value::<String, _>("MetrikLite").ok())
        .is_some()
}

#[cfg(not(windows))]
pub fn set_autostart(_enabled: bool) -> Result<()> {
    Ok(())
}

#[cfg(not(windows))]
pub fn is_autostart_enabled() -> bool {
    false
}
