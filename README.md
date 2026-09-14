# 串口工具 (SerialPort Tool)

> **多串口实时监控 / 调试工具** — 基于 WinUI 3 与 .NET 9 构建的 Windows 桌面串口助手。

SerialPortTool 面向需要同时盯多个串口的调试与产线场景：多口并发收发、实时日志筛选、波特率不匹配自动识别，以及通过串口批量推送 tuning / TOTA 固件包。

---

## ✨ 功能特性

- **多串口并发管理**：扫描、逐个打开/关闭、一键「打开全部 / 关闭全部」，每个串口独立收发与统计。
- **灵活串口配置**：波特率（含自定义）、数据位、停止位、校验位；配置持久化到 `settings.json`。
- **波特率不匹配检测**：实时分析数据质量，识别错的波特率并给出高置信度修正建议（一键修正）。
- **实时日志系统**：
  - 关键字 / 正则表达式筛选，带搜索历史（可单条删除、可清空）。
  - 暂停日志追加（不中断串口接收）、清空日志。
  - 每个串口独立 RX 颜色 + 可配置 TX 颜色，便于多口对读；日志区支持 `Ctrl+C` 复制、`Ctrl+A` 全选与右键菜单。
- **Tuning / TOTA 推送**：选择 `.bin` 载荷与 JSON 协议描述文件，广播到所有已打开串口，支持监听文件变化自动重发。
- **数据发送**：文本 / 十六进制模式，可广播到全部已打开串口。
- **日志落盘**：串口日志异步批量写盘；应用运行日志（Serilog）可在「工具 → 打开日志文件夹」直接定位。
- **现代化界面**：Fluent Design + 云母材质背景，日志列表虚拟化，可跑高吞吐数据流。

---

## 🏗️ 技术栈

| 项目 | 选型 |
| --- | --- |
| UI 框架 | WinUI 3（Windows App SDK `1.6.241114003`，self-contained） |
| 运行时 | .NET 9（`net9.0-windows10.0.22621.0`，最低 `10.0.17763.0`） |
| 语言 | C# 12 |
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
├── Assets/Images/                   # logo.ico、logo.png
├── Controls/                        # LogListView：唯一的自定义控件（虚拟化日志列表）
├── Converters/                      # BoolToVisibility / InverseBoolToVisibility / StringToVisibility / HexColorToBrush
├── Core/Enums/                      # ConnectionState、DataFormat、FilterType
├── Helpers/                         # PerformanceMonitor、VersionInfo、BuildInfo.g.cs（构建时生成）
├── Models/                          # SerialPortConfig、LogEntry、FilterRule、CommandPreset、PortStatistics
├── Services/                        # 串口、波特率检测、数据校验、日志过滤、文件日志、设置、Tuning 协议
├── ViewModels/                      # MainViewModel（含 RangeObservableCollection）
├── scripts/                         # 版本 / 构建信息 / 发布说明 / 清单版本 相关脚本
└── .github/workflows/release.yml    # 打 tag 触发的发布流水线
```

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
```

> 构建过程会调用 `scripts/` 下的 PowerShell 脚本，因此必须在 Windows 上构建。

### 基本使用

1. 「工具 → 扫描串口」，在左侧「可用串口」中选择并打开（可多选并发）。
2. 右上角选择 tuning `.bin` 与协议 JSON，点击发送即在所有已打开串口上广播。
3. 日志区使用搜索框（支持正则）过滤，`Ctrl+C` 复制选中行，`Ctrl+A` 全选。
4. 出现「检测到波特率可能不匹配」提示时，可直接一键修正。

---

## 📦 版本与发布

- 版本号与更新日志的**唯一来源**是 `version.json`，构建时自动同步到程序集信息与 `Package.appxmanifest`。
- 本地升版本：编辑 `version.json`，或执行
  ```powershell
  .\scripts\bump-version.ps1 -BumpType patch
  ```
- 正式发布：推送 `v*` 形式的 tag（如 `v1.8.12`），GitHub Actions 会校验 `version.json` 与 tag 一致后自动构建 x64 自包含 ZIP 并创建 Release。

## 🗂️ 运行数据位置

| 内容 | 位置 |
| --- | --- |
| 应用运行日志（Serilog） | `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log`（按天滚动，保留 7 天，单文件上限 50 MB） |
| 用户设置 | `%LOCALAPPDATA%\SerialPortTool\settings.json`（由 `SettingsService` 管理，写入带文件锁） |

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
