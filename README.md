# Metrik Lite

一个轻量的 Windows 托盘配额监视器。当前读取 **Codex** 的剩余额度。

- 定时启动 `codex app-server`，通过 stdio JSON-RPC 读取实时配额
- 每个 Agent 显示一个清晰的数字托盘图标；详情面板显示百分比、进度条和重置时间
- 数字使用 Segoe UI Semibold，并按 16/24/32px 图标尺寸自动缩放、光学居中
- 左键打开详情，右键刷新、切换浅色图标、选择 Agent、设置开机自启或退出
- 读取失败时保留程序运行状态，并在日志中记录原因；

## 环境要求

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（仅构建需要）
- 已安装并登录 Codex CLI（`codex login`）

Codex 可执行文件按以下顺序查找：环境变量 `CODEX_BINARY`、PATH 中的 `codex.exe`/`codex.cmd`、`%APPDATA%\npm\codex.cmd`，以及 winget 包目录。

## 构建与运行

```powershell
dotnet build -c Release

# 托盘常驻
.\bin\Release\net8.0-windows\MetrikLite.exe

# 读取、分组和图标渲染自检，输出 report.txt 与 PNG 预览
.\bin\Release\net8.0-windows\MetrikLite.exe --smoke smoke-out

# 发布自包含单文件（目标机无需安装 .NET）
dotnet publish MetrikLite.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

也可以在 GitHub Actions 中推送 `v*` 标签，自动生成便携版和 Inno Setup 安装包。

## 使用

首次运行后，Windows 可能会把图标收进任务栏的 `^` 折叠区；将图标拖到任务栏即可固定。悬停图标可查看百分比和重置时间，左键打开详情面板，右键打开菜单。

刷新间隔可在 `%APPDATA%\MetrikLite\config.json` 中修改，字段为 `RefreshSeconds`，默认 30 秒，最低 10 秒。

## 工作原理与 Metrik 的关系

本项目独立实现公开的 Codex app-server JSON-RPC 协议：启动 `codex app-server`，发送 `initialize`、`initialized` 和 `account/rateLimits/read`，解析窗口剩余比例与重置时间，再渲染托盘数字。

项目设计受到 [Metrik](https://github.com/keros68/metrik) 的思路启发，如果需要更多功能，请看雨神的Metrik完整版雨神主页：https://github.com/keros68。

## 代码地图

```text
App.xaml(.cs)          入口、单实例和全局异常处理
TrayHost.cs            刷新调度、Agent 分组、托盘图标和菜单
CodexAppServer.cs      Codex CLI 定位、JSON-RPC 会话和响应解析
IconRenderer.cs        数字字体布局、抗锯齿渲染和 Icon 转换
DetailsWindow.xaml(.cs)详情面板和进度条
ConfigStore.cs         %APPDATA%\MetrikLite\config.json
Models.cs              QuotaSnapshot / AgentQuota
SmokeTest.cs           读取、分组、PNG 预览自检
installer/              Inno Setup 安装脚本
```

## 已知限制

- Windows 托盘图标本质上是位图，因此图标内只显示数字；百分号和重置信息在提示文字、详情面板中显示。
- 每次刷新都会启动一次 Codex 子进程，建议不要把刷新间隔设得过短。
- Codex CLI 未安装或未登录时不会产生配额图标，日志位于 `%APPDATA%\MetrikLite\metriklite.log`。

## License

[MIT](LICENSE) © 2026 Mors

