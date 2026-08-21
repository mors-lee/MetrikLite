# MetrikLite

A lightweight, native, local-first Windows tray utility that displays Codex remaining quota as a numeric notification-area icon.

[Latest release](https://github.com/mors-lee/MetrikLite/releases/latest) · [中文说明](./README.md) · [Issues](https://github.com/mors-lee/MetrikLite/issues)

![MetrikLite Tauri quota panel](docs/images/details-panel-v2.png)

## Highlights

- Dynamically scaled and optically centered percentage tray icon.
- Separate percentage and reset-time lines for short and weekly windows.
- Clean Tauri 2 panel positioned above the Windows notification area.
- One persistent Codex app-server session reused across refreshes.
- Manual refresh, CLI selection, update checks, and current-user autostart.
- Clear `!` state when Codex is unavailable, so settings remain accessible.
- Rust release profile optimized for size with LTO and symbol stripping.

## Downloads

The [Releases page](https://github.com/mors-lee/MetrikLite/releases/latest) provides:

- `MetrikLite_*_x64-setup.exe` — recommended current-user NSIS installer.
- `MetrikLite_*_x64_en-US.msi` — MSI package.
- `MetrikLite-portable.exe` — portable executable.
- `SHA256SUMS.txt` — checksums for release assets.
- `MetrikLite-SBOM.spdx.json` — SPDX software bill of materials.

Windows 10/11 x64 is supported. The installer downloads Microsoft's WebView2 bootstrapper only when WebView2 is missing.

Current public builds are not Authenticode-signed and may trigger SmartScreen. Download only from this repository and verify SHA256. A clean antivirus result is useful evidence, not proof of absolute safety.

## Codex discovery

MetrikLite checks `CODEX_BINARY`, the configured path, official WinGet packages, the npm global shim, and then `PATH`. Protected WindowsApps paths and known unrelated launchers are skipped. The native WinGet executable is preferred over `codex.cmd` to avoid an unnecessary command-shell process.

If Codex is absent or not logged in, MetrikLite keeps a `!` tray icon and an actionable empty state instead of disappearing.

## Privacy

MetrikLite reads only its local configuration, a Codex executable path, Segoe UI font data for tray rendering, and rate-limit window data returned by Codex app-server.

It does **not** read prompts, conversations, project files, repositories, clipboard data, browser data, GitHub credentials, or Codex credential-file contents. Codex itself owns authentication.

MetrikLite contacts `api.github.com` only after the user clicks **Check for updates**. Opening a release page sends the default browser to `github.com`. MetrikLite has no cloud service.

## Build

Requirements: Windows 10/11 x64, Rust stable MSVC, Visual Studio 2022 C++ Build Tools, Node.js 22+, and WebView2 Runtime.

```powershell
npm ci
npm run tauri build
```

Quality checks:

```powershell
node --check src\main.js
cargo fmt --manifest-path src-tauri\Cargo.toml --all -- --check
cargo test --manifest-path src-tauri\Cargo.toml --locked
cargo clippy --manifest-path src-tauri\Cargo.toml --locked --all-targets -- -D warnings
```

## Release security

Every `v*` tag is built from public source by GitHub Actions. The workflow publishes NSIS/MSI installers, a portable executable, SHA256 checksums, and an SPDX SBOM. Dependabot, CodeQL, `cargo audit`, and `npm audit` are enabled. See [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). The Rust/Tauri v2 implementation was written independently for MetrikLite and does not copy AGPL source from `keros68/metrik`. MetrikLite is not an official OpenAI product.
