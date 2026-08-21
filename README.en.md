# MetrikLite

<p align="center">
  <img src="MetrikLite-icon-v2.png" width="112" alt="MetrikLite ML icon" />
</p>

<p align="center">
  <strong>See your Codex weekly quota and reset time directly in the Windows tray.</strong><br />
  Lightweight, native, and local-first. It does not read conversations or project files.
</p>

<p align="center">
  <a href="https://github.com/mors-lee/MetrikLite/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/mors-lee/MetrikLite?color=4E82E8&label=Release"></a>
  <a href="https://github.com/mors-lee/MetrikLite/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/mors-lee/MetrikLite/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/mors-lee/MetrikLite/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/mors-lee/MetrikLite/actions/workflows/codeql.yml/badge.svg"></a>
  <img alt="Windows" src="https://img.shields.io/badge/Windows_10%2F11-x64-0078D4?logo=windows11&logoColor=white">
  <a href="./LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/License-MIT-2ea44f"></a>
</p>

<p align="center">
  <a href="./README.md">中文</a> ·
  <a href="https://github.com/mors-lee/MetrikLite/releases/latest">Latest release</a> ·
  <a href="https://github.com/mors-lee/MetrikLite/issues">Issues</a> ·
  <a href="./SECURITY.md">Security</a>
</p>

## At a glance

MetrikLite renders the Codex **weekly remaining percentage** as a numeric Windows notification-area icon. Hover over the number to see its reset time, or left-click for a compact panel above the taskbar.

- Auto-scaled, optically centered values from `0` to `100`.
- Two-line hover text with remaining percentage and reset date.
- Compact weekly-quota panel anchored to the active monitor work area.
- Multi-monitor and high-DPI aware positioning with complete rounded corners.
- One persistent local Codex app-server session reused across refreshes.
- Current-user autostart, manual refresh, CLI selection, and update checks.
- Rust + Tauri 2; the official NSIS installer is approximately 1.3 MB.

> Starting with v2.0.1, the main panel and tray icon use only the Codex weekly window. The short window is not displayed.

## Download

[Download the MetrikLite v2.0.2 installer](https://github.com/mors-lee/MetrikLite/releases/download/v2.0.2/MetrikLite_2.0.2_x64-setup.exe)

The [latest Release](https://github.com/mors-lee/MetrikLite/releases/latest) contains:

| Asset | Purpose |
| --- | --- |
| `MetrikLite_*_x64-setup.exe` | Recommended per-user NSIS installer |
| `MetrikLite_*_x64_en-US.msi` | MSI deployment package |
| `MetrikLite-portable.exe` | Portable executable |
| `SHA256SUMS.txt` | SHA256 checksums for Release assets |
| `MetrikLite-SBOM.spdx.json` | SPDX JSON software bill of materials |

Windows 10/11 x64 is supported. If Microsoft Edge WebView2 Runtime is missing, the installer uses Microsoft's official bootstrapper.

## First run

1. Install and launch MetrikLite.
2. If an independent Codex CLI is installed and signed in, the tray shows the weekly remaining percentage.
3. Left-click the number for details; right-click for refresh, settings, updates, downloads, and exit.
4. If Codex is not detected, choose `codex.exe`, `codex.cmd`, or `codex.bat` in Settings.

If Codex is unavailable, MetrikLite stays accessible with a `!` tray icon and an actionable settings screen instead of silently disappearing.

## SmartScreen and verification

Public builds are not currently signed with a commercial Authenticode certificate, so Windows may show **Unknown publisher**. This does not mean malware was detected, but it should not be ignored:

1. Download only from `github.com/mors-lee/MetrikLite/releases`.
2. Verify the file against `SHA256SUMS.txt`.
3. Consider Windows Defender, VirusTotal, source review, and GitHub Actions history together.

No single signal—including a code signature or a VirusTotal `0/70` result—proves absolute safety.

## Tray behavior

| Display | Meaning |
| --- | --- |
| `0`–`100` | Codex weekly remaining percentage |
| Red value | No more than 20% remains |
| `!` | Codex quota is temporarily unavailable |

Hover text is shown on two lines: `Codex remaining xx%` and `Reset MM-DD HH:MM`.

The default refresh interval is 30 seconds and can be configured from 10 to 300 seconds. Local files are stored under:

```text
%APPDATA%\MetrikLite\config.json
%APPDATA%\MetrikLite\metriklite.log
%LOCALAPPDATA%\MetrikLite\Runtime
```

## Codex discovery

MetrikLite checks `CODEX_BINARY`, the configured path, official `OpenAI.Codex_*` WinGet packages, `%APPDATA%\npm\codex.cmd`, and then `PATH`. Protected WindowsApps paths and known unrelated launchers are skipped.

## Privacy and network access

MetrikLite reads only its own local configuration, a Codex CLI path, weekly quota data returned by Codex app-server, and Segoe UI font data used to render the tray number.

It does **not** read Codex conversations, prompts, responses, project files, repositories, clipboard data, browser data, GitHub credentials, or Codex credential-file contents. MetrikLite never reads sensitive credentials, so no credential-access switch is required; Codex CLI owns authentication.

| Domain | When | Purpose |
| --- | --- | --- |
| `api.github.com` | Only after **Check for updates** | Public Release metadata |
| `github.com` | After the user opens downloads | Release page in the default browser |

OpenAI traffic used by Codex CLI to read account quota belongs to Codex itself and does not pass through a MetrikLite server. MetrikLite has no cloud service, analytics service, or advertising SDK.

## Build from source

Requirements: Windows 10/11 x64, Rust stable MSVC, Visual Studio 2022 C++ Build Tools, Node.js 22+, and WebView2 Runtime.

```powershell
npm ci
npm run tauri build
```

Quality checks:

```powershell
npm audit --audit-level=high
node --check src\main.js
cargo fmt --manifest-path src-tauri\Cargo.toml --all -- --check
cargo test --manifest-path src-tauri\Cargo.toml --locked
cargo clippy --manifest-path src-tauri\Cargo.toml --locked --all-targets -- -D warnings
```

## Release security

Every official Release is built from a public `v*` tag by GitHub Actions. The workflow publishes NSIS and MSI installers, a portable executable, SHA256 checksums, and an SPDX SBOM. Dependabot, CodeQL, `cargo audit`, and `npm audit` are enabled. See [SECURITY.md](SECURITY.md).

## License and attribution

MetrikLite is distributed under the [MIT License](LICENSE). The Rust/Tauri v2 implementation was written independently.

The quota-tray concept was inspired by [Metrik](https://github.com/keros68/metrik). MetrikLite is not an official OpenAI product.
