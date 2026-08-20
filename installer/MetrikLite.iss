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
#define MyAppVersion "1.1.2"
#define MyAppPublisher "Mors"
#define MyAppExeName "MetrikLite.exe"
#define MyAppId "{{9F6B2C41-8D3E-4A57-B1C0-2E8D5A9F7B43}"

[Setup]
; AppId 固定不变：升级安装才能覆盖旧版而不是装出第二份
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/mors-lee/MetrikLite
AppSupportURL=https://github.com/mors-lee/MetrikLite/issues
AppUpdatesURL=https://github.com/mors-lee/MetrikLite/releases/latest
SetupIconFile=..\MetrikLite-v2.ico
; 每用户安装：{autopf} 在 lowest 权限下解析为 %LocalAppData%\Programs
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=Output
OutputBaseFilename=MetrikLite-Setup
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableWelcomePage=no
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=MetrikLite Windows 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
; 简体中文翻译固定来自 Inno Setup 官方源码，并随仓库一起发布，保证本地与 CI 一致。
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimplified.AdditionalOptions=附加选项:
english.AdditionalOptions=Additional options:
chinesesimplified.AutoStart=登录 Windows 后自动启动 MetrikLite
english.AutoStart=Start MetrikLite automatically after signing in to Windows
chinesesimplified.UninstallName=卸载 MetrikLite
english.UninstallName=Uninstall MetrikLite
chinesesimplified.FinishedHint=MetrikLite 已安装完成。启动后它会常驻任务栏通知区域；左键查看配额，右键打开设置菜单。
english.FinishedHint=MetrikLite is installed. It runs in the notification area; left-click for quota details and right-click for settings.

[Tasks]
; 默认勾选开机自启（托盘常驻工具的常见预期）；不想默认勾就把 \checked 去掉
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalOptions}"; Flags: checkedonce

[Files]
Source: "..\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\MetrikLite-v2.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\MetrikLite-v2.ico"; IconIndex: 0
; 使用版本化图标文件路径并在升级时重写快捷方式，绕过 Windows 旧图标缓存。
Name: "{userdesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\MetrikLite-v2.ico"; IconIndex: 0
Name: "{group}\{cm:UninstallName}"; Filename: "{uninstallexe}"

[Registry]
; 与程序内置 SetAutoStart() 同键同名；卸载时一并清理（uninsdeletevalue）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpFinished then
    WizardForm.FinishedLabel.Caption := ExpandConstant('{cm:FinishedHint}');
end;
