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

; AppVersion 必须由命令行注入（见 build-installer.ps1，来源是 version.json）。
; 这里刻意不放 "0.0.0" 兜底：直接编译 .iss 会安静地产出一个版本号为 0.0.0 的安装包，而
; UpdateService 用数值比较版本，装了它的机器再也不会收到任何更新提示 —— 一次误编译就永久
; 破坏该机器的更新路径。宁可编译失败。
#ifndef AppVersion
  #error AppVersion is not defined. Build through scripts/build-installer.ps1, or pass /DAppVersion=<x.y.z> to ISCC.exe.
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\packages\installer"
#endif

; [Files] 的排除列表由 scripts/build-installer.ps1 注入（目前只有 *.pdb 这一条兜底）。
; 框架语言资源目录不在这里排除：保留清单与目录名模式统一由
; scripts/prune-publish-output.ps1 维护（便携 ZIP 侧调用同一个脚本），
; build-installer.ps1 在调用 ISCC 之前就已就地裁剪 publish 目录。
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
; 应用本身要求 Windows 10 1809（build 17763）+（Windows App SDK 限制，见 csproj 的
; TargetPlatformMinVersion）。原先写的 10.0 会让安装器在 1607/1709 上照常安装，用户装完一启动就崩，
; 且因为文件已经落盘，报错看起来像应用 bug 而不是"系统版本不够"。必须与 csproj 保持一致。
MinVersion=10.0.17763

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
; 语言资源目录在调用 ISCC 之前就已被 scripts/prune-publish-output.ps1 从 publish 目录里删掉，
; Excludes 现在只剩调试符号这一条兜底（见脚本头部的说明）。
; 注意：这里**不能**加 createallsubdirs —— Inno Setup 默认跳过空目录，
; 但该 flag 会连「被 Excludes 排空」的目录也建出来，导致安装目录里仍躺着 84 个空的 xx-YY 语言文件夹。
; 实测：加上它 → 93 个目录（84 个为空）；去掉它 → 9 个目录（全部有文件）。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "{#Excludes}"

[Icons]
; IconFilename 显式指向随包发布的 logo.ico，而不是让 Windows 去 exe 里取主图标：
; 快捷方式图标的来源因此与 exe 的图标资源解耦（exe 图标若哪天没嵌进去，快捷方式也不会跟着错）。
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\Images\logo.ico"
Name: "{group}\{cm:UninstallProgram,{#AppDisplayName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\Images\logo.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppDisplayName}}"; Flags: nowait postinstall skipifsilent

[Code]
// ---------------------------------------------------------------------------
// shell 图标刷新（v2.0.3，不要删除）
//
// Windows 会把文件/快捷方式的图标缓存在 iconcache_*.db 和资源管理器进程内的系统
// 图像列表里。自动更新是「同路径覆盖文件 + 主程序立刻自行退出」，资源管理器既没收到
// 通知、也没有重绘时机，于是桌面/开始菜单快捷方式会继续显示上一版的图标，直到用户
// 手动 F5 或重启资源管理器（v2.0.1 换成多尺寸 .ico 后，v2.0.2 仍然复现）。
// 安装收尾时主动通知 shell，强制重新读取快捷方式图标并重绘桌面。
// ---------------------------------------------------------------------------
const
  SHCNE_ASSOCCHANGED = $08000000;
  SHCNF_IDLIST = $0000;

procedure SHChangeNotify(wEventId: Longint; uFlags: Longword; dwItem1, dwItem2: Longint);
  external 'SHChangeNotify@shell32.dll stdcall';

// 静默更新（自动更新路径）下由安装器显式重启主程序，不能依赖 RestartApplications：
//   1) RestartApplications 只会重启被 Restart Manager 关闭的进程，而自动更新时主程序已自行退出；
//   2) [Run] 条目带 skipifsilent，在 /SILENT 下不会执行。
// 因此这里不做任何开关判断 —— 静默模式必定重启。
// 交互式安装（非静默）由 [Run] 的 postinstall 复选框负责，两边互斥，不会拉起两个实例。
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;

  SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0);

  if WizardSilent() then
    Exec(ExpandConstant('{app}\{#AppExeName}'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode);
end;
