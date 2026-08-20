// ============================================================================
// TrayHost.cs —— 托盘宿主：定时刷新、数字图标生命周期、右键菜单和开机自启
// ============================================================================

using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;
using Application = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;

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
    private HwndSource? _systemMessageWindow;
    private DetailsWindow? _details;
    private bool _refreshing;
    private bool _disposed;

    private const int WmPowerBroadcast = 0x0218;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeCritical = 0x0006;
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

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
        CreateSystemMessageWindow();
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
            // app-server 会话完全在线程池中运行，避免应用退出时 UI Dispatcher
            // 与会话锁互相等待；结果回来后再由当前 Dispatcher 更新托盘。
            var snapshots = await Task.Run(() => CodexAppServer.ReadAsync(
                TimeSpan.FromSeconds(25), _config.CodexBinaryPath));
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
        "codex" => ("Codex", "#4E82E8"),
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

        // 没有找到 Codex 或配额时也保留一个状态图标，用户可以右键打开菜单
        // 设置 D 盘等位置的独立 Codex CLI，而不是陷入“没有图标无法配置”的死循环。
        if (desired.Count == 0)
        {
            const string statusText = "MetrikLite：未找到 Codex CLI；右键设置路径";
            desired.Add(("status", statusText,
                _ => IconRenderer.ToIcon(IconRenderer.RenderStatus(
                    "!", System.Windows.Media.Color.FromRgb(0xD1, 0x34, 0x38),
                    _config.LightGlyphs, px))));
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

    /// <summary>
    /// Explorer 重启或电脑从睡眠恢复后，NotifyIcon 对象可能还在，但 Shell 中
    /// 的图标已经丢失。通过 Visible=false/true 强制重新注册，不等待用户手动拖动。
    /// </summary>
    private void ReRegisterTrayIcons(string reason)
    {
        if (_entries.Count == 0)
        {
            RefreshSafe();
            return;
        }

        Log.Info($"re-registering tray icons: {reason}");
        foreach (var entry in _entries.Values)
        {
            try
            {
                entry.NotifyIcon.Visible = false;
                entry.NotifyIcon.Visible = true;
            }
            catch (Exception ex)
            {
                Log.Error("failed to re-register tray icon", ex);
            }
        }

        RefreshSafe();
    }

    private void CreateSystemMessageWindow()
    {
        try
        {
            var parameters = new HwndSourceParameters("MetrikLiteSystemMessages")
            {
                Width = 1,
                Height = 1,
                WindowStyle = unchecked((int)0x80000000), // WS_POPUP：不显示普通窗口边框
                ExtendedWindowStyle = 0x00000080 | 0x08000000, // TOOLWINDOW + NOACTIVATE
            };
            _systemMessageWindow = new HwndSource(parameters);
            _systemMessageWindow.AddHook(SystemMessageWindowProc);
        }
        catch (Exception ex)
        {
            Log.Error("failed to create system message listener", ex);
        }
    }

    private IntPtr SystemMessageWindowProc(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == TaskbarCreatedMessage)
        {
            Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ReRegisterTrayIcons("Explorer/taskbar recreated")));
        }
        else if (msg == WmPowerBroadcast &&
                 (wParam.ToInt32() == PbtApmResumeAutomatic ||
                  wParam.ToInt32() == PbtApmResumeSuspend ||
                  wParam.ToInt32() == PbtApmResumeCritical))
        {
            Application.Current.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => ReRegisterTrayIcons("system resumed from sleep")));
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    private static string BuildAgentTooltip(AgentQuota agent)
    {
        var p = agent.Primary;
        var lines = new List<string> { $"{agent.DisplayName} 剩余 {p.RemainingPercent:0.#}%" };
        if (p.ResetsAtMs is long reset)
        {
            lines.Add($"重置 {DateTimeOffset.FromUnixTimeMilliseconds(reset).ToLocalTime():MM-dd HH:mm}");
        }
        return string.Join("\n", lines);
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

        var checkUpdate = new WinForms.ToolStripMenuItem("检查更新") { Name = "check-update" };
        checkUpdate.Click += (_, _) => _ = CheckForUpdatesAsync(checkUpdate);
        menu.Items.Add(checkUpdate);

        var codexPath = new WinForms.ToolStripMenuItem("设置 Codex CLI 路径")
        {
            Name = "codex-path",
            ToolTipText = "选择独立安装的 codex.exe 或 codex.cmd；不要选择 WindowsApps 内置文件",
        };
        codexPath.Click += (_, _) => ChooseCodexBinary(codexPath);
        menu.Items.Add(codexPath);
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

    private async Task CheckForUpdatesAsync(WinForms.ToolStripMenuItem item)
    {
        item.Enabled = false;
        item.Text = "检查更新中…";
        try
        {
            var result = await UpdateChecker.CheckAsync();
            if (result.IsUpdateAvailable)
            {
                var answer = WpfMessageBox.Show(
                    $"发现新版本 v{result.LatestVersionText}。\n当前版本：v{result.CurrentVersionText}\n\n是否打开下载页面？",
                    "MetrikLite 更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes)
                {
                    UpdateChecker.OpenReleasePage(result.ReleaseUrl);
                }
            }
            else
            {
                WpfMessageBox.Show(
                    $"当前已是最新版本 v{result.CurrentVersionText}。",
                    "MetrikLite 更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Log.Error("update check failed", ex);
            WpfMessageBox.Show(
                "检查更新失败，请确认网络连接后重试。",
                "MetrikLite 更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            item.Text = "检查更新";
            item.Enabled = true;
        }
    }

    private void ChooseCodexBinary(WinForms.ToolStripMenuItem item)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择独立 Codex CLI",
            Filter = "Codex CLI (codex.exe;codex.cmd;codex.bat)|codex.exe;codex.cmd;codex.bat|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.FileName.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            WpfMessageBox.Show(
                "Codex 桌面版 WindowsApps 内置 CLI 受系统权限保护，独立程序无法直接启动。\n\n请安装独立 Codex CLI，或选择 D 盘等可访问位置中的 codex.exe / codex.cmd。",
                "无法使用该 Codex 路径",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _config.CodexBinaryPath = dialog.FileName;
        ConfigStore.Save(_config);
        Log.Info($"configured Codex binary: {dialog.FileName}");
        item.Text = "Codex CLI 路径已设置";
        RefreshSafe();
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
            _details.RefreshRequested += (_, _) => RefreshSafe();
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
        try
        {
            Task.Run(CodexAppServer.ShutdownAsync).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("failed to shut down Codex app-server", ex);
        }
        if (_systemMessageWindow != null)
        {
            _systemMessageWindow.RemoveHook(SystemMessageWindowProc);
            _systemMessageWindow.Dispose();
            _systemMessageWindow = null;
        }
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
