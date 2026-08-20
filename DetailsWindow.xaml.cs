// ============================================================================
// DetailsWindow.xaml.cs —— 托盘上方的轻量配额面板
// ============================================================================

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace MetrikLite;

public partial class DetailsWindow : Window
{
    public event EventHandler? RefreshRequested;

    public DetailsWindow()
    {
        InitializeComponent();
        // 首次 Show 时先隐藏，定位完成后再显示，避免窗口在屏幕中央闪一下。
        Opacity = 0;
    }

    /// <summary>把面板贴到鼠标所在屏幕的任务栏上方右侧，并正确处理 DPI 缩放。</summary>
    public void PositionNearCursor()
    {
        var cursor = WinForms.Cursor.Position;
        var work = WinForms.Screen.FromPoint(cursor).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new Point(work.Left, work.Top));
        var bottomRight = fromDevice.Transform(new Point(work.Right, work.Bottom));

        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        Left = Math.Max(topLeft.X + 10, bottomRight.X - width - 12);
        Top = Math.Max(topLeft.Y + 10, bottomRight.Y - height - 12);
    }

    /// <summary>用新状态重建面板。配额数量很少，全量重建更简单可靠。</summary>
    public void Update(TrayState state)
    {
        RefreshedText.Text = state.ReadAt == DateTimeOffset.MinValue
            ? "等待首次读取…"
            : $"刚刚更新 · {state.ReadAt:HH:mm:ss}";
        RefreshButton.IsEnabled = true;
        LiveDot.Fill = new SolidColorBrush(state.Agents.Count > 0
            ? Color.FromRgb(0x45, 0xB9, 0x7C)
            : Color.FromRgb(0xE7, 0xA5, 0x3B));

        AgentsPanel.Children.Clear();
        if (state.Agents.Count == 0)
        {
            AgentsPanel.Children.Add(BuildEmptyState());
        }
        else
        {
            foreach (var agent in state.Agents)
            {
                AgentsPanel.Children.Add(BuildAgentCard(agent));
            }
        }

        if (!IsVisible)
        {
            Show();
        }

        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        PositionNearCursor();
        Opacity = 1;
    }

    private static UIElement BuildEmptyState()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "暂时没有读取到配额",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(0x2B, 0x36, 0x48),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "请确认 Codex CLI 已登录；也可以右键托盘图标手动选择 CLI 路径。",
            FontSize = 11.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf(0x7A, 0x87, 0x98),
            Margin = new Thickness(0, 6, 0, 0),
        });

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushOf(0xE4, 0xE9, 0xF1),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16),
            Child = panel,
        };
    }

    private static UIElement BuildAgentCard(AgentQuota agent)
    {
        var accent = ParseBrush(agent.BrandHex, Color.FromRgb(0x4E, 0x82, 0xE8));
        var body = new StackPanel();

        var header = new Grid { Margin = new Thickness(1, 0, 1, 11) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var name = new TextBlock
        {
            Text = agent.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf(0x21, 0x2B, 0x3C),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var status = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = BrushOf(0xF0, 0xF4, 0xF8),
            Padding = new Thickness(7, 3, 7, 3),
            Child = new TextBlock
            {
                Text = "实时",
                FontSize = 10,
                Foreground = BrushOf(0x70, 0x7E, 0x91),
            },
        };
        Grid.SetColumn(status, 2);
        header.Children.Add(status);
        body.Children.Add(header);

        var windows = agent.AllWindows
            .OrderBy(w => w.WindowKey.Equals("primary", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        for (var i = 0; i < windows.Count; i++)
        {
            if (i > 0)
            {
                body.Children.Add(new Border
                {
                    Height = 1,
                    Background = BrushOf(0xED, 0xF0, 0xF5),
                    Margin = new Thickness(0, 12, 0, 12),
                });
            }
            body.Children.Add(BuildWindowBlock(windows[i], accent));
        }

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = BrushOf(0xE3, 0xE8, 0xF0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(15, 13, 15, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = body,
        };
    }

    private static UIElement BuildWindowBlock(QuotaSnapshot snapshot, Brush accent)
    {
        var remaining = Math.Clamp(snapshot.RemainingPercent, 0, 100);
        var display = remaining <= 20 ? BrushOf(0xE4, 0x54, 0x5C) : accent;

        var panel = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock
        {
            Text = WindowTitle(snapshot.WindowKey),
            FontSize = 11.5,
            Foreground = BrushOf(0x66, 0x74, 0x87),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 3),
        });
        var percent = new TextBlock
        {
            Text = $"{remaining:0.#}%",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = display,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(percent, 1);
        line.Children.Add(percent);
        panel.Children.Add(line);

        var progress = new Grid { Height = 7, Margin = new Thickness(0, 8, 0, 0) };
        progress.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.001, remaining), GridUnitType.Star),
        });
        progress.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(0.001, 100 - remaining), GridUnitType.Star),
        });
        var track = new Border
        {
            Background = BrushOf(0xEC, 0xF0, 0xF5),
            CornerRadius = new CornerRadius(4),
        };
        Grid.SetColumnSpan(track, 2);
        progress.Children.Add(track);
        progress.Children.Add(new Border
        {
            Background = display,
            CornerRadius = new CornerRadius(4),
            MinWidth = remaining > 0 ? 3 : 0,
        });
        panel.Children.Add(progress);

        panel.Children.Add(new TextBlock
        {
            Text = ResetText(snapshot),
            FontSize = 10.5,
            Foreground = BrushOf(0x82, 0x8E, 0x9E),
            Margin = new Thickness(0, 7, 0, 0),
        });
        return panel;
    }

    private static string WindowTitle(string key) => key.ToLowerInvariant() switch
    {
        "primary" => "短时窗口剩余",
        "secondary" => "每周窗口剩余",
        _ => $"{key} 窗口剩余",
    };

    private static string ResetText(QuotaSnapshot snapshot)
    {
        if (snapshot.ResetsAtMs is not long resetMs)
        {
            return $"重置时间  暂未提供  ·  更新于 {RelativeTime(snapshot.CollectedAtMs)}";
        }

        var reset = DateTimeOffset.FromUnixTimeMilliseconds(resetMs).ToLocalTime();
        var when = reset.Date == DateTimeOffset.Now.Date
            ? $"今天 {reset:HH:mm}"
            : reset.Date == DateTimeOffset.Now.Date.AddDays(1)
                ? $"明天 {reset:HH:mm}"
                : $"{reset:MM月dd日 HH:mm}";
        return $"重置时间  {when}  ·  更新于 {RelativeTime(snapshot.CollectedAtMs)}";
    }

    private static string RelativeTime(long unixMs)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        var delta = DateTimeOffset.Now - time;
        return delta.TotalMinutes < 2 ? "刚刚"
            : delta.TotalHours < 1 ? $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前"
            : delta.TotalDays < 1 ? $"{(int)delta.TotalHours} 小时前"
            : $"{(int)delta.TotalDays} 天前";
    }

    private static Brush ParseBrush(string hex, Color fallback)
    {
        try
        {
            return new BrushConverter().ConvertFromString(hex) as Brush ?? new SolidColorBrush(fallback);
        }
        catch
        {
            return new SolidColorBrush(fallback);
        }
    }

    private static SolidColorBrush BrushOf(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshedText.Text = "正在刷新…";
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDeactivated(object sender, EventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
