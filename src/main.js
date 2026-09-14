const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;

const $ = (selector) => document.querySelector(selector);
const elements = {
  statusDot: $(".status-dot"),
  statusText: $("#status-text"),
  content: $("#quota-content"),
  empty: $("#empty-state"),
  emptyMessage: $("#empty-message"),
  updatedAt: $("#updated-at"),
  refresh: $("#refresh-button"),
  refreshSeconds: $("#refresh-seconds"),
  refreshOutput: $("#refresh-output"),
  autostart: $("#autostart"),
  lightTheme: $("#light-theme-button"),
  darkTheme: $("#dark-theme-button"),
  toast: $("#toast"),
};

const quotaElements = {
  primary: {
    card: document.querySelector('[data-window="primary"]'),
    percent: $("#primary-percent"),
    meter: $("#primary-meter"),
    reset: $("#primary-reset"),
  },
  secondary: {
    card: document.querySelector('[data-window="secondary"]'),
    percent: $("#secondary-percent"),
    meter: $("#secondary-meter"),
    reset: $("#secondary-reset"),
  },
};

let appState = null;
let config = null;
let toastTimer = null;

function formatPercent(value) {
  if (!Number.isFinite(value)) return "—";
  const rounded = Math.round(value * 10) / 10;
  return `${Number.isInteger(rounded) ? rounded.toFixed(0) : rounded.toFixed(1)}%`;
}

function formatReset(timestamp) {
  if (!timestamp) return "暂未提供重置时间";
  const date = new Date(timestamp);
  const absolute = new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", hour12: false,
  }).format(date);
  const minutes = Math.round((timestamp - Date.now()) / 60000);
  let relative;
  if (minutes <= 0) relative = "即将重置";
  else if (minutes < 60) relative = `${minutes} 分钟后`;
  else if (minutes < 1440) relative = `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分后`;
  else relative = `${Math.floor(minutes / 1440)} 天 ${Math.floor((minutes % 1440) / 60)} 小时后`;
  return `重置：${absolute} · ${relative}`;
}

function setQuotaWindow(key, quota) {
  const target = quotaElements[key];
  target.card.classList.toggle("hidden", !quota);
  if (!quota) return;
  const remaining = Math.max(0, Math.min(100, quota.remaining_percent));
  target.percent.textContent = formatPercent(remaining);
  target.meter.style.width = `${remaining}%`;
  target.meter.classList.toggle("low", remaining <= 20);
  target.reset.textContent = formatReset(quota.resets_at_ms);
}

function renderState(state) {
  appState = state;
  const dotClass = state.status === "ready" ? "live" : state.status === "error" ? "error" : "";
  elements.statusDot.className = `status-dot ${dotClass}`;
  elements.statusText.textContent = state.status_text;
  const primary = state.windows.find((item) => item.window_key === "primary");
  const secondary = state.windows.find((item) => item.window_key === "secondary");
  setQuotaWindow("primary", primary);
  setQuotaWindow("secondary", secondary);
  const hasQuota = Boolean(primary || secondary);
  elements.content.classList.toggle("hidden", !hasQuota);
  elements.empty.classList.toggle("hidden", hasQuota);
  elements.emptyMessage.textContent = state.error_message
    || (state.status === "ready" ? "Codex 当前未提供可用配额窗口。" : "请确认已安装并登录独立 Codex CLI。");
  elements.updatedAt.textContent = state.refreshed_at_ms
    ? `更新于 ${new Date(state.refreshed_at_ms).toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false })}`
    : "等待更新";
}

function applyTheme(theme) {
  const normalized = theme === "dark" ? "dark" : "light";
  document.documentElement.dataset.theme = normalized;
  elements.lightTheme.setAttribute("aria-pressed", String(normalized === "light"));
  elements.darkTheme.setAttribute("aria-pressed", String(normalized === "dark"));
}

function populateSettings(nextConfig) {
  config = structuredClone(nextConfig);
  applyTheme(config.theme);
  elements.refreshSeconds.value = config.refresh_seconds;
  elements.refreshOutput.textContent = `${config.refresh_seconds} 秒`;
  elements.autostart.checked = config.autostart;
}

function showToast(message, duration = 2600) {
  clearTimeout(toastTimer);
  elements.toast.textContent = message;
  elements.toast.classList.remove("hidden");
  toastTimer = setTimeout(() => elements.toast.classList.add("hidden"), duration);
}

async function refresh() {
  elements.refresh.classList.add("spinning");
  elements.refresh.disabled = true;
  try {
    renderState(await invoke("refresh_now"));
  } catch (error) {
    showToast(`刷新失败：${error}`);
  } finally {
    elements.refresh.classList.remove("spinning");
    elements.refresh.disabled = false;
  }
}

async function saveSettings() {
  const next = {
    ...config,
    theme: document.documentElement.dataset.theme === "dark" ? "dark" : "light",
    refresh_seconds: Number(elements.refreshSeconds.value),
    autostart: elements.autostart.checked,
  };
  try {
    populateSettings(await invoke("save_config", { config: next }));
    showToast("设置已保存");
    await refresh();
  } catch (error) {
    showToast(`保存失败：${error}`);
  }
}

function compareVersions(a, b) {
  const left = a.replace(/^v/i, "").split(".").map(Number);
  const right = b.replace(/^v/i, "").split(".").map(Number);
  for (let i = 0; i < Math.max(left.length, right.length); i += 1) {
    const difference = (left[i] || 0) - (right[i] || 0);
    if (difference) return difference;
  }
  return 0;
}

async function checkUpdate() {
  const button = $("#check-update-button");
  button.disabled = true;
  button.textContent = "检查中…";
  try {
    const response = await fetch("https://api.github.com/repos/mors-lee/MetrikLite/releases/latest", {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!response.ok) throw new Error(`GitHub ${response.status}`);
    const release = await response.json();
    if (compareVersions(release.tag_name, appState.version) > 0) {
      const openPage = confirm(`发现新版本 ${release.tag_name}，当前为 v${appState.version}。是否打开下载页面？`);
      if (openPage) await invoke("open_url", { url: release.html_url });
    } else {
      showToast(`当前已是最新版本 v${appState.version}`);
    }
  } catch (error) {
    showToast(`检查更新失败：${error}`);
  } finally {
    button.disabled = false;
    button.textContent = "检查更新";
  }
}

elements.refresh.addEventListener("click", refresh);
$("#close-panel-button").addEventListener("click", () => invoke("hide_panel"));
$("#save-settings-button").addEventListener("click", saveSettings);
$("#check-update-button").addEventListener("click", checkUpdate);
elements.refreshSeconds.addEventListener("input", () => {
  elements.refreshOutput.textContent = `${elements.refreshSeconds.value} 秒`;
});
elements.lightTheme.addEventListener("click", () => applyTheme("light"));
elements.darkTheme.addEventListener("click", () => applyTheme("dark"));
document.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") return;
  invoke("hide_panel");
});

await listen("quota-updated", (event) => renderState(event.payload));
await listen("check-update", () => {
  checkUpdate();
});

try {
  const [initialState, initialConfig] = await Promise.all([
    invoke("get_app_state"),
    invoke("get_config"),
  ]);
  renderState(initialState);
  populateSettings(initialConfig);
} catch (error) {
  showToast(`初始化失败：${error}`, 5000);
}
