// ============================================================================
// 【模块】DetailsWindow.xaml.cs —— 详情面板逻辑（左键托盘图标弹出的悬浮卡）
// ============================================================================
// 【职责】
//   1. Update(TrayState)：接收一次刷新数据，重建面板内容。
//      由 TrayHost.Refresh() 在面板打开时调用；也在 ShowDetails() 里首显前调用。
//   2. BuildAgentRow()：为单个 Agent 生成一行 UI（名称 + 百分比 + 进度条 + 元信息）。
//   3. 定位与自动关闭：贴着任务栏右下角弹出；失去焦点或按 Esc 即关。
//
// 【依赖】Models（AgentQuota/QuotaSnapshot/TrayState）。
//
// 【修改指南】
//   · 想改行的字号/间距/颜色 → BuildAgentRow()（进度条宽度基准 300 是
//     面板内可用宽度，改面板宽度时同步改它）。
//   · 低电量红色阈值（≤20%）→ BuildAgentRow() 的 displayColor 判断，
//     与 TrayHost.PercentColor() 保持一致。
//   · 想显示“已用百分比”而非“剩余”→ BuildAgentRow() 里把
//     RemainingPercent 换成 100 - RemainingPercent。
//   · 元信息行内容（重置时间/更新于/来源）→ BuildSubLine()。
// ============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace MetrikLite;

public partial class DetailsWindow : Window
{
    public DetailsWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 把面板定位到任务栏托盘附近（鼠标所在屏幕的右下角、避开屏幕边缘）。
    /// 前提：内容已布局完成（UpdateLayout 之后高度才是真实值）。
    /// </summary>
    public void PositionNearCursor()
    {
        var cursor = WinForms.Cursor.Position;
        // 用鼠标所在显示器的工作区（避开任务栏本身）
        var work = WinForms.Screen.FromPoint(cursor).WorkingArea;
        Left = Math.Min(cursor.X - Width - 16, work.Right - Width - 8);
        Top = work.Bottom - Height - 16;
        if (Top < work.Top) // 极端小屏兜底
        {
            Top = work.Top;
        }
    }

    /// <summary>
    /// 用新状态重建面板。每次全量 Clear + 重建（Agent 数量少，无需增量优化）。
    /// </summary>
    public void Update(TrayState state)
    {
        RefreshedText.Text = $"更新于 {state.ReadAt:HH:mm:ss} · 直连 Codex app-server 实时读取";

        AgentsPanel.Children.Clear();
        if (state.Agents.Count == 0)
        {
            // 读不到数据时给出可操作的提示，而不是一片空白
            AgentsPanel.Children.Add(new TextBlock
            {
                Text = "暂无配额数据。请确认：① 已安装 Codex CLI 并完成登录；② 详见日志 %APPDATA%\\MetrikLite\\metriklite.log",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            return;
        }

        foreach (var agent in state.Agents)
        {
            AgentsPanel.Children.Add(BuildAgentRow(agent));
        }

        // 先按内容定高 → 再布局 → 再定位（顺序不能反，否则高度不准）
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        PositionNearCursor();
        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>
    /// 构造一个 Agent 的完整行：
    ///   [名称 ................. 12%]
    ///   [■■■□□□□□□□□□ 进度条]
    ///   重置于 08-20 15:05 · 更新于 刚刚 · Codex app-server
    /// </summary>
    private UIElement BuildAgentRow(AgentQuota agent)
    {
        var p = agent.Primary;

        // 品牌色画笔；HEX 非法时退化为中性灰（不抛异常）
        var color = new BrushConverter().ConvertFromString(agent.BrandHex) as Brush
                    ?? new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A));
        // 与托盘图标一致的告警规则：剩余 ≤20% 变红
        var displayColor = p.RemainingPercent <= 20
            ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
            : color;

        // —— 行头：左名称、右百分比 ——
        var name = new TextBlock
        {
            Text = agent.DisplayName,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var percent = new TextBlock
        {
            Text = $"{p.RemainingPercent:0.#}%",
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = displayColor,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var header = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(percent, Dock.Right); // Dock 右侧需先加右子项再加填充项
        header.Children.Add(percent);
        header.Children.Add(name);

        // —— 进度条：灰底 + 品牌色填充（宽度基准 300 ≈ 面板可用宽）——
        var track = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
            Margin = new Thickness(0, 6, 0, 0),
        };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(2.5),
            Background = displayColor,
            Width = Math.Max(2, 300 * Math.Clamp(p.RemainingPercent / 100.0, 0, 1)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };
        track.Child = fill;

        // —— 元信息行 ——
        var sub = BuildSubLine(p);
        sub.Margin = new Thickness(0, 4, 0, 0);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(track);
        panel.Children.Add(sub);
        return panel;
    }

    /// <summary>拼一行元信息：重置时间（过期则标注）· 采集时间 · 数据来源 · 非实时质量标注。</summary>
    private static TextBlock BuildSubLine(QuotaSnapshot p)
    {
        var bits = new List<string>();
        if (p.ResetsAtMs is long resetMs)
        {
            var reset = DateTimeOffset.FromUnixTimeMilliseconds(resetMs).ToLocalTime();
            bits.Add(reset > DateTimeOffset.Now
                ? $"重置于 {reset:MM-dd HH:mm}"
                : $"窗口 {reset:MM-dd HH:mm} 已结束"); // 过期窗口如实标注，不冒充有效
        }
        bits.Add($"更新于 {RelativeTime(p.CollectedAtMs)}");
        bits.Add(p.SourceLabel);
        if (p.Quality != "official_live")
        {
            bits.Add($"质量: {p.Quality}"); // 非“官方实时”的数据让用户知情
        }

        return new TextBlock
        {
            Text = string.Join(" · ", bits),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>Unix 毫秒 → 人性化相对时间（“刚刚 / 5 分钟前 / 3 小时前 / 2 天前”）。</summary>
    private static string RelativeTime(long unixMs)
    {
        var t = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        var delta = DateTimeOffset.Now - t;
        return delta.TotalMinutes < 2 ? "刚刚"
            : delta.TotalHours < 1 ? $"{(int)delta.TotalMinutes} 分钟前"
            : delta.TotalDays < 1 ? $"{(int)delta.TotalHours} 小时前"
            : $"{(int)delta.TotalDays} 天前";
    }

    // 点到面板外任意处 → 自动关闭（悬浮卡的标准行为）
    private void OnDeactivated(object sender, EventArgs e) => Close();

    // Esc 关闭（无标题栏窗口给键盘留的出口）
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
