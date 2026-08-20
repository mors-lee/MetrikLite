// ============================================================================
// 【模块】CodexAppServer.cs —— 直连 Codex 读取配额（独立数据源，不依赖 Metrik）
// ============================================================================
// 【职责】
//   启动本机 `codex app-server` 子进程，通过其 stdio 上的 JSON-RPC 协议
//   询问账号配额（account/rateLimits/read），解析出 primary/secondary
//   两个窗口的剩余百分比。进程作为长连接复用，避免频繁启动/强杀 Codex
//   及其 Git 子进程。协议解析参考 Metrik 的
//   src-tauri/src/adapters/app_server.rs（该实现已在本机验证可用），
//   因此 MetrikLite 不再需要 Metrik 应用或其数据库——只要装了 Codex。
//
// 【协议时序】（newline 分隔的 JSON-RPC，一问一答）
//   ① 我方 → {"id":1,"method":"initialize","params":{clientInfo, capabilities}}
//   ② 对方 → {"id":1,"result":{...}}                （就绪）
//   ③ 我方 → {"method":"initialized"}                （通知，无 id）
//   ④ 我方 → {"id":3,"method":"account/rateLimits/read"}
//   ⑤ 对方 → {"id":3,"result":{ rateLimits:{ primary:{...}, secondary:{...} } }}
//   后续刷新只重复 ④⑤。期间对方可能推送若干无 id 的通知，跳过即可。
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
//   · app-server 在刷新之间保持运行；退出时先关闭 stdin 等待正常结束，
//     仅在超时后使用 Process.Kill(entireProcessTree: true) 兜底。
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
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static Process? _sessionProcess;
    private static Channel<string>? _sessionLines;
    private static CancellationTokenSource? _sessionLifetime;
    private static Task? _stdoutPump;
    private static Task? _stderrPump;
    private static string? _sessionCommandKey;
    private static long _nextRequestId = 1;

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
    /// 复用 codex app-server 长连接读取配额。进程不存在、已退出或 CLI 路径变化时
    /// 才重新启动；失败返回空列表（错误已记日志），绝不向 UI 抛出。
    /// </summary>
    /// <param name="timeout">整个会话的硬超时（含进程冷启动）。</param>
    public static async Task<IReadOnlyList<QuotaSnapshot>> ReadAsync(
        TimeSpan timeout, string? configuredPath = null)
    {
        await SessionGate.WaitAsync();
        try
        {
            var resolved = ResolveCodexCommand(configuredPath);
            if (resolved == null)
            {
                await StopSessionAsync();
                return Array.Empty<QuotaSnapshot>();
            }

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await EnsureSessionAsync(resolved.Value, cts.Token);
                var proc = _sessionProcess
                    ?? throw new InvalidOperationException("Codex app-server session is unavailable");
                var requestId = Interlocked.Increment(ref _nextRequestId);
                await WriteLineAsync(proc,
                    $"{{\"id\":{requestId},\"method\":\"account/rateLimits/read\"}}",
                    cts.Token);
                Log.Info($"codex app-server: rateLimits request sent, id={requestId}");
                var result = await WaitForResultAsync(requestId, cts.Token);
                Log.Info($"codex app-server: rateLimits result received, id={requestId}");
                return ParseRateLimits(result);
            }
            catch (Exception ex)
            {
                Log.Error("codex app-server session failed", ex);
                await StopSessionAsync();
                return Array.Empty<QuotaSnapshot>();
            }
        }
        finally
        {
            SessionGate.Release();
        }
    }

    /// <summary>应用退出时正常关闭长连接，避免 taskkill 打断正在初始化的 Git 子进程。</summary>
    public static async Task ShutdownAsync()
    {
        await SessionGate.WaitAsync();
        try
        {
            await StopSessionAsync();
        }
        finally
        {
            SessionGate.Release();
        }
    }

    private static async Task EnsureSessionAsync(
        (string FileName, string Args) resolved,
        CancellationToken ct)
    {
        var commandKey = $"{resolved.FileName}\n{resolved.Args}";
        if (_sessionProcess is { HasExited: false } &&
            string.Equals(_sessionCommandKey, commandKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await StopSessionAsync();

        var workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MetrikLite", "Runtime");
        Directory.CreateDirectory(workingDirectory);
        var psi = new ProcessStartInfo
        {
            FileName = resolved.FileName,
            Arguments = $"{resolved.Args} app-server".Trim(),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");
        var lifetime = new CancellationTokenSource();
        var lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        _sessionProcess = proc;
        _sessionLines = lines;
        _sessionLifetime = lifetime;
        _sessionCommandKey = commandKey;
        _nextRequestId = 1;
        _stdoutPump = PumpStdoutAsync(proc, lines.Writer, lifetime.Token);
        _stderrPump = DrainStderrAsync(proc, lifetime.Token);
        Log.Info($"codex app-server started for reusable session, pid={proc.Id}");

        await WriteLineAsync(proc,
            """{"id":1,"method":"initialize","params":{"clientInfo":{"name":"metriklite","title":"MetrikLite","version":"1.1.2"},"capabilities":{"experimentalApi":true,"optOutNotificationMethods":[]}}}""",
            ct);
        _ = await WaitForResultAsync(1, ct);
        await WriteLineAsync(proc, """{"method":"initialized"}""", ct);
        Log.Info("codex app-server reusable session initialized");
    }

    private static async Task PumpStdoutAsync(
        Process proc,
        ChannelWriter<string> writer,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }
                await writer.WriteAsync(line, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            Log.Error("codex app-server stdout pump failed", ex);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task DrainStderrAsync(Process proc, CancellationToken ct)
    {
        try
        {
            _ = await proc.StandardError.ReadToEndAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 正常关闭。
        }
        catch (Exception ex)
        {
            Log.Error("codex app-server stderr pump failed", ex);
        }
    }

    private static async Task<JsonElement> WaitForResultAsync(long expectedId, CancellationToken ct)
    {
        var reader = _sessionLines?.Reader
            ?? throw new InvalidOperationException("Codex app-server output channel is unavailable");
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

            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("id", out var idProp) ||
                !idProp.TryGetInt64(out var responseId) ||
                responseId != expectedId)
            {
                continue;
            }

            if (value.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidOperationException(
                    $"Codex app-server request {expectedId} failed: {error.GetRawText()}");
            }
            if (value.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null)
            {
                return result.Clone();
            }
            throw new InvalidOperationException(
                $"Codex app-server request {expectedId} returned no result (codex 未登录?)");
        }
        throw new EndOfStreamException("Codex app-server closed stdout before answering");
    }

    private static async Task WriteLineAsync(
        Process proc,
        string json,
        CancellationToken ct)
    {
        await proc.StandardInput.WriteLineAsync(json);
        await proc.StandardInput.FlushAsync(ct);
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

    /// <summary>
    /// 先关闭 stdin，让 app-server 按协议流结束；两秒内仍不退出时才强制清理。
    /// 避免旧实现每次刷新都 taskkill /T /F，打断正在初始化的 git.exe。
    /// </summary>
    private static async Task StopSessionAsync()
    {
        var proc = _sessionProcess;
        var lifetime = _sessionLifetime;
        var stdoutPump = _stdoutPump;
        var stderrPump = _stderrPump;
        _sessionProcess = null;
        _sessionLines = null;
        _sessionLifetime = null;
        _stdoutPump = null;
        _stderrPump = null;
        _sessionCommandKey = null;

        if (proc == null)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.StandardInput.Close();
                    using var gracefulTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await proc.WaitForExitAsync(gracefulTimeout.Token);
                    Log.Info($"codex app-server exited gracefully, pid={proc.Id}");
                }
                catch (OperationCanceledException)
                {
                    Log.Info($"codex app-server graceful shutdown timed out; terminating pid={proc.Id}");
                    proc.Kill(entireProcessTree: true);
                    await proc.WaitForExitAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("failed to stop codex app-server", ex);
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // 已退出或句柄失效。
            }
        }
        finally
        {
            lifetime?.Cancel();
            var pumps = new[] { stdoutPump, stderrPump }.Where(t => t != null).Cast<Task>().ToArray();
            if (pumps.Length > 0)
            {
                try
                {
                    await Task.WhenAny(Task.WhenAll(pumps), Task.Delay(500));
                }
                catch
                {
                    // 排水任务只负责结束管道，不阻碍退出。
                }
            }
            lifetime?.Dispose();
            proc.Dispose();
        }
    }
}
