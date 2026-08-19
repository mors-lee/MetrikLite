// ============================================================================
// 【模块】CodexAppServer.cs —— 直连 Codex 读取配额（独立数据源，不依赖 Metrik）
// ============================================================================
// 【职责】
//   启动本机 `codex app-server` 子进程，通过其 stdio 上的 JSON-RPC 协议
//   一次性询问账号配额（account/rateLimits/read），解析出 primary/secondary
//   两个窗口的剩余百分比，然后清理进程。方法完整复刻 Metrik 的
//   src-tauri/src/adapters/app_server.rs（该实现已在本机验证可用），
//   因此 MetrikLite 不再需要 Metrik 应用或其数据库——只要装了 Codex。
//
// 【协议时序】（newline 分隔的 JSON-RPC，一问一答）
//   ① 我方 → {"id":1,"method":"initialize","params":{clientInfo, capabilities}}
//   ② 对方 → {"id":1,"result":{...}}                （就绪）
//   ③ 我方 → {"method":"initialized"}                （通知，无 id）
//   ④ 我方 → {"id":3,"method":"account/rateLimits/read"}
//   ⑤ 对方 → {"id":3,"result":{ rateLimits:{ primary:{...}, secondary:{...} } }}
//   拿到 ⑤ 即断开杀进程。期间对方可能推送若干无 id 的通知，跳过即可。
//
// 【响应解析】（与 Metrik parse_rate_limits 一致）
//   · 优先 result.rateLimits（账号当前生效窗口的权威数据）；
//     为 null 时回退 result.rateLimitsByLimitId.codex（或蛇形命名变体）。
//     —— 两个来源百分比可能不一致，Metrik/CodexBar 都采用这个优先级。
//   · 每个窗口: usedPercent(0~100) → 剩余 = 100 - used（夹 0~100）；
//     resetsAt 是 Unix【秒】→ 转毫秒；windowDurationMins 决定窗口归类：
//       ≤1440 分钟（如 5 小时窗）→ "primary"；更长（如 10080 分钟周窗）→
//       "secondary"；缺时长 → 用槽位名兜底。
//     （primary/secondary 槽位≠窗口语义：prolite 套餐的 primary 槽装的就是周窗）
//
// 【codex 可执行文件的解析顺序】（ResolveCodexCommand）
//   1. 环境变量 CODEX_BINARY（用户显式指定，调试用）
//   2. 用户在托盘菜单中选择的独立 Codex CLI 路径
//   3. 官方 winget 包目录和 %APPDATA%\npm\codex.cmd
//   4. PATH 中的 codex.cmd / codex.bat / codex.exe（兼容其他安装方式）
//      —— PATH 中已知属于 Trae 的 ai-agent 启动器会跳过，避免加载
//         TRAE SOLO 的 aiep_vm.dll。
//   .cmd/.bat 必须经 cmd.exe /D /C 包装启动（Windows 批处理规则），
//   .exe 直接启动。ChatGPT 桌面版 WindowsApps 里的 codex.exe 受 MSIX
//   保护无法直接执行，故不列入。
//
// 【进程卫生】（重要，都是 Metrik 踩过的坑）
//   · stderr 必须持续排水：子进程警告写满 4KB 管道缓冲后会整体卡死
//     （本机实测：arg0 清理警告即走 stderr）。
//   · stdout 用专职线程逐行读（ReadLineAsync 不能并发重入）。
//   · 结束时 taskkill /PID x /T /F 杀整棵进程树（app-server 可能有孙进程）。
//   · CreateNoWindow：绝不弹出控制台窗口。
//
// 【修改指南】
//   · 协议字段变化（新版 codex 改了响应结构）：→ ParseRateLimits()。
//   · 超时太短/太长：→ ReadAsync 的 timeout 参数（调用方 TrayHost 传入）。
//   · 想支持新的安装方式：→ ResolveCodexCommand() 加候选。
//   · 一切失败都返回空列表 + Log.Error（含解析到的 exe 路径），
//     托盘保持上次数据，绝不抛异常。
// ============================================================================

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MetrikLite;

public static class CodexAppServer
{
    private static readonly string[] KnownUnrelatedPathMarkers =
    {
        @"\trae solo\",
        @"\trae solo cn\",
        @"\modules\ai-agent\",
    };

    // ---------------------------------------------------------------
    // codex 可执行文件解析
    // ---------------------------------------------------------------

    /// <summary>找到 codex 可执行文件并给出启动方式。找不到返回 null（错误已记日志）。</summary>
    /// <returns>(FileName, Arguments前缀, 是否需要cmd包装)——调用方再补 "app-server"。</returns>
    private static (string FileName, string Args)? ResolveCodexCommand(string? configuredPath)
    {
        // ① 显式环境变量
        var explicitPath = NormalizePath(Environment.GetEnvironmentVariable("CODEX_BINARY"));
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return WrapIfNeeded(explicitPath);
        }

        // ② 托盘菜单选择的独立 CLI。路径保存在 %APPDATA%\MetrikLite\config.json。
        var selectedPath = NormalizePath(configuredPath);
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            if (IsProtectedWindowsAppsPath(selectedPath))
            {
                Log.Error($"configured Codex path is protected WindowsApps: {selectedPath}");
            }
            else if (File.Exists(selectedPath))
            {
                return WrapIfNeeded(selectedPath);
            }
            else
            {
                Log.Error($"configured Codex binary not found: {selectedPath}");
            }
        }

        // ③ 官方 winget 包目录直查。不同版本的 Codex CLI 可能把 shim 或
        // 原生文件放在包根目录或版本子目录中，因此两种布局都兼容。
        try
        {
            var pkgRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");
            var packageDirs = Directory.Exists(pkgRoot)
                ? Directory.EnumerateDirectories(pkgRoot, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();

            foreach (var packageDir in packageDirs)
            {
                foreach (var relative in new[]
                {
                    "codex.cmd",
                    "codex.bat",
                    "codex.exe",
                    "codex-x86_64-pc-windows-msvc.exe",
                })
                {
                    var candidate = Path.Combine(packageDir, relative);
                    if (File.Exists(candidate))
                    {
                        return WrapIfNeeded(candidate);
                    }
                }
            }
        }
        catch
        {
            // WinGet 目录不存在或无权读取时，继续检查 npm/PATH。
        }

        // ④ npm 全局 shim（官方 Codex CLI 的常见安装位置）。必须放在 PATH
        // 前面，因为其他软件可能把同名 codex.exe 加入 PATH。
        var npm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
        if (File.Exists(npm))
        {
            return WrapIfNeeded(npm);
        }

        // ⑤ 扫描 PATH（进程环境里的 Path 是系统+用户合并结果）。
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var ext in new[] { "codex.cmd", "codex.bat", "codex.exe" })
        {
            foreach (var dir in pathDirs)
            {
                try
                {
                    var candidate = Path.Combine(dir, ext);
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    if (IsProtectedWindowsAppsPath(candidate))
                    {
                        Log.Info($"skip protected Codex path: {candidate}");
                        continue;
                    }

                    if (IsKnownUnrelatedPath(candidate))
                    {
                        Log.Info($"skip unrelated codex launcher: {candidate}");
                        continue;
                    }

                    return WrapIfNeeded(candidate);
                }
                catch
                {
                    // 非法路径字符等：跳过该目录继续找
                }
            }
        }

        Log.Error("codex binary not found (tried CODEX_BINARY, configured path, official WinGet package, npm, PATH)");
        return null;
    }

    private static string? NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"');

    /// <summary>.cmd/.bat 用 cmd.exe /D /C 包装（引号包路径防空格）；.exe 直接执行。</summary>
    private static (string FileName, string Args) WrapIfNeeded(string script)
        => script.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
           script.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? (Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\cmd.exe"), $"/D /C \"{script}\"")
            : (script, "");

    private static bool IsProtectedWindowsAppsPath(string path)
        => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownUnrelatedPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        return KnownUnrelatedPathMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------
    // 配额读取主流程
    // ---------------------------------------------------------------

    /// <summary>
    /// 启动 codex app-server → 走一遍 JSON-RPC → 解析配额 → 杀进程。
    /// 失败返回空列表（错误已记日志），绝不抛出。
    /// </summary>
    /// <param name="timeout">整个会话的硬超时（含进程冷启动）。</param>
    public static async Task<IReadOnlyList<QuotaSnapshot>> ReadAsync(
        TimeSpan timeout, string? configuredPath = null)
    {
        var resolved = ResolveCodexCommand(configuredPath);
        if (resolved == null)
        {
            return Array.Empty<QuotaSnapshot>();
        }
        var (fileName, argsPrefix) = resolved.Value;
        Log.Info($"codex app-server: {fileName} {argsPrefix} app-server");

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"{argsPrefix} app-server".Trim(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,   // 必须重定向并持续排水，否则子进程会卡死
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            Log.Info($"codex app-server started, pid={proc.Id}");
            using var cts = new CancellationTokenSource(timeout);

            // stderr 排水：丢弃内容，只保证管道不堵塞
            _ = proc.StandardError.ReadToEndAsync(cts.Token).ContinueWith(_ => { });

            // stdout 专职读线程：逐行塞进 channel（ReadLineAsync 不可重入，
            // 必须由单一消费者顺序读——专职线程是最简单的正确写法）
            var lines = Channel.CreateUnbounded<string>();
            var pump = Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var line = await proc.StandardOutput.ReadLineAsync(cts.Token);
                        if (line == null)
                        {
                            break; // EOF：进程退出
                        }
                        await lines.Writer.WriteAsync(line, cts.Token);
                    }
                }
                catch
                {
                    // 超时/取消：读循环自然结束
                }
                finally
                {
                    lines.Writer.TryComplete();
                }
            }, cts.Token);

            var result = await ExchangeAsync(proc, lines.Reader, cts.Token);
            return ParseRateLimits(result);
        }
        catch (Exception ex)
        {
            Log.Error("codex app-server session failed", ex);
            return Array.Empty<QuotaSnapshot>();
        }
        finally
        {
            KillTree(proc);
        }
    }

    /// <summary>执行“初始化 → rateLimits 问答”的协议状态机，返回 id=3 的 result 节点。</summary>
    private static async Task<JsonElement> ExchangeAsync(
        Process proc, ChannelReader<string> reader, CancellationToken ct)
    {
        await WriteLineAsync(proc,
            """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"metriklite","title":"MetrikLite","version":"1.0.0"},"capabilities":{"experimentalApi":true,"optOutNotificationMethods":[]}}}""");

        var asked = false;
        await foreach (var line in reader.ReadAllAsync(ct))
        {
            JsonElement value;
            try
            {
                value = JsonSerializer.Deserialize<JsonElement>(line);
            }
            catch (JsonException)
            {
                continue; // 非 JSON 行（横幅/杂音）：跳过
            }

            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var idProp))
            {
                continue; // 无 id 的通知：跳过
            }

            if (!asked && idProp.GetInt64() == 1)
            {
                Log.Info("codex app-server: initialize ack received");
                // initialize 应答到达 → 发通知 + 正式请求
                await WriteLineAsync(proc, """{"method":"initialized"}""");
                await WriteLineAsync(proc, """{"id":3,"method":"account/rateLimits/read"}""");
                Log.Info("codex app-server: rateLimits request sent");
                asked = true;
            }
            else if (idProp.GetInt64() == 3)
            {
                if (value.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
                {
                    Log.Info("codex app-server: rateLimits result received");
                    return result;
                }
                // id=3 但 result 缺失/为 null（如未登录）：向前走会超时，
                // 直接抛出让上层给出明确错误
                throw new InvalidOperationException(
                    "account/rateLimits/read returned no result (codex 未登录?)");
            }
        }
        throw new TimeoutException("codex app-server closed stdout before answering");
    }

    private static async Task WriteLineAsync(Process proc, string json)
    {
        await proc.StandardInput.WriteLineAsync(json);
        await proc.StandardInput.FlushAsync();
    }

    /// <summary>解析 rateLimits 响应为快照列表（逻辑对照 Metrik parse_rate_limits）。</summary>
    internal static IReadOnlyList<QuotaSnapshot> ParseRateLimits(JsonElement result)
    {
        // 优先级：rateLimits → rateLimitsByLimitId.codex → rate_limits_by_limit_id.codex
        JsonElement limits;
        if (result.TryGetProperty("rateLimits", out var primary) && primary.ValueKind != JsonValueKind.Null)
        {
            limits = primary;
        }
        else if ((result.TryGetProperty("rateLimitsByLimitId", out var byId) ||
                  result.TryGetProperty("rate_limits_by_limit_id", out byId)) &&
                 byId.TryGetProperty("codex", out var codex) && codex.ValueKind != JsonValueKind.Null)
        {
            limits = codex;
        }
        else
        {
            return Array.Empty<QuotaSnapshot>();
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var samples = new List<QuotaSnapshot>();
        foreach (var slot in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(slot, out var window) || window.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            if (!window.TryGetProperty("usedPercent", out var usedProp) ||
                !usedProp.TryGetDouble(out var used))
            {
                continue;
            }

            long? resetsAtMs = null;
            if (window.TryGetProperty("resetsAt", out var resetProp) && resetProp.TryGetInt64(out var secs))
            {
                resetsAtMs = secs * 1000; // 协议给的是秒，统一转毫秒
            }

            var minutes = window.TryGetProperty("windowDurationMins", out var durProp) &&
                          durProp.TryGetInt64(out var m) ? m : (long?)null;

            samples.Add(new QuotaSnapshot(
                AdapterId: "codex",
                WindowKey: WindowKey(minutes, slot),
                RemainingPercent: Math.Clamp(100.0 - used, 0.0, 100.0),
                ResetsAtMs: resetsAtMs,
                CollectedAtMs: now,
                Quality: "official_live",
                SourceLabel: "Codex app-server"));
        }
        return samples;

        // 窗口按时长归类（Metrik codex_window_key）：槽位只是位置，
        // ≤1440 分钟算短窗(primary)，更长算周窗(secondary)
        static string WindowKey(long? windowMinutes, string slot) => windowMinutes switch
        {
            <= 1440 => "primary",
            > 1440 => "secondary",
            _ => slot,
        };
    }

    /// <summary>杀整棵进程树（app-server 可能有孙进程）。taskkill 失败也兜底 Process.Kill。</summary>
    private static void KillTree(Process? proc)
    {
        if (proc == null)
        {
            return;
        }
        try
        {
            if (!proc.HasExited)
            {
                var kill = new ProcessStartInfo
                {
                    FileName = Environment.ExpandEnvironmentVariables("%SystemRoot%\\System32\\taskkill.exe"),
                    Arguments = $"/PID {proc.Id} /T /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var k = Process.Start(kill);
                k?.WaitForExit(5000);
            }
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        }
        finally
        {
            proc.Dispose();
        }
    }
}
