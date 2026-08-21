# MetrikLite

<p align="center">
  <img src="MetrikLite-icon-v2.png" width="112" alt="MetrikLite ML 图标" />
</p>

<p align="center">
  <strong>在 Windows 托盘直接查看 Codex 每周剩余配额与重置时间。</strong><br />
  轻量、原生、本地优先，不读取对话和项目文件。
</p>

<p align="center">
  <a href="https://github.com/mors-lee/MetrikLite/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/mors-lee/MetrikLite?color=4E82E8&label=Release"></a>
  <a href="https://github.com/mors-lee/MetrikLite/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/mors-lee/MetrikLite/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/mors-lee/MetrikLite/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/mors-lee/MetrikLite/actions/workflows/codeql.yml/badge.svg"></a>
  <img alt="Windows" src="https://img.shields.io/badge/Windows_10%2F11-x64-0078D4?logo=windows11&logoColor=white">
  <a href="./LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/License-MIT-2ea44f"></a>
</p>

<p align="center">
  <a href="./README.en.md">English</a> ·
  <a href="https://github.com/mors-lee/MetrikLite/releases/latest">下载最新版</a> ·
  <a href="https://github.com/mors-lee/MetrikLite/issues">问题反馈</a> ·
  <a href="./SECURITY.md">安全策略</a>
</p>

## 一眼了解

MetrikLite 将 Codex **每周剩余百分比**绘制成 Windows 通知区域数字图标。无需打开窗口，就能看到当前余量；将鼠标停在数字上，还会显示重置时间。

- **托盘直接读数**：`8`、`28`、`100` 自动缩放并光学居中。
- **悬停查看重置**：提示分两行显示剩余百分比与重置日期。
- **紧凑周配额面板**：左键点击托盘数字，在当前显示器右下角展开。
- **适配多显示器和高 DPI**：面板锚定工作区，不遮挡任务栏，圆角完整。
- **本地优先**：复用本机 Codex app-server，不建立 MetrikLite 云端账户。
- **体积小**：Rust + Tauri 2，正式 NSIS 安装包约 1.3 MB。
- **常用功能齐全**：开机自启、手动刷新、CLI 路径选择、更新检查。

> v2.0.1 起，主界面和托盘数字只使用 Codex 每周窗口，不再显示短时窗口。

## 下载

### 推荐：安装向导

[下载 MetrikLite v2.0.1 安装包](https://github.com/mors-lee/MetrikLite/releases/download/v2.0.1/MetrikLite_2.0.1_x64-setup.exe)

也可以前往 [Latest Release](https://github.com/mors-lee/MetrikLite/releases/latest) 获取当前最新版：

| 文件 | 用途 |
| --- | --- |
| `MetrikLite_*_x64-setup.exe` | 推荐，当前用户 NSIS 安装向导 |
| `MetrikLite_*_x64_en-US.msi` | MSI 部署或企业软件分发 |
| `MetrikLite-portable.exe` | 免安装便携版 |
| `SHA256SUMS.txt` | Release 附件 SHA256 校验值 |
| `MetrikLite-SBOM.spdx.json` | SPDX JSON 软件物料清单 |

系统要求：Windows 10/11 x64。若系统没有 Microsoft Edge WebView2 Runtime，安装器会使用微软官方引导程序安装；Windows 11 和多数 Windows 10 设备已经预装。

## 安装与首次使用

1. 从本仓库 Releases 下载 `MetrikLite_*_x64-setup.exe`。
2. 运行安装向导并启动 MetrikLite。
3. 如果已经安装并登录独立 Codex CLI，托盘会显示每周剩余百分比。
4. 左键点击托盘数字查看详情；右键打开功能菜单。
5. 如果未自动识别 Codex，在“设置”中选择 `codex.exe`、`codex.cmd` 或 `codex.bat`。

### Windows 显示“未知发布者”

当前公开版本尚未配置 Authenticode 商业代码签名证书，因此 SmartScreen 可能提示“未知发布者”。这不等同于检测到病毒，但也不应被忽略：

1. 只从 `github.com/mors-lee/MetrikLite/releases` 下载。
2. 使用 Release 中的 `SHA256SUMS.txt` 核对文件哈希。
3. 可结合 Windows Defender、VirusTotal、源码审查和 GitHub Actions 构建记录判断。

任何单一信号——包括代码签名或 VirusTotal `0/70`——都不能证明软件绝对安全。

## 使用说明

### 托盘图标

| 显示 | 含义 |
| --- | --- |
| `0`–`100` | Codex 每周剩余百分比 |
| 红色数字 | 剩余配额不高于 20% |
| `!` | 暂时无法读取 Codex 配额 |

- **鼠标悬停**：显示“Codex 剩余 xx%”和“重置 MM-DD HH:MM”。
- **左键点击**：显示或隐藏紧凑配额面板。
- **右键点击**：显示配额、立即刷新、设置、检查更新、下载页面和退出。

### 刷新与配置

默认每 30 秒刷新一次，可在 10–300 秒之间调整。配置和日志位于：

```text
%APPDATA%\MetrikLite\config.json
%APPDATA%\MetrikLite\metriklite.log
%LOCALAPPDATA%\MetrikLite\Runtime
```

开机自启仅写入当前用户的：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MetrikLite
```

## 没有安装 Codex 会怎样

MetrikLite 不会崩溃或消失，而是保留 `!` 托盘图标和可操作的设置入口。安装并登录独立 Codex CLI 后，点击“立即刷新”即可。

Codex CLI 自动查找顺序：

1. `CODEX_BINARY` 环境变量。
2. MetrikLite 设置中保存的路径。
3. 官方 WinGet `OpenAI.Codex_*` 包中的原生 Windows EXE。
4. `%APPDATA%\npm\codex.cmd`。
5. `PATH` 中的 Codex CLI。

受保护的 `WindowsApps` 内置路径和已知同名第三方启动器会被跳过，避免启动失败或误用其他程序。

## 隐私与联网范围

### 会读取

- MetrikLite 自己的本地配置和日志目录。
- 用户指定或自动找到的 Codex CLI 文件路径。
- Codex app-server 返回的每周配额百分比、窗口时长和重置时间。
- Windows 字体目录中的 Segoe UI Semibold/Bold，仅用于渲染托盘数字。

### 不会读取

- Codex 对话、提示词、回答或聊天历史。
- 项目源码、Git 仓库内容、文档或剪贴板。
- GitHub 密码、验证码、Token。
- 浏览器 Cookie、历史记录或保存的密码。
- Codex 凭据文件内容。

MetrikLite 不读取敏感凭据，因此不需要“允许读取凭据”的开关。Codex 登录完全由 Codex CLI 自己负责。

### MetrikLite 自身请求的域名

| 域名 | 何时请求 | 用途 |
| --- | --- | --- |
| `api.github.com` | 用户点击“检查更新” | 获取公开 Release 版本信息 |
| `github.com` | 用户主动打开下载页面 | 由默认浏览器显示 Release |

Codex CLI 为获取账户配额而访问的 OpenAI 服务属于 Codex 自身通信，不经过 MetrikLite 服务器。MetrikLite 没有自己的云服务、分析服务或广告 SDK。

## 工作原理

```text
Windows 托盘数字 / 紧凑面板
              │
              │ 每 10–300 秒或手动刷新
              ▼
      Rust Codex 会话管理器
              │ initialize（一次）
              │ account/rateLimits/read
              ▼
      本机 codex app-server
              │ JSON-RPC stdout
              ▼
       每周配额与重置时间
```

CLI 路径不变时，MetrikLite 复用同一个 app-server 进程。退出时先关闭标准输入并等待正常结束，只有超时才终止直接子进程。

## 从源码构建

要求：Windows 10/11 x64、Rust stable MSVC、Visual Studio 2022 Build Tools（Desktop development with C++）、Node.js 22+ 和 WebView2 Runtime。

```powershell
npm ci
npm run tauri build
```

开发检查：

```powershell
npm audit --audit-level=high
node --check src\main.js
cargo fmt --manifest-path src-tauri\Cargo.toml --all -- --check
cargo test --manifest-path src-tauri\Cargo.toml --locked
cargo clippy --manifest-path src-tauri\Cargo.toml --locked --all-targets -- -D warnings
```

## 自动构建与供应链

正式 Release 全部由 GitHub Actions 从公开标签自动构建，不接受本地手工编译后上传：

1. 校验 Git 标签与应用版本一致。
2. 构建 NSIS、MSI 和便携 EXE。
3. 生成 SPDX JSON SBOM。
4. 生成 `SHA256SUMS.txt`。
5. 上传到公开 GitHub Release。

仓库还启用了 Dependabot、CodeQL、`cargo audit` 和 `npm audit`。漏洞报告方式参见 [SECURITY.md](SECURITY.md)。

## 项目结构

```text
src/                         无框架 HTML/CSS/JavaScript 面板
src-tauri/src/main.rs        Tauri 生命周期、托盘和前端命令
src-tauri/src/codex.rs       持久 Codex JSON-RPC 会话
src-tauri/src/models.rs      配额状态和每周托盘选择逻辑
src-tauri/src/tray_icon.rs   数字渲染、缩放与面板定位
src-tauri/src/config.rs      配置和当前用户开机自启
src-tauri/tauri.conf.json    窗口及 NSIS/MSI 打包配置
.github/workflows/           CI、CodeQL 和 Release
```

## 许可证与声明

MetrikLite 使用 [MIT License](LICENSE)。Rust/Tauri v2 版本是针对本项目需求的独立实现，没有复制 `keros68/metrik` 的 AGPL 源码。

配额托盘思路受到 [Metrik](https://github.com/keros68/metrik) 启发。MetrikLite 不是 OpenAI 官方产品，也不代表 OpenAI。
