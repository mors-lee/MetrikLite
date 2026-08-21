use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct QuotaWindow {
    pub adapter_id: String,
    pub window_key: String,
    pub remaining_percent: f64,
    pub resets_at_ms: Option<i64>,
    pub collected_at_ms: i64,
    pub quality: String,
    pub source_label: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct AppPayload {
    pub version: String,
    pub status: String,
    pub status_text: String,
    pub error_message: Option<String>,
    pub windows: Vec<QuotaWindow>,
    pub tray_percent: Option<i32>,
    pub refreshed_at_ms: Option<i64>,
}

impl AppPayload {
    pub fn connecting() -> Self {
        Self {
            version: env!("CARGO_PKG_VERSION").to_owned(),
            status: "connecting".into(),
            status_text: "正在连接 Codex…".into(),
            error_message: None,
            windows: Vec::new(),
            tray_percent: None,
            refreshed_at_ms: None,
        }
    }

    pub fn ready(mut windows: Vec<QuotaWindow>) -> Self {
        windows.sort_by_key(|item| if item.window_key == "primary" { 0 } else { 1 });
        let now = chrono::Utc::now().timestamp_millis();
        let tray_percent = windows
            .iter()
            .find(|item| {
                item.window_key == "secondary"
                    && item.resets_at_ms.map(|reset| reset > now).unwrap_or(true)
            })
            .map(|item| item.remaining_percent.round().clamp(0.0, 100.0) as i32);

        Self {
            version: env!("CARGO_PKG_VERSION").to_owned(),
            status: "ready".into(),
            status_text: "Codex 配额已连接".into(),
            error_message: None,
            windows,
            tray_percent,
            refreshed_at_ms: Some(now),
        }
    }

    pub fn error(message: impl Into<String>) -> Self {
        Self {
            version: env!("CARGO_PKG_VERSION").to_owned(),
            status: "error".into(),
            status_text: "Codex 暂不可用".into(),
            error_message: Some(message.into()),
            windows: Vec::new(),
            tray_percent: None,
            refreshed_at_ms: Some(chrono::Utc::now().timestamp_millis()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::{AppPayload, QuotaWindow};

    fn window(key: &str, remaining_percent: f64) -> QuotaWindow {
        QuotaWindow {
            adapter_id: "codex".into(),
            window_key: key.into(),
            remaining_percent,
            resets_at_ms: Some(1_900_000_000_000),
            collected_at_ms: 1_800_000_000_000,
            quality: "official_live".into(),
            source_label: "Codex app-server".into(),
        }
    }

    #[test]
    fn tray_uses_weekly_window_only() {
        let payload = AppPayload::ready(vec![window("primary", 8.0), window("secondary", 63.0)]);
        assert_eq!(payload.tray_percent, Some(63));
    }
}
