; ============================================================================
; SerialPortTool 安装程序（Inno Setup 6）
;
; 编译方式（本地与 CI 共用，见 scripts/build-installer.ps1）：
;   ISCC.exe /DAppVersion=1.8.14 /DSourceDir=<publish 目录> /DOutputDir=<输出目录> SerialPortTool.iss
;
; 可选开关：
;   /DIncludeChinese=1  —— 仅当 ISCC 的 Languages\ChineseSimplified.isl 存在时传入
;                          （Inno Setup 官方安装包不含简体中文语言文件）
;
; 注意：版本号一律由命令行注入（与 version.json 同源），禁止在此硬编码第二份副本。
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\packages\installer"
#endif

; [Files] 的排除列表由 scripts/build-installer.ps1 注入：
; 调试符号 + 除 zh-CN / en-us 之外的框架语言资源目录（与便携 ZIP 的保留清单一致）。
; 这里保留一个仅供单独编译 .iss 时使用的兜底值。
#ifndef Excludes
  #define Excludes "*.pdb"
#endif

#define AppDisplayName "串口工具 (SerialPortTool)"
#define AppPublisher "SerialPortTool"
#define AppExeName "SerialPortTool.exe"

[Setup]
; AppId 必须与 Services/UpdateInstallerService.cs 的 InstalledBuildInfo.AppId 完全一致。
; 一旦发布不可更改，否则老版本无法被覆盖升级（会变成并存安装）。
; Pascal 风格的 INI 中字面量 "{" 需要写成 "{{"。
AppId={{7B4E2C19-3A6D-4F82-9E51-0C8A5D3F1B74}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
AppPublisher={#AppPublisher}
; VersionInfoVersion 必须是四段式（x.x.x.x）
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\SerialPortTool
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir={#OutputDir}
OutputBaseFilename=SerialPortTool-Setup-v{#AppVersion}-win-x64
SetupIconFile=..\Assets\Images\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 每用户安装：无需管理员、不弹 UAC，因此自动更新可以完全静默完成。
; {autopf} 在 lowest 下解析为 %LOCALAPPDATA%\Programs\SerialPortTool。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
; CloseApplications=yes：需要时由 Restart Manager 关闭仍在运行的主程序（例如用户手动双击安装包）。
CloseApplications=yes
; RestartApplications 必须保持 no，重启主程序由下面 [Code] 段显式完成。
; 原因：RestartApplications 底层是 Restart Manager 的 RmRestart，它只会重启「本次安装过程中
; 被 Restart Manager 主动关闭」的进程。自动更新路径下主程序在启动安装器后立即自行退出
; （MainWindow.DownloadAndInstallAsync → Close() → App.OnWindowClosed → Environment.Exit(0)），
; Restart Manager 无进程可关，自然也无进程可重启 —— 表现为「更新装完了但程序没再打开」。
; 保留 no 还能避免与 [Code]/[Run] 的显式重启叠加而拉起两个实例。
RestartApplications=no
SetupMutex=SerialPortTool-Setup-Mutex
; 应用本身要求 Windows 10 1809+（Windows App SDK 限制），这里放宽以兼容更多 Inno 版本。
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#ifdef IncludeChinese
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; 升级时先清空安装目录，避免上一版本遗留的 DLL / 语言资源堆积（WinUI 3 对残留程序集很敏感）。
; 用户设置（%LOCALAPPDATA%\SerialPortTool）与运行日志（Documents\SerialPortTool）都不在 {app} 下，不会受影响。
Type: filesandordirs; Name: "{app}\*"

[Files]
; Excludes 由 build-installer.ps1 注入：调试符号 + 除 zh-CN / en-us 外的框架语言资源目录。
; 注意：这里**不能**加 createallsubdirs —— Inno Setup 默认跳过空目录，
; 但该 flag 会连「被 Excludes 排空」的目录也建出来，导致安装目录里仍躺着 84 个空的 xx-YY 语言文件夹。
; 实测：加上它 → 93 个目录（84 个为空）；去掉它 → 9 个目录（全部有文件）。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "{#Excludes}"

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppDisplayName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent

[Code]
// 静默更新（自动更新路径）下由安装器显式重启主程序，不能依赖 RestartApplications：
//   1) RestartApplications 只会重启被 Restart Manager 关闭的进程，而自动更新时主程序已自行退出；
//   2) [Run] 条目带 skipifsilent，在 /SILENT 下不会执行。
// 因此这里不做任何开关判断 —— 静默模式必定重启。
// 交互式安装（非静默）由 [Run] 的 postinstall 复选框负责，两边互斥，不会拉起两个实例。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardSilent() then
    Exec(ExpandConstant('{app}\{#AppExeName}'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
