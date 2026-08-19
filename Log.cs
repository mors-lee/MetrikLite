// ============================================================================
// 【模块】Log.cs —— 极简文件日志
// ============================================================================
// 【职责】
//   把运行信息写入 %APPDATA%\MetrikLite\metriklite.log，便于排查“托盘没显示/
//   数据没刷新”这类问题。全项目统一入口：Log.Info() / Log.Error()。
//
// 【行为细节】
//   · 文件超过 256 KB 自动删除重来（避免无限增长，单用户场景足够）。
//   · 日志写入自身失败时静默吞掉——日志永远不能反过来弄崩托盘程序。
//
// 【依赖】无。
//
// 【修改指南】
//   · 想换日志位置/文件名：→ 改下方 Dir / File 两个静态字段。
//   · 想加日志级别（WARN/DEBUG）：→ 仿照 Info/Error 加方法，level 字符串传给 Write。
//   · 想接入正式日志库（如 Serilog）：→ 保持 Log.Info/Log.Error 签名不变，替换实现即可。
// ============================================================================

using System.IO;
using System.Text;

namespace MetrikLite;

public static class Log
{
    // 日志目录：%APPDATA%\MetrikLite\（与 config.json 同目录，集中管理）
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetrikLite");
    private static readonly string File = Path.Combine(Dir, "metriklite.log");

    /// <summary>常规信息：启动、刷新结果、退出等。每次定时刷新会写一行。</summary>
    public static void Info(string message) => Write("INFO ", message);

    /// <summary>错误信息：带异常类型与消息，便于定位。写日志失败不会抛出。</summary>
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    /// <summary>实际写文件的唯一出口；加锁保证多线程追加不串行损坏。</summary>
    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
                System.IO.File.AppendAllText(File, line, Encoding.UTF8);

                // 简单轮转：超过 256 KB 直接删除，下一条日志会自动重建文件
                var info = new FileInfo(File);
                if (info.Exists && info.Length > 256 * 1024)
                {
                    System.IO.File.Delete(File);
                }
            }
        }
        catch
        {
            // 日志失败不影响托盘运行（例如磁盘只读时静默放弃）
        }
    }
}
