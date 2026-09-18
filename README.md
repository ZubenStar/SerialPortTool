# 串口工具 (SerialPort Tool)

> **多串口实时监控 / 调试工具** — 基于 WinUI 3 与 .NET 9 构建的 Windows 桌面串口助手。

SerialPortTool 面向需要同时盯多个串口的调试与产线场景：多口并发收发、实时日志筛选、波特率不匹配自动识别，以及通过串口批量推送 tuning / TOTA 固件包。

---

## ✨ 功能特性

- **多串口并发管理**：扫描、逐个打开/关闭、一键「打开全部 / 关闭全部」，每个串口独立收发与统计。
- **灵活串口配置**：波特率（含自定义，可输入 1 ~ 12000000 之间的整数）、数据位、停止位、校验位；配置持久化到 `settings.json`（原子写入，读取失败时自动进入只读保护，绝不覆盖原文件）。
- **单实例运行**：同一时间只允许一个实例。重复启动会直接给出提示并退出，避免两个实例争抢同一个串口、以及相互覆盖设置文件。
- **外观切换**：菜单「外观」可选 **跟随系统 / 浅色 / 深色**，切换即时生效并会被记住；两套配色各自校准（含端口标识色），标题栏使用云母材质（系统不支持时自动降级为实色）。
- **可折叠配置栏**：左侧串口配置与端口列表可一键折叠，窗口变窄时会自动收起，把宽度让给日志。
- **波特率不匹配检测**：实时分析数据质量，识别错的波特率并给出高置信度修正建议（一键修正）。
- **实时日志系统**：
  - 关键字 / 正则表达式筛选，带搜索历史（可单条删除、可清空）。
  - 暂停日志追加（不中断串口接收）、清空日志。
  - 每个串口独立 RX 颜色 + 可配置 TX 颜色，便于多口对读；日志区支持 `Ctrl+C` 复制、`Ctrl+A` 全选与右键菜单。
  - 每行日志左侧带一条所属串口的「通道色条」，日志上方另有「通道图例」列出 颜色 → 端口 → 收发字节数，多口交织时一眼分清每行的归属。
- **Tuning / TOTA 推送**：选择 `.bin` 载荷与 JSON 协议描述文件，广播到所有已打开串口，支持监听文件变化自动重发。
- **数据发送**：文本 / 十六进制模式（十六进制支持空格 / `-` / `,` / `:` 等分隔符与每组可选的 `0x` 前缀），回车或点「发送」即可广播到全部已打开串口。
- **日志落盘**：串口日志异步批量写盘；应用运行日志（Serilog）可在「工具 → 打开日志文件夹」直接定位。
- **检查更新 / 自动更新**：启动后静默检查 GitHub Releases，有新版本时提示并可下载安装；「帮助 → 检查更新」可随时手动检查。
- **现代化界面**：Fluent Design + 云母材质标题栏，深浅两套配色，统一的字形图标与控件样式，日志列表虚拟化，可跑高吞吐数据流。

---

## 🏗️ 技术栈

| 项目 | 选型 |
| --- | --- |
| UI 框架 | WinUI 3（Windows App SDK `1.6.241114003`，self-contained） |
| 运行时 | .NET 9（`net9.0-windows10.0.22621.0`，最低 `10.0.17763.0`） |
| 语言 | C# 13（.NET 9 SDK 默认；未固定 `LangVersion`） |
| 架构模式 | MVVM（`CommunityToolkit.Mvvm` 8.2.2） |
| 串口通信 | `System.IO.Ports` 9.0.0 |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` 9.0.0（轻量 `ServiceCollection`，不使用 Generic Host） |
| 日志 | `Serilog` 4.0.0 + `Serilog.Sinks.File` 5.0.0 + `Serilog.Extensions.Logging` 8.0.0 |
| 目标平台 | 仅 `x64`；**非打包**（unpackaged）应用 |

> 依赖清单以 `SerialPortTool.csproj` 为准。

---

## 📂 项目结构

```
SerialPortTool/
├── App.xaml / App.xaml.cs           # 入口：Serilog、DI 容器、全局异常处理、退出清理
├── MainWindow.xaml / .cs            # 主窗口（位于仓库根目录，没有 Views/ 目录）
├── Package.appxmanifest             # 保留给 MSIX 工具链使用（当前为非打包构建）
├── SerialPortTool.csproj / .sln
├── version.json                     # 版本号与更新日志的唯一来源
├── mic-tota-tuning.json             # Tuning 协议描述文件的示例（非应用配置）
├── Themes/Tokens.xaml               # 设计令牌：浅色 / 深色 / 高对比三套配色 + 字体间距圆角
├── Themes/Controls.xaml             # 按钮族样式与模板（唯一被重新模板化的控件）
├── Assets/Images/                   # logo.ico（16–256 多尺寸图标）、logo.png（1024 主图）
├── Controls/                        # LogListView：唯一的自定义控件（虚拟化日志列表）
├── Converters/                      # BoolToVisibility / InverseBoolToVisibility / HexColorToBrush
├── Core/Enums/                      # ConnectionState、FilterType、UpdateCheckStatus、AppThemePreference
├── Helpers/                         # VersionInfo、BuildInfo.g.cs（构建时生成）
├── Models/                          # SerialPortConfig、LogEntry、FilterRule、PortStatistics、
│                                    # PortColorSlot / PortColorPalette、UpdateReleaseInfo / UpdateCheckResult
├── Services/                        # 串口、波特率检测、数据校验、日志过滤、文件日志、设置、Tuning 协议、更新与安装
├── ViewModels/                      # MainViewModel（含 RangeObservableCollection、PortViewModel、PortColorOption）
├── installer/SerialPortTool.iss     # Inno Setup 安装程序脚本（每用户安装，支持静默替换升级）
├── scripts/                         # 版本、构建信息、清单版本、安装包、publish 目录裁剪
└── .github/workflows/release.yml    # 打 tag 触发的发布流水线
```

---

## 📥 安装与更新

发布产物有两种，都是自包含的，无需另外安装运行时：

| 方式 | 文件 | 说明 |
| --- | --- | --- |
| **安装程序（推荐）** | `SerialPortTool-Setup-v<版本>-win-x64.exe` | 单个文件，安装到 `%LOCALAPPDATA%\Programs\SerialPortTool`。**当前用户级安装，无需管理员权限，不弹 UAC**；创建开始菜单快捷方式，可选桌面快捷方式；可从系统「应用和功能」卸载。 |
| **便携版** | `SerialPortTool-v<版本>-win-x64.zip` | 解压到任意目录，直接运行 `SerialPortTool.exe`，不写注册表。 |

卸载只清理程序文件，**保留**用户设置（`%LOCALAPPDATA%\SerialPortTool\settings.json`）与运行日志。

### 检查更新

- **启动静默检查**：启动后数秒后台检查一次，只有发现新版本才会提示；距上次检查不足 24 小时不会重复联网。
- **手动检查**：「帮助 → 检查更新」随时触发，无论结果如何都会给出明确反馈。
- **更新对话框**（安装版）：可「下载并安装」，或「跳过此版本」（静默检查时）/「前往下载页」（手动检查时），或「稍后 / 关闭」。选择「跳过此版本」后，静默检查不会再提示该版本。
- **更新对话框**（便携版）：只能「前往下载页」（静默检查时还可「跳过此版本」）。
- **自动替换**：仅在**安装程序安装的版本**上生效——下载并校验安装包后，程序自动退出、由安装程序静默替换文件并自动重启，用户设置与日志不会丢失。
- **便携版**：不会自动替换任何文件，只会提示并打开下载页（避免覆盖你自己的解压目录）。

---

## 🚀 快速开始

### 前置要求

1. **Windows 10 1809（build 17763）及以上**，推荐 Windows 11。
2. **.NET 9 SDK**（`dotnet --version` 显示 `9.0.x`）。
3. **Visual Studio 2022 17.8+**（使用 IDE 时），需要工作负载：`.NET 桌面开发`、`通用 Windows 平台开发`、`Windows 应用开发`。
4. Windows App SDK 1.6 运行环境 / 构建工具（通过 NuGet 还原自动获取）。

### 构建与运行

**Visual Studio**：打开 `SerialPortTool.sln` → 还原 NuGet 包 → `Ctrl+Shift+B` 构建 → `F5` 调试。

**命令行（PowerShell）**：

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run

# 发布自包含版本（与发布流水线一致的参数）
dotnet publish --configuration Release --runtime win-x64 --self-contained true `
  --output publish/x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:PublishSingleFile=false

# 打包成本地安装程序（需要 Inno Setup 6，产物输出到 packages/installer）
.\scripts\build-installer.ps1
```

> 构建过程会调用 `scripts/` 下的 PowerShell 脚本，因此必须在 Windows 上构建。

### 基本使用

1. 「工具 → 扫描串口」，在左侧「可用串口」中选择并打开（可多选并发），或直接点「全部打开」。
2. 顶部工具条第二行选择 tuning `.bin` 与协议 JSON，点「发送 Tuning」即在所有已打开串口上广播。
3. 底部输入框输入内容后按 **回车**（或点「发送」）发送到全部已打开串口；勾选「十六进制发送」可发送 hex 字节。
4. 日志区用搜索框（支持正则）过滤；工具条右侧为 全选 / 复制 / 清空 / 暂停，`Ctrl+C` 复制选中行、`Ctrl+A` 全选。
5. 出现「检测到波特率可能不匹配」提示时，可直接一键修正。
6. 需要更大的日志区时，点标题栏右侧的折叠按钮（或「工具 → 折叠 / 展开串口配置栏」）收起左侧配置栏。
7. 换配色走菜单「外观 → 跟随系统 / 浅色 / 深色」，切换即时生效，重启后保持上次选择。

---

## 📦 版本与发布

- 版本号与更新日志的**唯一来源**是 `version.json`，构建时自动同步到程序集信息与 `Package.appxmanifest`。
- 本地升版本：编辑 `version.json`，或执行
  ```powershell
  .\scripts\bump-version.ps1 -BumpType patch
  ```
- 正式发布：推送 `v*` 形式的 tag（如 `v2.0.0`），GitHub Actions 会校验 `version.json` 与 tag 一致后自动构建 x64 自包含产物并创建 Release，同时上传 **单文件安装程序 Setup.exe** 与**便携 ZIP**。

## 🗂️ 运行数据位置

| 内容 | 位置 |
| --- | --- |
| 应用运行日志（Serilog） | `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log`（按天滚动，保留 7 天，单文件上限 50 MB） |
| 用户设置 | `%LOCALAPPDATA%\SerialPortTool\settings.json`（由 `SettingsService` 管理：写入带文件锁且为同目录临时文件 + 原子替换；若该文件无法解析则本次运行只读，不会覆盖原文件） |
| 更新下载临时目录 | `%TEMP%\SerialPortTool\Update\`（安装包下载落地点，启动时自动清理历史残留） |
| 安装位置（安装版） | `%LOCALAPPDATA%\Programs\SerialPortTool`（每用户安装，不需要管理员权限） |

---

## 📝 开发与文档约定

**AI 改完代码后，必须同步更新相关文档。** 文档是交付物的一部分，文档与代码不一致即视为改动未完成：

| 改动内容 | 需同步更新 |
| --- | --- |
| 用户可见的功能、UI、行为、打包/运行要求 | 本 `README.md` |
| 架构、服务、DI 注册、线程模型、性能与可靠性机制、构建/版本流程、编码约定 | `AGENTS.md` |
| 任何会随版本发布的内容（功能、修复、重构、性能、构建） | `version.json` 的更新日志 |
| 发布流水线 | `.github/workflows/release.yml` 与 `AGENTS.md` 的 CI/CD 章节 |
| Tuning 协议描述格式或示例 | `mic-tota-tuning.json` 与 `AGENTS.md` 的 Tuning 章节 |

其他约定：

- **不要新增顶层 `*.md` 文档**。面向用户的写进本文件，面向开发者/代理的写进 `AGENTS.md`；历史设计稿不作为文档保留，Git 历史就是归档。
- 不要在文档里留下过期的路径 / 类型 / 方法 / 常量引用；重命名或删除被文档引用的符号时，同一提交内修好引用。
- `AGENTS.md` 中标注为 **do not regress（不得回退）** 的机制是有历史 bug 背书的，不要在没有书面理由的情况下删除或"简化"。

---

## 🤝 贡献

欢迎提交 Issue 与 Pull Request。提交前请确认：

1. `dotnet build --configuration Release` 通过；
2. 按上面的表格同步更新了相关文档；
3. 需要发版时按「版本与发布」流程升版本并打 tag。

## 📄 许可证

仓库当前未包含 `LICENSE` 文件。如需以开源方式分发，请先补充许可证文件（例如 MIT）。
