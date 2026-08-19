// ============================================================================
// 【模块】App.xaml.cs —— 程序入口（单实例 + 命令行分发 + 异常兜底）
// ============================================================================
// 【职责】
//   1. 命令行分发：--smoke 参数 → 跑冒烟测试（不进托盘，输出报告后退出），
//      用于无界面验证“数据读取 + 图标渲染”是否正常。
//   2. 单实例互斥：已有实例运行时直接退出（防止重复托盘图标）。
//   3. 正常路径：创建 TrayHost 并启动；全局未处理异常记日志不闪退。
//
// 【依赖】所有模块（入口性质）。
//
// 【修改指南】
//   · 想加新的命令行模式（如 --version）：→ 仿照 --smoke 分支加判断。
//   · 单实例键名：→ "MetrikLite_SingleInstance"（改了之后旧实例不会互斥）。
//   · 注意 ShutdownMode=OnExplicitShutdown 在 App.xaml 里——窗口全部关闭
//     程序也不退出，必须显式 Shutdown()（托盘程序的标准配置）。
// ============================================================================

using System.Windows;
using Application = System.Windows.Application;

namespace MetrikLite;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private TrayHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;

        // —— 冒烟测试模式：MetrikLite.exe --smoke <输出目录> ——
        // 必须放 Task.Run：ReadAsync 内部有 await，若在 Dispatcher 线程上同步等待，
        // 续体会排队回 Dispatcher（正被占用）→ 经典死锁（进程挂起不退出）。
        // 线程池上无同步上下文，await 正常续行；完成后回 Dispatcher 收尾关进程。
        if (args.Length > 0 && args[0] == "--smoke")
        {
            var outDir = args.Length > 1 ? args[1] : "smoke-out";
            _ = Task.Run(() =>
            {
                var code = SmokeTest.Run(outDir);
                Dispatcher.BeginInvoke(() =>
                {
                    Environment.ExitCode = code;
                    Shutdown();
                });
            });
            return;
        }

        // —— 单实例：第二个实例启动时安静退出 ——
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "MetrikLite_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Info("another instance is already running, exiting");
            Shutdown();
            return;
        }

        // UI 线程未处理异常：记日志并吞掉（托盘常驻程序不能因偶发异常消失）
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("unhandled UI exception", ex.Exception);
            ex.Handled = true;
        };

        // —— 正常启动：托盘宿主 ——
        _host = new TrayHost();
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
