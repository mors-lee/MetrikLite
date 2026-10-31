use crate::models::QuotaWindow;
use anyhow::{bail, Context, Result};
use serde_json::Value;
use std::{
    fs::{self, File},
    io::{BufRead, BufReader, Read, Seek, SeekFrom},
    path::{Path, PathBuf},
    time::{Duration, Instant, SystemTime},
};

const HEADER_BYTES: u64 = 256 * 1024;
const TAIL_BYTES: u64 = 1024 * 1024;
const CANDIDATE_LIMIT: usize = 16;
const DISCOVERY_INTERVAL: Duration = Duration::from_secs(30);

// This reader never starts a CLI, reads auth.json, or accesses a credential store.
// Only Desktop-origin session metadata and token_count quota events are parsed.
pub struct DesktopQuotaReader {
    candidates: Vec<PathBuf>,
    last_discovery: Option<Instant>,
}

impl DesktopQuotaReader {
    pub fn new() -> Self {
        Self {
            candidates: Vec::new(),
            last_discovery: None,
        }
    }

    pub async fn read(&mut self) -> Result<Vec<QuotaWindow>> {
        if self.candidates.is_empty()
            || self
                .last_discovery
                .map(|time| time.elapsed() >= DISCOVERY_INTERVAL)
                .unwrap_or(true)
        {
            // Use the desktop's standard session directory, never CLI overrides.
            let root = dirs::home_dir()
                .context("无法定位 Codex 桌面端用户目录")?
                .join(".codex")
                .join("sessions");
            self.candidates = tokio::task::spawn_blocking(move || desktop_sessions(&root))
                .await
                .context("读取桌面端配额缓存失败")??;
            self.last_discovery = Some(Instant::now());
        }
        let paths = self.candidates.clone();
        tokio::task::spawn_blocking(move || read_snapshot(&paths))
            .await
            .context("读取桌面端配额缓存失败")?
    }
}

fn desktop_sessions(root: &Path) -> Result<Vec<PathBuf>> {
    if !root.is_dir() {
        bail!("尚未找到 Codex 桌面端配额记录，请先在桌面端使用一次后刷新")
    }
    let mut directories = vec![root.to_path_buf()];
    let mut files = Vec::new();
    let oldest = SystemTime::now()
        .checked_sub(Duration::from_secs(7 * 24 * 60 * 60))
        .unwrap_or(SystemTime::UNIX_EPOCH);
    while let Some(directory) = directories.pop() {
        let Ok(entries) = fs::read_dir(directory) else {
            continue;
        };
        for entry in entries.flatten() {
            let Ok(kind) = entry.file_type() else {
                continue;
            };
            if kind.is_dir() {
                directories.push(entry.path());
            } else if kind.is_file() {
                let path = entry.path();
                let name = entry.file_name();
                let name = name.to_string_lossy();
                if !name.starts_with("rollout-") || !name.ends_with(".jsonl") {
                    continue;
                }
                let Ok(modified) = entry.metadata().and_then(|meta| meta.modified()) else {
                    continue;
                };
                if modified >= oldest {
                    files.push((modified, path));
                }
            }
        }
    }
    files.sort_by_key(|file| std::cmp::Reverse(file.0));
    Ok(files
        .into_iter()
        .map(|(_, path)| path)
        .filter(|path| is_desktop_session(path).unwrap_or(false))
        .take(CANDIDATE_LIMIT)
        .collect())
}

fn is_desktop_metadata(line: &str) -> bool {
    let Ok(metadata) = serde_json::from_str::<Value>(line) else {
        return false;
    };
    if metadata.get("type").and_then(Value::as_str) != Some("session_meta") {
        return false;
    }
    let payload = &metadata["payload"];
    let originator = payload["originator"].as_str().unwrap_or_default();
    let originator = originator.to_ascii_lowercase().replace([' ', '_', '-'], "");
    originator == "codexdesktop" && payload["source"].as_str() != Some("cli")
}

fn is_desktop_session(path: &Path) -> Result<bool> {
    let file = File::open(path)?;
    let mut line = String::new();
    BufReader::new(file.take(HEADER_BYTES)).read_line(&mut line)?;
    Ok(is_desktop_metadata(&line))
}

fn read_snapshot(paths: &[PathBuf]) -> Result<Vec<QuotaWindow>> {
    let now = chrono::Utc::now().timestamp_millis();
    let mut newest: Option<(i64, Vec<QuotaWindow>)> = None;
    for path in paths {
        let Ok(Some(snapshot)) = latest_snapshot(path, now) else {
            continue;
        };
        if newest
            .as_ref()
            .map(|old| snapshot.0 > old.0)
            .unwrap_or(true)
        {
            newest = Some(snapshot);
        }
    }
    match newest {
        Some((_, windows)) if !windows.is_empty() => Ok(windows),
        _ => bail!("桌面端尚无有效配额记录，请在 Codex 桌面端使用一次后刷新"),
    }
}

fn latest_snapshot(path: &Path, now: i64) -> Result<Option<(i64, Vec<QuotaWindow>)>> {
    // Revalidate the source even when reusing a discovered path.
    if !is_desktop_session(path)? {
        return Ok(None);
    }
    let mut file = File::open(path)?;
    let start = file.metadata()?.len().saturating_sub(TAIL_BYTES);
    file.seek(SeekFrom::Start(start))?;
    let mut bytes = Vec::new();
    file.take(TAIL_BYTES).read_to_end(&mut bytes)?;
    let text = String::from_utf8_lossy(&bytes);
    // Incomplete tail fragments are rejected by JSON parsing below.
    for line in text.lines().rev() {
        if !line.contains("\"token_count\"") || !line.contains("\"rate_limits\"") {
            continue;
        }
        if let Some(snapshot) = parse_quota_event(line, now) {
            return Ok(Some(snapshot));
        }
    }
    Ok(None)
}

fn parse_quota_event(line: &str, now: i64) -> Option<(i64, Vec<QuotaWindow>)> {
    let event: Value = serde_json::from_str(line).ok()?;
    if event["type"].as_str()? != "event_msg" || event["payload"]["type"].as_str()? != "token_count"
    {
        return None;
    }
    let collected = chrono::DateTime::parse_from_rfc3339(event["timestamp"].as_str()?)
        .ok()?
        .timestamp_millis();
    if collected > now.saturating_add(5 * 60 * 1000) {
        return None;
    }
    let limits = event["payload"].get("rate_limits")?;
    if limits["limit_id"].as_str().is_some_and(|id| id != "codex") {
        return None;
    }
    let mut windows = Vec::new();
    for (slot, minutes) in [("primary", 300), ("secondary", 10_080)] {
        let window = &limits[slot];
        let Some(used) = window["used_percent"].as_f64() else {
            continue;
        };
        if window["window_minutes"].as_i64() != Some(minutes) {
            continue;
        }
        let reset = window["resets_at"]
            .as_i64()
            .and_then(|value| value.checked_mul(1000));
        let valid_until = reset.unwrap_or_else(|| collected.saturating_add(minutes * 60 * 1000));
        if valid_until <= now {
            continue; // Never carry old usage into a new quota window.
        }
        windows.push(QuotaWindow {
            adapter_id: "codex".into(),
            window_key: slot.into(),
            remaining_percent: (100.0 - used).clamp(0.0, 100.0),
            resets_at_ms: reset,
            collected_at_ms: collected,
            quality: "desktop_cache".into(),
            source_label: "Codex 桌面端配额缓存".into(),
        });
    }
    // A newer empty/null snapshot invalidates older cached quotas.
    Some((collected, windows))
}

#[cfg(test)]
mod tests {
    use super::{is_desktop_metadata, parse_quota_event};
    use serde_json::json;

    const NOW: i64 = 1_793_328_000_000;

    fn event() -> serde_json::Value {
        json!({"timestamp":"2026-10-30T00:00:00Z","type":"event_msg","payload":{
        "type":"token_count","rate_limits":{"limit_id":"codex",
            "primary":{"used_percent":19.0,"window_minutes":300,"resets_at":1_793_340_000},
            "secondary":{"used_percent":35.0,"window_minutes":10080,"resets_at":1_793_900_000}
        }}})
    }

    #[test]
    fn only_desktop_origin_is_accepted() {
        assert!(is_desktop_metadata(&json!({"type":"session_meta","payload":{"originator":"Codex Desktop","source":"vscode"}}).to_string()));
        assert!(!is_desktop_metadata(
            &json!({"type":"session_meta","payload":{"originator":"codex-tui","source":"cli"}})
                .to_string()
        ));
        assert!(!is_desktop_metadata(
            &json!({"type":"session_meta","payload":{"originator":"Codex Desktop","source":"cli"}})
                .to_string()
        ));
    }

    #[test]
    fn reads_quota_percentages_and_original_timestamp() {
        let (timestamp, windows) = parse_quota_event(&event().to_string(), NOW).unwrap();
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].remaining_percent, 81.0);
        assert_eq!(windows[1].remaining_percent, 65.0);
        assert_eq!(windows[0].collected_at_ms, timestamp);
        assert_eq!(windows[0].quality, "desktop_cache");
    }

    #[test]
    fn expired_window_is_not_reused() {
        let mut value = event();
        value["payload"]["rate_limits"]["primary"]["resets_at"] = json!(NOW / 1000 - 1);
        let (_, windows) = parse_quota_event(&value.to_string(), NOW).unwrap();
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].window_key, "secondary");
    }

    #[test]
    fn null_quota_invalidates_previous_data() {
        let mut value = event();
        value["payload"]["rate_limits"] = Value::Null;
        assert!(parse_quota_event(&value.to_string(), NOW)
            .unwrap()
            .1
            .is_empty());
    }

    #[test]
    fn conversation_payload_is_not_a_quota_event() {
        assert!(parse_quota_event(
            &json!({"type":"response_item","payload":{"type":"message","rate_limits":{}}})
                .to_string(),
            NOW
        )
        .is_none());
    }

    use serde_json::Value;
}
