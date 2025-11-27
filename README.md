# 串口工具 (SerialPort Tool)

> **多串口实时监控工具** - 基于 WinUI 3 构建的现代化串口调试助手

## 📋 项目概述

SerialPort Tool 是一款功能强大的 Windows 桌面串口调试工具，支持多串口同时监控、实时日志筛选、智能数据分析等高级功能。

### ✨ 核心特性

- 🌟 **多串口同时管理**: 支持同时打开和监控多个串口
- 🔍 **实时日志筛选**: 正则表达式、关键字、日志级别过滤
- 📊 **数据统计分析**: 实时收发统计、错误率监控
- 🎨 **现代化UI**: Fluent Design + 云母材质背景
- 💾 **会话管理**: 保存和恢复串口配置
- 📝 **日志导出**: 支持多种格式导出

## 🏗️ 技术栈

- **UI框架**: WinUI 3
- **运行时**: .NET 9
- **架构模式**: MVVM (CommunityToolkit.Mvvm)
- **串口通信**: System.IO.Ports
- **依赖注入**: Microsoft.Extensions.DependencyInjection
- **日志系统**: Serilog

## 📂 项目结构

```
SerialPortTool/
├── Core/
│   └── Enums/                    # 枚举定义
│       ├── LogLevel.cs
│       ├── DataFormat.cs
│       ├── ConnectionState.cs
│       └── FilterType.cs
├── Models/                       # 数据模型
│   ├── SerialPortConfig.cs
│   ├── LogEntry.cs
│   ├── FilterRule.cs
│   ├── CommandPreset.cs
│   └── PortStatistics.cs
├── Services/                     # 业务服务
│   ├── ISerialPortService.cs
│   ├── SerialPortService.cs
│   ├── ILogFilterService.cs
│   └── LogFilterService.cs
├── ViewModels/                   # 视图模型
│   └── MainViewModel.cs
├── Views/                        # 视图
│   ├── MainWindow.xaml
│   └── MainWindow.xaml.cs
├── App.xaml                      # 应用程序定义
├── App.xaml.cs                   # 应用程序逻辑
└── Package.appxmanifest          # 应用清单
```

## 🚀 快速开始

### 前置要求

1. **Windows 10 版本 1809+ 或 Windows 11**
2. **Visual Studio 2022** (17.8+) 包含以下工作负载:
   - .NET桌面开发
   - 通用Windows平台开发
   - Windows应用开发
3. **.NET 9 SDK**
4. **Windows App SDK 1.6+**

### 构建步骤

#### 方法 A: 使用 Visual Studio (推荐)

1. 打开 `SerialPortTool.sln` 解决方案
2. 恢复 NuGet 包:
   - 右键点击解决方案 → "还原 NuGet 包"
3. 构建项目:
   - 按 `Ctrl+Shift+B` 或选择 "生成" → "生成解决方案"
4. 运行项目:
   - 按 `F5` 启动调试
   - 或按 `Ctrl+F5` 启动而不调试

#### 方法 B: 使用命令行

```powershell
# 进入项目目录
cd SerialPortTool

# 恢复 NuGet 包
dotnet restore

# 构建项目
dotnet build --configuration Release

# 运行项目
dotnet run
```

## 📦 依赖包

项目使用以下主要 NuGet 包:

```xml
<!-- WinUI 3 和 MVVM -->
<PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.241114003" />
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
<PackageReference Include="WinUIEx" Version="2.3.4" />

<!-- 串口通信 -->
<PackageReference Include="System.IO.Ports" Version="9.0.0" />

<!-- 日志 -->
<PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />

<!-- 依赖注入 -->
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
```

## 🎯 核心功能实现

### 1. SerialPortService

负责串口通信的核心服务:
- 串口枚举和检测
- 多串口并发管理
- 数据收发
- 自动重连机制
- 统计信息收集

```csharp
// 打开串口示例
var config = new SerialPortConfig
{
    PortName = "COM3",
    BaudRate = 115200
};
await serialPortService.OpenPortAsync(config);
```

### 2. LogFilterService

实时日志过滤服务:
- 文本匹配
- 正则表达式过滤
- 日志级别筛选
- 高亮显示

```csharp
// 添加过滤规则示例
var filter = new FilterRule
{
    Name = "错误过滤",
    Type = FilterType.LogLevel,
    LogLevel = LogLevel.Error
};
logFilterService.AddFilter(filter);
```

## 🔧 配置说明

### 串口配置

```csharp
public class SerialPortConfig
{
    public string PortName { get; set; } = "COM3";
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;
    public bool AutoReconnect { get; set; } = true;
}
```

### 日志配置

日志文件位置: `Logs/serialport-{Date}.log`

可以在 `App.xaml.cs` 中修改 Serilog 配置:

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(
        path: "Logs/serialport-.log",
        rollingInterval: RollingInterval.Day)
    .CreateLogger();
```

## 📊 性能优化

1. **日志数量限制**: 每个串口最多保留 10,000 条日志
2. **异步数据处理**: 所有IO操作均使用异步模式
3. **虚拟化列表**: 使用 ItemsRepeater 实现高性能日志显示
4. **循环缓冲区**: 避免内存无限增长

## 🐛 故障排除

### 常见问题

#### 1. 串口打开失败

**原因**: 串口被其他程序占用
**解决**: 关闭其他串口调试工具

#### 2. 无法发送数据

**原因**: 串口未正确连接
**解决**: 检查串口连接状态和配置

#### 3. 日志显示延迟

**原因**: 过滤规则过于复杂
**解决**: 简化正则表达式或减少过滤规则数量

## 📝 开发指南

### 添加新功能

1. 在 `Services/` 目录创建服务接口和实现
2. 在 `App.xaml.cs` 中注册服务:
   ```csharp
   services.AddSingleton<IYourService, YourService>();
   ```
3. 在 ViewModel 中注入使用

### 代码规范

- 使用 C# 12 语法特性
- 遵循 MVVM 模式
- 所有公共方法添加 XML 文档注释
- 异步方法命名以 `Async` 结尾

## 🤝 贡献

欢迎提交 Issue 和 Pull Request!

## 📄 许可证

MIT License

## 👥 联系方式

- **项目地址**: [GitHub](https://github.com/yourusername/SerialPortTool)
- **问题反馈**: [Issues](https://github.com/yourusername/SerialPortTool/issues)

---

**注意**: 本项目仅用于学习和开发目的，生产环境使用请充分测试。