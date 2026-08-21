use crate::{logging, models::QuotaWindow};
use anyhow::{anyhow, bail, Context, Result};
use serde_json::{json, Value};
use std::{
    ffi::OsString,
    fs,
    path::{Path, PathBuf},
    process::Stdio,
    time::Duration,
};
use tokio::{
    io::{AsyncBufReadExt, AsyncWriteExt, BufReader},
    process::{Child, ChildStdin, Command},
    sync::mpsc,
    time::{timeout, Instant},
};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Debug, Clone)]
struct CommandSpec {
    program: PathBuf,
    arguments: Vec<OsString>,
    key: String,
}

impl CommandSpec {
    fn for_path(path: PathBuf) -> Self {
        let extension = path
            .extension()
            .and_then(|value| value.to_str())
            .unwrap_or_default()
            .to_ascii_lowercase();
        let key = path.to_string_lossy().to_ascii_lowercase();
        if matches!(extension.as_str(), "cmd" | "bat") {
            let command_interpreter = std::env::var_os("ComSpec")
                .map(PathBuf::from)
                .unwrap_or_else(|| PathBuf::from(r"C:\Windows\System32\cmd.exe"));
            Self {
                program: command_interpreter,
                arguments: vec![
                    OsString::from("/D"),
                    OsString::from("/C"),
                    path.into_os_string(),
                    OsString::from("app-server"),
                ],
                key,
            }
        } else {
            Self {
                program: path,
                arguments: vec![OsString::from("app-server")],
                key,
            }
        }
    }
}

pub struct CodexManager {
    session: Option<CodexSession>,
}

impl CodexManager {
    pub fn new() -> Self {
        Self { session: None }
    }

    pub async fn read(&mut self, configured_path: Option<&str>) -> Result<Vec<QuotaWindow>> {
        let specification = resolve_command(configured_path)?;
        self.ensure_session(&specification).await?;

        let result = match self.session.as_mut() {
            Some(session) => session.read_rate_limits().await,
            None => Err(anyhow!("Codex app-server 会话不可用")),
        };

        match result {
            Ok(value) => {
                let windows = parse_rate_limits(&value);
                if windows.is_empty() {
                    bail!("Codex 已连接，但没有返回可用配额；请确认 CLI 已登录")
                }
                Ok(windows)
            }
            Err(error) => {
                logging::error(format!("codex app-server request failed: {error:#}"));
                self.shutdown().await;
                Err(error)
            }
        }
    }

    async fn ensure_session(&mut self, specification: &CommandSpec) -> Result<()> {
        let reuse = if let Some(session) = self.session.as_mut() {
            session.command_key == specification.key && session.child.try_wait()?.is_none()
        } else {
            false
        };
        if reuse {
            return Ok(());
        }

        self.shutdown().await;
        self.session = Some(CodexSession::start(specification.clone()).await?);
        Ok(())
    }

    pub async fn shutdown(&mut self) {
        if let Some(mut session) = self.session.take() {
            session.stop().await;
        }
    }
}

struct CodexSession {
    child: Child,
    stdin: Option<ChildStdin>,
    lines: mpsc::UnboundedReceiver<String>,
    command_key: String,
    next_request_id: u64,
}

impl CodexSession {
    async fn start(specification: CommandSpec) -> Result<Self> {
        let runtime_dir = dirs::data_local_dir()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("MetrikLite")
            .join("Runtime");
        fs::create_dir_all(&runtime_dir).context("create Codex runtime directory")?;

        let mut command = Command::new(&specification.program);
        command
            .args(&specification.arguments)
            .current_dir(runtime_dir)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .kill_on_drop(true);
        #[cfg(windows)]
        command.creation_flags(CREATE_NO_WINDOW);

        let mut child = command.spawn().with_context(|| {
            format!(
                "无法启动 Codex CLI：{}",
                specification.program.to_string_lossy()
            )
        })?;
        let process_id = child.id().unwrap_or_default();
        let stdin = child.stdin.take().context("Codex stdin unavailable")?;
        let stdout = child.stdout.take().context("Codex stdout unavailable")?;
        let stderr = child.stderr.take().context("Codex stderr unavailable")?;
        let (line_sender, lines) = mpsc::unbounded_channel();

        tauri::async_runtime::spawn(async move {
            let mut reader = BufReader::new(stdout).lines();
            while let Ok(Some(line)) = reader.next_line().await {
                if line_sender.send(line).is_err() {
                    break;
                }
            }
        });
        tauri::async_runtime::spawn(async move {
            let mut reader = BufReader::new(stderr).lines();
            while let Ok(Some(_line)) = reader.next_line().await {
                // 必须持续排水，但不记录 CLI 输出，避免日志意外保存敏感内容。
            }
        });

        let mut session = Self {
            child,
            stdin: Some(stdin),
            lines,
            command_key: specification.key,
            next_request_id: 1,
        };
        session
            .send(&json!({
                "id": 1,
                "method": "initialize",
                "params": {
                    "clientInfo": {
                        "name": "metriklite",
                        "title": "MetrikLite",
                        "version": env!("CARGO_PKG_VERSION")
                    },
                    "capabilities": {
                        "experimentalApi": true,
                        "optOutNotificationMethods": []
                    }
                }
            }))
            .await?;
        let _ = session.wait_for_result(1, Duration::from_secs(25)).await?;
        session.send(&json!({ "method": "initialized" })).await?;
        logging::info(format!(
            "codex app-server started for reusable session, pid={process_id}"
        ));
        Ok(session)
    }

    async fn read_rate_limits(&mut self) -> Result<Value> {
        self.next_request_id += 1;
        let request_id = self.next_request_id;
        self.send(&json!({
            "id": request_id,
            "method": "account/rateLimits/read"
        }))
        .await?;
        logging::info(format!(
            "codex app-server rateLimits request sent, id={request_id}"
        ));
        let result = self
            .wait_for_result(request_id, Duration::from_secs(25))
            .await?;
        logging::info(format!(
            "codex app-server rateLimits result received, id={request_id}"
        ));
        Ok(result)
    }

    async fn send(&mut self, value: &Value) -> Result<()> {
        let stdin = self.stdin.as_mut().context("Codex stdin is closed")?;
        let mut line = serde_json::to_vec(value).context("serialize Codex request")?;
        line.push(b'\n');
        stdin
            .write_all(&line)
            .await
            .context("write Codex request")?;
        stdin.flush().await.context("flush Codex request")
    }

    async fn wait_for_result(&mut self, expected_id: u64, duration: Duration) -> Result<Value> {
        let deadline = Instant::now() + duration;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                bail!("Codex app-server 请求超时")
            }
            let line = timeout(remaining, self.lines.recv())
                .await
                .context("Codex app-server 请求超时")?
                .context("Codex app-server 已关闭输出")?;
            let Ok(message) = serde_json::from_str::<Value>(&line) else {
                continue;
            };
            if message.get("id").and_then(Value::as_u64) != Some(expected_id) {
                continue;
            }
            if let Some(error) = message.get("error").filter(|value| !value.is_null()) {
                bail!("Codex app-server 返回错误：{error}")
            }
            return message
                .get("result")
                .filter(|value| !value.is_null())
                .cloned()
                .context("Codex app-server 没有返回 result；CLI 可能尚未登录");
        }
    }

    async fn stop(&mut self) {
        self.stdin.take();
        match timeout(Duration::from_secs(2), self.child.wait()).await {
            Ok(Ok(_)) => logging::info("codex app-server exited gracefully"),
            _ => {
                logging::info("codex app-server graceful shutdown timed out; terminating child");
                let _ = self.child.kill().await;
                let _ = self.child.wait().await;
            }
        }
    }
}

fn resolve_command(configured_path: Option<&str>) -> Result<CommandSpec> {
    if let Some(path) = std::env::var_os("CODEX_BINARY") {
        if let Some(specification) = valid_candidate(PathBuf::from(path), true) {
            return Ok(specification);
        }
    }

    if let Some(path) = configured_path.filter(|value| !value.trim().is_empty()) {
        let selected = PathBuf::from(path.trim().trim_matches('"'));
        if is_protected_windows_apps(&selected) {
            bail!("不能使用 WindowsApps 内置 Codex；请选择独立安装的 Codex CLI")
        }
        if let Some(specification) = valid_candidate(selected.clone(), false) {
            return Ok(specification);
        }
        bail!("设置的 Codex CLI 不存在：{}", selected.display())
    }

    if let Some(local_app_data) = dirs::data_local_dir() {
        let packages = local_app_data
            .join("Microsoft")
            .join("WinGet")
            .join("Packages");
        if let Ok(entries) = fs::read_dir(packages) {
            let mut package_dirs = entries
                .filter_map(Result::ok)
                .map(|entry| entry.path())
                .filter(|path| {
                    path.file_name()
                        .and_then(|name| name.to_str())
                        .map(|name| name.starts_with("OpenAI.Codex_"))
                        .unwrap_or(false)
                })
                .collect::<Vec<_>>();
            package_dirs.sort_by(|left, right| right.cmp(left));
            for directory in package_dirs {
                for name in [
                    "codex.exe",
                    "codex-x86_64-pc-windows-msvc.exe",
                    "codex.cmd",
                    "codex.bat",
                ] {
                    if let Some(specification) = valid_candidate(directory.join(name), false) {
                        return Ok(specification);
                    }
                }
            }
        }
    }

    if let Some(roaming) = dirs::config_dir() {
        if let Some(specification) = valid_candidate(roaming.join("npm").join("codex.cmd"), false) {
            return Ok(specification);
        }
    }

    if let Some(path_value) = std::env::var_os("PATH") {
        for name in ["codex.cmd", "codex.bat", "codex.exe"] {
            for directory in std::env::split_paths(&path_value) {
                if let Some(specification) = valid_candidate(directory.join(name), true) {
                    return Ok(specification);
                }
            }
        }
    }

    bail!("找不到 Codex CLI；请在设置中选择独立 codex.exe 或 codex.cmd")
}

fn valid_candidate(path: PathBuf, reject_special_paths: bool) -> Option<CommandSpec> {
    if !path.is_file() {
        return None;
    }
    if reject_special_paths && (is_protected_windows_apps(&path) || is_known_unrelated(&path)) {
        return None;
    }
    Some(CommandSpec::for_path(path))
}

fn is_protected_windows_apps(path: &Path) -> bool {
    path.to_string_lossy()
        .replace('/', "\\")
        .to_ascii_lowercase()
        .contains("\\windowsapps\\")
}

fn is_known_unrelated(path: &Path) -> bool {
    let normalized = path
        .to_string_lossy()
        .replace('/', "\\")
        .to_ascii_lowercase();
    ["\\trae solo\\", "\\trae solo cn\\", "\\modules\\ai-agent\\"]
        .iter()
        .any(|marker| normalized.contains(marker))
}

pub fn parse_rate_limits(result: &Value) -> Vec<QuotaWindow> {
    let limits = result
        .get("rateLimits")
        .filter(|value| !value.is_null())
        .or_else(|| {
            result
                .get("rateLimitsByLimitId")
                .or_else(|| result.get("rate_limits_by_limit_id"))
                .and_then(|by_id| by_id.get("codex"))
                .filter(|value| !value.is_null())
        });
    let Some(limits) = limits else {
        return Vec::new();
    };

    let collected_at_ms = chrono::Utc::now().timestamp_millis();
    ["primary", "secondary"]
        .into_iter()
        .filter_map(|slot| {
            let window = limits.get(slot)?;
            let used_percent = window.get("usedPercent")?.as_f64()?;
            let duration_minutes = window.get("windowDurationMins").and_then(Value::as_i64);
            let window_key = match duration_minutes {
                Some(minutes) if minutes <= 1440 => "primary",
                Some(_) => "secondary",
                None => slot,
            };
            Some(QuotaWindow {
                adapter_id: "codex".into(),
                window_key: window_key.into(),
                remaining_percent: (100.0 - used_percent).clamp(0.0, 100.0),
                resets_at_ms: window
                    .get("resetsAt")
                    .and_then(Value::as_i64)
                    .map(|seconds| seconds.saturating_mul(1000)),
                collected_at_ms,
                quality: "official_live".into(),
                source_label: "Codex app-server".into(),
            })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{is_protected_windows_apps, parse_rate_limits};
    use serde_json::json;
    use std::path::Path;

    #[test]
    fn parses_primary_and_secondary_windows() {
        let windows = parse_rate_limits(&json!({
            "rateLimits": {
                "primary": { "usedPercent": 13.5, "resetsAt": 1_800_000_000, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 41.0, "resetsAt": 1_800_500_000, "windowDurationMins": 10_080 }
            }
        }));
        assert_eq!(windows.len(), 2);
        assert_eq!(windows[0].window_key, "primary");
        assert_eq!(windows[0].remaining_percent, 86.5);
        assert_eq!(windows[1].window_key, "secondary");
        assert_eq!(windows[1].remaining_percent, 59.0);
    }

    #[test]
    fn accepts_by_limit_id_shape() {
        let windows = parse_rate_limits(&json!({
            "rateLimitsByLimitId": {
                "codex": {
                    "primary": { "usedPercent": 10, "windowDurationMins": 60 }
                }
            }
        }));
        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].remaining_percent, 90.0);
    }

    #[test]
    fn rejects_protected_windows_apps_paths_case_insensitively() {
        assert!(is_protected_windows_apps(Path::new(
            r"C:\Program Files\WindowsApps\OpenAI.Codex\codex.exe"
        )));
        assert!(is_protected_windows_apps(Path::new(
            r"c:/program files/windowsapps/openai.codex/codex.exe"
        )));
        assert!(!is_protected_windows_apps(Path::new(
            r"D:\Tools\Codex\codex.exe"
        )));
    }
}
