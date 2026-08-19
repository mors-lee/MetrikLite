// ============================================================================
// TrayHost.cs —— 托盘宿主：定时刷新、数字图标生命周期、右键菜单和开机自启
// ============================================================================

using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;

namespace MetrikLite;

/// <summary>一次刷新得到的完整状态快照。</summary>
public sealed record TrayState(
    IReadOnlyList<AgentQuota> Agents,
    DateTimeOffset ReadAt);

/// <summary>托盘宿主。App.OnStartup 创建并 Start()；退出时 Dispose()。</summary>
public sealed class TrayHost : IDisposable
{
    private const int IconSize = 16;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MetrikLite";

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TrayEntry> _entries = new();
    private readonly TrayConfig _config;
    private TrayState _state = new(Array.Empty<AgentQuota>(), DateTimeOffset.MinValue);
    private DetailsWindow? _details;
    private bool _refreshing;
    private bool _disposed;

    public TrayHost()
    {
        _config = ConfigStore.Load();
        var interval = TimeSpan.FromSeconds(Math.Max(10, _config.RefreshSeconds));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        _timer.Tick += (_, _) => RefreshSafe();
    }

    /// <summary>启动：立即刷新一次并开启定时器。</summary>
    public void Start()
    {
        Log.Info("tray host starting");
        RefreshSafe();
        _timer.Start();
    }

    private void RefreshSafe() => _ = RefreshAsync();

    /// <summary>拉取 Codex 配额并同步托盘图标与详情面板。</summary>
    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var snapshots = await CodexAppServer.ReadAsync(TimeSpan.FromSeconds(25));
            var agents = GroupAgents(snapshots);
            _state = new TrayState(agents, DateTimeOffset.Now);
            Log.Info($"refreshed: agents={agents.Count}");
            SyncIcons();

            if (_details is { IsLoaded: true })
            {
                _details.Update(_state);
            }
        }
        catch (Exception ex)
        {
            Log.Error("refresh failed", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>
    /// 按 Agent 分组，并选出当前最紧且尚未过期的配额窗口作为主窗口。
    /// </summary>
    internal static IReadOnlyList<AgentQuota> GroupAgents(IReadOnlyList<QuotaSnapshot> snapshots)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return snapshots
            .GroupBy(s => s.AdapterId)
            .Select(g =>
            {
                var primary = g
                    .OrderByDescending(s => (s.ResetsAtMs ?? 0) > now)
                    .ThenBy(s => s.RemainingPercent)
                    .First();
                var (name, hex) = AgentIdentity(g.Key);
                return new AgentQuota(g.Key, name, hex, primary, g.ToList());
            })
            .OrderBy(a => a.Primary.RemainingPercent)
            .ToList();
    }

    /// <summary>Agent 标识 → 展示名和品牌色，为后续适配其他 Agent 预留。</summary>
    private static (string Name, string Hex) AgentIdentity(string adapterId) => adapterId.ToLowerInvariant() switch
    {
        "codex" => ("Codex", "#6B7C32"),
        "claude" => ("Claude", "#D97757"),
        "zcode" => ("ZCode / GLM", "#1E6FFF"),
        "opencode" => ("OpenCode", "#8B5CF6"),
        "kimi" or "kimiwork" => ("Kimi", "#4F46E5"),
        "antigravity" => ("Antigravity", "#00B8A9"),
        "qoder" => ("Qoder", "#E67E22"),
        "workbuddy" => ("CodeBuddy", "#2B7DE9"),
        _ => (adapterId, "#5A5A5A"),
    };

    private int TrayPixelSize()
    {
        try
        {
            using var g = Drawing.Graphics.FromHwnd(IntPtr.Zero);
            var scale = g.DpiX / 96.0;
            return Math.Clamp((int)Math.Round(IconSize * scale), 16, 32);
        }
        catch
        {
            return 16;
        }
    }

    /// <summary>按当前 Agent 状态增量创建、更新或销毁托盘数字图标。</summary>
    private void SyncIcons()
    {
        var px = TrayPixelSize();
        var desired = new List<(string Key, string Text, Func<int, Drawing.Icon> Render)>();

        foreach (var agent in _state.Agents)
        {
            if (!_config.IsAgentVisible(agent.AdapterId))
            {
                continue;
            }

            var percent = (int)Math.Round(agent.Primary.RemainingPercent);
            var tooltip = BuildAgentTooltip(agent);
            var capturedAgent = agent;
            desired.Add(($"agent:{agent.AdapterId}", tooltip,
                _ => IconRenderer.ToIcon(IconRenderer.RenderPercent(
                    percent, PercentColor(percent, capturedAgent.BrandHex), _config.LightGlyphs, px))));
        }

        var keep = new HashSet<string>();
        foreach (var (key, text, render) in desired)
        {
            keep.Add(key);
            if (_entries.TryGetValue(key, out var existing) &&
                existing.Matches(text, px, _config.LightGlyphs))
            {
                continue;
            }

            if (!_entries.TryGetValue(key, out var entry))
            {
                var ni = new WinForms.NotifyIcon
                {
                    Visible = true,
                    Text = Truncate(text),
                };
                ni.MouseClick += OnIconClick;
                ni.ContextMenuStrip = BuildMenu();
                entry = new TrayEntry(ni);
                _entries[key] = entry;
            }

            entry.Text = text;
            entry.PixelSize = px;
            entry.LightGlyphs = _config.LightGlyphs;
            IconRenderer.SafeDispose(entry.Icon);
            try
            {
                entry.Icon = render(px);
                entry.NotifyIcon.Icon = entry.Icon;
            }
            catch (Exception ex)
            {
                Log.Error($"failed to render icon {key}", ex);
            }

            entry.NotifyIcon.Text = Truncate(text);
        }

        foreach (var stale in _entries.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            var entry = _entries[stale];
            entry.NotifyIcon.Visible = false;
            entry.NotifyIcon.Dispose();
            IconRenderer.SafeDispose(entry.Icon);
            _entries.Remove(stale);
        }
    }

    private static string BuildAgentTooltip(AgentQuota agent)
    {
        var p = agent.Primary;
        var parts = new List<string> { $"{agent.DisplayName} 剩余 {p.RemainingPercent:0.#}%" };
        if (p.ResetsAtMs is long reset)
        {
            parts.Add($"重置 {DateTimeOffset.FromUnixTimeMilliseconds(reset).ToLocalTime():MM-dd HH:mm}");
        }
        return string.Join(" · ", parts);
    }

    private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

    private static System.Windows.Media.Color ParseColor(string hex)
        => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static System.Windows.Media.Color PercentColor(int percent, string brandHex)
        => percent <= 20 ? ParseColor("#D13438") : ParseColor(brandHex);

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Opening += (_, _) => RebuildMenuItems(menu);
        RebuildMenuItems(menu);
        return menu;
    }

    private void RebuildMenuItems(WinForms.ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var refresh = new WinForms.ToolStripMenuItem("立即刷新") { Name = "refresh" };
        refresh.Click += (_, _) => RefreshSafe();
        menu.Items.Add(refresh);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var lightItem = new WinForms.ToolStripMenuItem("浅色图标（深色任务栏）")
        {
            CheckOnClick = true,
            Checked = _config.LightGlyphs,
        };
        lightItem.Click += (_, _) =>
        {
            _config.LightGlyphs = lightItem.Checked;
            ConfigStore.Save(_config);
            RefreshSafe();
        };
        menu.Items.Add(lightItem);

        if (_state.Agents.Count > 0)
        {
            var agentMenu = new WinForms.ToolStripMenuItem("Agent 显示");
            foreach (var agent in _state.Agents)
            {
                var capturedAgent = agent;
                var item = new WinForms.ToolStripMenuItem(capturedAgent.DisplayName)
                {
                    CheckOnClick = true,
                    Checked = _config.IsAgentVisible(capturedAgent.AdapterId),
                };
                item.Click += (_, _) =>
                {
                    var allIds = _state.Agents.Select(x => x.AdapterId).ToList();
                    _config.ToggleAgent(capturedAgent.AdapterId, allIds);
                    ConfigStore.Save(_config);
                    RefreshSafe();
                };
                agentMenu.DropDownItems.Add(item);
            }
            menu.Items.Add(agentMenu);
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var autoStart = new WinForms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = _config.AutoStart,
        };
        autoStart.Click += (_, _) =>
        {
            _config.AutoStart = autoStart.Checked;
            SetAutoStart(_config.AutoStart);
            ConfigStore.Save(_config);
        };
        menu.Items.Add(autoStart);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exit = new WinForms.ToolStripMenuItem("退出");
        exit.Click += (_, _) =>
        {
            ConfigStore.Save(_config);
            Application.Current.Shutdown();
        };
        menu.Items.Add(exit);
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                key?.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key?.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("failed to update autostart", ex);
        }
    }

    private void OnIconClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left)
        {
            ShowDetails();
        }
    }

    private void ShowDetails()
    {
        if (_details == null || !_details.IsLoaded)
        {
            _details = new DetailsWindow();
        }
        _details.Update(_state);
        _details.Activate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _details?.Close();
        foreach (var entry in _entries.Values)
        {
            entry.NotifyIcon.Visible = false;
            entry.NotifyIcon.Dispose();
            IconRenderer.SafeDispose(entry.Icon);
        }
        _entries.Clear();
        Log.Info("tray host disposed");
    }

    private sealed class TrayEntry
    {
        public TrayEntry(WinForms.NotifyIcon notifyIcon) => NotifyIcon = notifyIcon;

        public WinForms.NotifyIcon NotifyIcon { get; }
        public Drawing.Icon? Icon { get; set; }
        public string Text { get; set; } = "";
        public int PixelSize { get; set; }
        public bool LightGlyphs { get; set; }

        public bool Matches(string text, int px, bool light)
            => Text == text && PixelSize == px && LightGlyphs == light;
    }
}
