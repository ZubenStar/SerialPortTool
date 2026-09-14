; ============================================================================
; SerialPortTool 安装程序（Inno Setup 6）
;
; 编译方式（本地与 CI 共用，见 scripts/build-installer.ps1）：
;   ISCC.exe /DAppVersion=1.8.14 /DSourceDir=<publish 目录> /DOutputDir=<输出目录> SerialPortTool.iss
;
; 可选开关：
;   /DIncludeChinese=1  —— 仅当 ISCC 的 Languages\ChineseSimplified.isl 存在时传入
;                          （Inno Setup 官方安装包不含简体中文语言文件）
;   /DRestartFallback=1 —— 兜底重启：/SILENT 下 RestartApplications 未重启主程序时启用
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

#ifndef RestartFallback
  #define RestartFallback "0"
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
; 自动更新的关键：/CLOSEAPPLICATIONS 由 Restart Manager 关闭正在运行的主程序，
; /RESTARTAPPLICATIONS 在文件替换完成后自动把它拉起来。
CloseApplications=yes
RestartApplications=yes
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
// 兜底重启：仅在显式传入 /DRestartFallback=1 时启用。
// 默认关闭，避免与 /RESTARTAPPLICATIONS 同时生效而拉起两个实例。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardSilent() and ({#RestartFallback} = 1) then
    Exec(ExpandConstant('{app}\{#AppExeName}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
