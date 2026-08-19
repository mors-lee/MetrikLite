; ============================================================================
; 【文件】installer\MetrikLite.iss —— Inno Setup 安装包脚本
; ============================================================================
; 【用途】把发布产物 publish\MetrikLite.exe 打成 MetrikLite-Setup.exe：
;   开始菜单快捷方式、控制面板卸载入口、可选开机自启、安装完可直接启动。
;
; 【构建前提】先在本文件上一级目录产出单文件 exe：
;   dotnet publish MetrikLite.csproj -c Release -r win-x64 --self-contained true ^
;     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
;     -p:EnableCompressionInSingleFile=true -o publish
;   然后执行：ISCC installer\MetrikLite.iss
;   产物位置：installer\Output\MetrikLite-Setup.exe
;   （GitHub Actions 的 release.yml 已自动完成以上两步，本地一般不需要手动执行。）
;
; 【设计要点】
;   · 每用户安装（PrivilegesRequired=lowest，装到 %LocalAppData%\Programs）：
;     不弹 UAC，卸载不残留系统级条目——托盘小工具的推荐形态。
;   · 开机自启任务写的键与程序内置 TrayHost.SetAutoStart() 完全一致
;     （HKCU\...\Run，值名 MetrikLite），保证两边互斥、不会出现两个自启项。
;
; 【修改指南】
;   · 发新版本：改下面的 MyAppVersion，同时 git tag v同版本号 触发 Release。
;   · 想改安装目录/默认勾选：见 [Setup] 与 [Tasks] 段注释。
; ============================================================================

; 版本号唯一入口（发布时与 git tag 保持一致，如 v1.0.0 → "1.0.0"）
#define MyAppName "MetrikLite"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "Mors"
#define MyAppExeName "MetrikLite.exe"
#define MyAppId "{{9F6B2C41-8D3E-4A57-B1C0-2E8D5A9F7B43}"

[Setup]
; AppId 固定不变：升级安装才能覆盖旧版而不是装出第二份
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
; 每用户安装：{autopf} 在 lowest 权限下解析为 %LocalAppData%\Programs
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=MetrikLite-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern

[Tasks]
; 默认勾选开机自启（托盘常驻工具的常见预期）；不想默认勾就把 \checked 去掉
Name: "autostart"; Description: "开机自动启动 {#MyAppName}"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; 与程序内置 SetAutoStart() 同键同名；卸载时一并清理（uninsdeletevalue）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName}"; Flags: nowait postinstall skipifsilent
