// ============================================================================
// SmokeTest.cs —— 无界面自检：读取 Codex、分组并输出数字图标预览
// ============================================================================

using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MetrikLite;

public static class SmokeTest
{
    /// <summary>执行冒烟测试并写报告。返回 0=成功、1=异常。</summary>
    public static int Run(string outDir)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var report = new StringBuilder();
            report.AppendLine($"MetrikLite smoke test @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine();

            var snapshots = CodexAppServer.ReadAsync(TimeSpan.FromSeconds(30))
                .GetAwaiter().GetResult();
            report.AppendLine($"[Codex] app-server snapshots = {snapshots.Count}");
            foreach (var snapshot in snapshots)
            {
                var reset = snapshot.ResetsAtMs is long resetMs
                    ? DateTimeOffset.FromUnixTimeMilliseconds(resetMs).ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    : "--";
                var collected = DateTimeOffset.FromUnixTimeMilliseconds(snapshot.CollectedAtMs)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                report.AppendLine(
                    $"  {snapshot.AdapterId,-12} {snapshot.WindowKey,-14} " +
                    $"remaining={snapshot.RemainingPercent,5:0.0}%  resets={reset} " +
                    $"collected={collected} quality={snapshot.Quality} source={snapshot.SourceLabel}");
            }
            report.AppendLine();

            var agents = TrayHost.GroupAgents(snapshots);
            report.AppendLine($"[分组] agents = {agents.Count} (每个 Agent 选择当前最紧窗口)");
            foreach (var agent in agents)
            {
                report.AppendLine(
                    $"  {agent.AdapterId,-12} display={agent.DisplayName,-14} " +
                    $"primary={agent.Primary.WindowKey} " +
                    $"{agent.Primary.RemainingPercent:0.#}% color={agent.BrandHex}");
            }
            report.AppendLine();

            report.AppendLine("[图标] rendered PNG previews (24px 与 32px):");
            foreach (var agent in agents)
            {
                var percent = (int)Math.Round(agent.Primary.RemainingPercent);
                var color = Parse(agent.BrandHex);
                SavePng(outDir, $"agent-{agent.AdapterId}-24.png",
                    IconRenderer.RenderPercent(percent, color, false, 24));
                SavePng(outDir, $"agent-{agent.AdapterId}-32.png",
                    IconRenderer.RenderPercent(percent, color, false, 32));
                report.AppendLine($"  agent-{agent.AdapterId}-24/32.png ({percent}%)");
            }

            // 固定覆盖容易暴露基线、裁切与缩放问题的数字，避免只测试当前配额值。
            foreach (var sample in new[] { 8, 10, 100 })
            {
                foreach (var size in new[] { 16, 24, 32 })
                {
                    SavePng(outDir, $"type-{sample}-{size}.png",
                        IconRenderer.RenderPercent(sample, Color.FromRgb(0xE4, 0x54, 0x5C), false, size));
                }
            }
            report.AppendLine("  typography samples: 8 / 10 / 100 at 16 / 24 / 32px");

            var reportPath = Path.Combine(outDir, "report.txt");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(true));
            Console.WriteLine(report.ToString());
            Console.WriteLine($"smoke report written to {Path.GetFullPath(reportPath)}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"smoke test failed: {ex}");
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(
                    Path.Combine(outDir, "report.txt"),
                    $"SMOKE FAILED @ {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{ex}",
                    new UTF8Encoding(true));
            }
            catch
            {
                // 输出目录不可写时，保留控制台异常即可。
            }
            return 1;
        }
    }

    private static Color Parse(string hex)
        => (Color)ColorConverter.ConvertFromString(hex);

    private static void SavePng(string dir, string name, RenderTargetBitmap bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(dir, name));
        encoder.Save(stream);
    }
}
