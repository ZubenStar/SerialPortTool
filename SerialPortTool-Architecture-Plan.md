# 串口工具应用架构设计方案

> **项目名称**: 多串口实时监控工具 (Multi-Port Serial Monitor)
> 
> **技术栈**: WinUI 3 + .NET 9 + MVVM
> 
> **创建日期**: 2025-11-27

---

## 📋 需求分析

### 核心功能需求

#### 1️⃣ 基础串口功能
- ✅ 串口扫描和自动检测
- ✅ 串口连接/断开
- ✅ 波特率、数据位、停止位、校验位配置
- ✅ 数据发送(文本/十六进制)
- ✅ 数据接收和显示
- ✅ 自动重连机制
- ✅ 发送历史记录
- ✅ 数据保存(导出log)

#### 2️⃣ 创新功能(核心差异化)
- 🌟 **多串口同时监控**: 支持同时打开和监控多个串口
- 🌟 **实时日志筛选**: 支持正则表达式、关键字、日志级别过滤
- 🌟 **智能日志分析**: 错误/警告高亮显示
- 🌟 **日志对比视图**: 多串口日志并排对比
- 🌟 **数据统计**: 实时统计收发字节数、错误率
- 🌟 **自定义命令集**: 快捷发送常用命令
- 🌟 **脚本自动化**: 支持自动化测试脚本

#### 3️⃣ 高级功能
- 📊 数据可视化(波形图、统计图表)
- 🎨 语法高亮(JSON、XML等格式)
- 📝 自定义协议解析
- ⏱️ 定时发送
- 🔔 关键字触发通知
- 💾 会话保存和恢复

---

## 🏗️ 系统架构设计

### 整体架构图

```
┌─────────────────────────────────────────────────────────┐
│                   Presentation Layer                     │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │ MainWindow   │  │ PortManager  │  │ LogFilter    │  │
│  │ (WinUI 3)    │  │ View         │  │ View         │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────┘
                          ↕ Data Binding
┌─────────────────────────────────────────────────────────┐
│                   ViewModel Layer (MVVM)                 │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │ MainViewModel│  │ PortViewModel│  │FilterViewModel  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────┘
                          ↕ Business Logic
┌─────────────────────────────────────────────────────────┐
│                   Service Layer                          │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │SerialPort    │  │LogFilter     │  │DataExport    │  │
│  │Service       │  │Service       │  │Service       │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │Command       │  │Notification  │  │Session       │  │
│  │Manager       │  │Service       │  │Manager       │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────┘
                          ↕ Hardware Access
┌─────────────────────────────────────────────────────────┐
│                   Infrastructure Layer                   │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │System.IO     │  │File System   │  │Configuration │  │
│  │.Ports        │  │              │  │              │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────┘
```

### 项目结构

```
SerialPortTool/
├── SerialPortTool/                    # 主应用项目
│   ├── App.xaml
│   ├── App.xaml.cs
│   ├── Package.appxmanifest
│   │
│   ├── Views/                         # 视图层
│   │   ├── MainWindow.xaml            # 主窗口
│   │   ├── Controls/
│   │   │   ├── PortConfigControl.xaml     # 串口配置控件
│   │   │   ├── LogViewControl.xaml        # 日志显示控件
│   │   │   ├── FilterPanelControl.xaml    # 过滤器面板
│   │   │   └── CommandPanelControl.xaml   # 命令面板
│   │   └── Dialogs/
│   │       ├── PortSettingsDialog.xaml
│   │       └── ExportDialog.xaml
│   │
│   ├── ViewModels/                    # 视图模型层
│   │   ├── MainViewModel.cs
│   │   ├── PortViewModel.cs           # 单个串口的ViewModel
│   │   ├── LogFilterViewModel.cs
│   │   └── CommandManagerViewModel.cs
│   │
│   ├── Models/                        # 数据模型
│   │   ├── SerialPortConfig.cs        # 串口配置
│   │   ├── LogEntry.cs                # 日志条目
│   │   ├── FilterRule.cs              # 过滤规则
│   │   └── CommandPreset.cs           # 预设命令
│   │
│   ├── Services/                      # 业务服务
│   │   ├── ISerialPortService.cs
│   │   ├── SerialPortService.cs       # 串口通信服务
│   │   ├── ILogFilterService.cs
│   │   ├── LogFilterService.cs        # 日志过滤服务
│   │   ├── IDataExportService.cs
│   │   ├── DataExportService.cs       # 数据导出服务
│   │   ├── ICommandManagerService.cs
│   │   ├── CommandManagerService.cs   # 命令管理服务
│   │   ├── ISessionService.cs
│   │   ├── SessionService.cs          # 会话管理服务
│   │   └── INotificationService.cs
│   │       └── NotificationService.cs # 通知服务
│   │
│   ├── Helpers/                       # 辅助类
│   │   ├── SerialPortHelper.cs        # 串口辅助方法
│   │   ├── DataFormatHelper.cs        # 数据格式转换
│   │   ├── RegexHelper.cs             # 正则表达式工具
│   │   └── ColorHelper.cs             # 日志颜色管理
│   │
│   ├── Converters/                    # XAML转换器
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── LogLevelToColorConverter.cs
│   │   └── BytesToStringConverter.cs
│   │
│   ├── Styles/                        # 样式资源
│   │   ├── Colors.xaml
│   │   ├── Brushes.xaml
│   │   └── ControlStyles.xaml
│   │
│   └── Assets/                        # 静态资源
│       ├── Icons/
│       └── Images/
│
├── SerialPortTool.Core/               # 核心业务逻辑库
│   ├── Enums/
│   │   ├── LogLevel.cs
│   │   ├── DataFormat.cs
│   │   └── ConnectionState.cs
│   ├── Extensions/
│   │   └── StringExtensions.cs
│   └── Constants/
│       └── AppConstants.cs
│
└── SerialPortTool.Tests/              # 单元测试
    ├── Services/
    └── ViewModels/
```

---

## 🧩 核心模块设计

### 1. 串口服务 (SerialPortService)

**职责**:
- 串口枚举和检测
- 串口连接/断开管理
- 数据收发
- 错误处理和自动重连

**接口设计**:

```csharp
public interface ISerialPortService
{
    // 串口管理
    Task<IEnumerable<string>> GetAvailablePortsAsync();
    Task<bool> OpenPortAsync(SerialPortConfig config);
    Task ClosePortAsync(string portName);
    bool IsPortOpen(string portName);
    
    // 数据收发
    Task SendDataAsync(string portName, byte[] data);
    Task SendTextAsync(string portName, string text, Encoding encoding);
    
    // 事件
    event EventHandler<DataReceivedEventArgs> DataReceived;
    event EventHandler<PortStateChangedEventArgs> PortStateChanged;
    event EventHandler<ErrorEventArgs> ErrorOccurred;
    
    // 统计信息
    PortStatistics GetStatistics(string portName);
}

public class SerialPortConfig
{
    public string PortName { get; set; }
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Parity Parity { get; set; } = Parity.None;
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectInterval { get; set; } = 3000; // ms
}
```

### 2. 日志过滤服务 (LogFilterService)

**职责**:
- 实时日志过滤
- 正则表达式匹配
- 关键字高亮
- 日志级别筛选

**接口设计**:

```csharp
public interface ILogFilterService
{
    // 过滤规则管理
    void AddFilter(FilterRule rule);
    void RemoveFilter(Guid filterId);
    void ClearFilters();
    IEnumerable<FilterRule> GetActiveFilters();
    
    // 过滤执行
    bool ShouldDisplay(LogEntry entry);
    IEnumerable<LogEntry> FilterLogs(IEnumerable<LogEntry> logs);
    
    // 高亮管理
    IEnumerable<HighlightSpan> GetHighlights(string text);
}

public class FilterRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
    public FilterType Type { get; set; } // Text, Regex, LogLevel
    public string Pattern { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsInclude { get; set; } = true; // true=包含, false=排除
    public LogLevel? LogLevel { get; set; }
}

public enum FilterType
{
    Text,        // 文本匹配
    Regex,       // 正则表达式
    LogLevel,    // 日志级别
    PortName     // 串口名称
}
```

### 3. 命令管理服务 (CommandManagerService)

**职责**:
- 预设命令管理
- 命令历史记录
- 定时发送
- 脚本执行

**接口设计**:

```csharp
public interface ICommandManagerService
{
    // 预设命令
    void SaveCommand(CommandPreset command);
    void DeleteCommand(Guid commandId);
    IEnumerable<CommandPreset> GetAllCommands();
    
    // 命令历史
    void AddToHistory(string command, string portName);
    IEnumerable<string> GetCommandHistory(int count = 20);
    void ClearHistory();
    
    // 定时发送
    Task StartScheduledSendAsync(ScheduledCommand command);
    void StopScheduledSend(Guid commandId);
}

public class CommandPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
    public string Description { get; set; }
    public string Command { get; set; }
    public DataFormat Format { get; set; } = DataFormat.Text;
    public bool AppendNewLine { get; set; } = true;
    public string Category { get; set; }
}

public enum DataFormat
{
    Text,           // 文本
    Hex,            // 十六进制
    Binary,         // 二进制
    Base64          // Base64编码
}
```

### 4. 会话管理服务 (SessionService)

**职责**:
- 保存和恢复串口配置
- 日志持久化
- 工作区管理

**接口设计**:

```csharp
public interface ISessionService
{
    // 会话管理
    Task SaveSessionAsync(SessionData session);
    Task<SessionData> LoadSessionAsync(string sessionName);
    Task<IEnumerable<string>> GetAllSessionsAsync();
    
    // 自动保存
    void EnableAutoSave(bool enabled);
    
    // 日志持久化
    Task ExportLogsAsync(string filePath, IEnumerable<LogEntry> logs, ExportFormat format);
}

public class SessionData
{
    public string Name { get; set; }
    public DateTime Created { get; set; }
    public List<SerialPortConfig> PortConfigs { get; set; }
    public List<FilterRule> FilterRules { get; set; }
    public List<CommandPreset> Commands { get; set; }
}
```

---

## 🎨 UI设计方案

### 主窗口布局

```
┌─────────────────────────────────────────────────────────────┐
│ 串口工具 - Multi-Port Serial Monitor            [- □ X]     │
├─────────────────────────────────────────────────────────────┤
│ 文件  编辑  视图  工具  帮助                                  │
├─────────────────────────────────────────────────────────────┤
│ ┌─侧边栏───┐ ┌─────────主内容区─────────────────────┐        │
│ │          │ │ ┌─Tab: COM3 (115200)─┐              │        │
│ │ 📡 串口   │ │ │ ┌────日志显示─────┐ │              │        │
│ │  ├ COM3  │ │ │ │[INFO] 2025-...   │ │              │        │
│ │  ├ COM5  │ │ │ │[DEBUG] Data...   │ │              │        │
│ │  └ COM8  │ │ │ │[ERROR] Failed... │ │              │        │
│ │          │ │ │ └───────────────────┘ │              │        │
│ │ 🔍 过滤器 │ │ │ ┌───发送区────────┐ │              │        │
│ │  ⊕ 新增   │ │ │ │ [文本框]         │ │              │        │
│ │          │ │ │ │ [发送] [十六进制]│ │              │        │
│ │ ⚡ 命令   │ │ │ └───────────────────┘ │              │        │
│ │  ├ 重启  │ │ └─────────────────────────┘              │        │
│ │  ├ 查询  │ │ ┌─统计信息─────────────────┐            │        │
│ │  └ 配置  │ │ │ 📊 接收: 1.2MB 发送:256KB│            │        │
│ │          │ │ └─────────────────────────┘            │        │
│ │ 📊 统计   │ │                                         │        │
│ └──────────┘ └─────────────────────────────────────────┘        │
├─────────────────────────────────────────────────────────────┤
│ 状态栏: 已连接3个串口 | 接收速率: 15.2KB/s | 过滤已启用        │
└─────────────────────────────────────────────────────────────┘
```

### 关键UI组件

#### 1. 串口卡片 (PortCard)

```xml
<UserControl>
    <Grid Background="{ThemeResource CardBackgroundFillColorDefault}">
        <!-- 串口名称和状态 -->
        <TextBlock Text="{x:Bind PortName}" FontWeight="Bold" />
        <FontIcon Glyph="&#xF3A1;" Foreground="{x:Bind StatusColor}" />
        
        <!-- 快速操作按钮 -->
        <Button Content="连接" Command="{x:Bind ConnectCommand}" />
        <Button Content="设置" Command="{x:Bind SettingsCommand}" />
        
        <!-- 统计信息 -->
        <TextBlock Text="{x:Bind Statistics.ReceivedBytes}" />
    </Grid>
</UserControl>
```

#### 2. 日志查看器 (LogViewer)

特性:
- 虚拟化滚动(处理大量日志)
- 语法高亮
- 时间戳显示
- 日志级别颜色编码
- 搜索和定位

```xml
<ItemsRepeater ItemsSource="{x:Bind FilteredLogs}">
    <ItemsRepeater.ItemTemplate>
        <DataTemplate>
            <Grid>
                <TextBlock Text="{Binding Timestamp}" />
                <TextBlock Text="{Binding Level}" 
                          Foreground="{Binding Level, Converter={StaticResource LogLevelToColorConverter}}" />
                <TextBlock Text="{Binding Content}" />
            </Grid>
        </DataTemplate>
    </ItemsRepeater.ItemTemplate>
</ItemsRepeater>
```

#### 3. 过滤器面板 (FilterPanel)

```xml
<StackPanel>
    <!-- 快速过滤输入 -->
    <TextBox PlaceholderText="搜索或输入正则表达式..." />
    
    <!-- 日志级别筛选 -->
    <CheckBox Content="ERROR" />
    <CheckBox Content="WARN" />
    <CheckBox Content="INFO" />
    <CheckBox Content="DEBUG" />
    
    <!-- 自定义过滤规则列表 -->
    <ListView ItemsSource="{x:Bind FilterRules}" />
</StackPanel>
```

---

## 📦 技术栈和依赖

### NuGet包

```xml
<ItemGroup>
  <!-- WinUI 3 和 MVVM -->
  <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.6.0" />
  <PackageReference Include="CommunityToolkit.Mvvm" Version="8.2.2" />
  <PackageReference Include="WinUIEx" Version="2.3.4" />
  
  <!-- 串口通信 -->
  <PackageReference Include="System.IO.Ports" Version="9.0.0" />
  
  <!-- 数据处理 -->
  <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  <PackageReference Include="CsvHelper" Version="30.0.1" />
  
  <!-- UI增强 -->
  <PackageReference Include="CommunityToolkit.WinUI.Controls.DataGrid" Version="8.0.8" />
  
  <!-- 日志 -->
  <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
  
  <!-- 依赖注入 -->
  <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
  <PackageReference Include="Microsoft.Extensions.Hosting" Version="9.0.0" />
</ItemGroup>
```

---

## 🔄 数据流设计

### 串口数据接收流程

```
Hardware Serial Port
        ↓
SerialPortService (数据接收)
        ↓
DataReceivedEvent
        ↓
PortViewModel (处理接收数据)
        ↓
LogFilterService (应用过滤规则)
        ↓
ObservableCollection<LogEntry> (UI更新)
        ↓
LogViewControl (显示)
```

### 过滤器应用流程

```
用户输入过滤条件
        ↓
LogFilterViewModel
        ↓
LogFilterService.AddFilter()
        ↓
触发FilterChanged事件
        ↓
PortViewModel重新应用过滤
        ↓
UI更新显示
```

---

## ⚡ 性能优化策略

### 1. 大量日志处理

```csharp
// 使用循环缓冲区限制日志数量
public class CircularBuffer<T>
{
    private readonly T[] _buffer;
    private int _start;
    private int _end;
    private int _size;

    public CircularBuffer(int capacity)
    {
        _buffer = new T[capacity];
    }

    public void Add(T item)
    {
        _buffer[_end] = item;
        _end = (_end + 1) % _buffer.Length;
        
        if (_size == _buffer.Length)
            _start = (_start + 1) % _buffer.Length;
        else
            _size++;
    }
}
```

### 2. UI虚拟化

```xml
<!-- 使用ItemsRepeater实现虚拟化 -->
<ScrollViewer>
    <ItemsRepeater ItemsSource="{x:Bind Logs}"
                   VirtualizingLayout="{StaticResource StackLayout}" />
</ScrollViewer>
```

### 3. 异步数据处理

```csharp
// 后台线程处理数据
private async Task ProcessReceivedDataAsync(byte[] data)
{
    await Task.Run(() =>
    {
        var logEntry = ParseData(data);
        
        // 切换到UI线程更新
        DispatcherQueue.TryEnqueue(() =>
        {
            Logs.Add(logEntry);
        });
    });
}
```

### 4. 日志写入优化

```csharp
// 批量写入文件
private readonly BufferBlock<LogEntry> _logBuffer = new();

private async Task LogWriterLoopAsync()
{
    var batch = new List<LogEntry>();
    
    while (!_cancellationToken.IsCancellationRequested)
    {
        await Task.Delay(1000); // 每秒写入一次
        
        while (_logBuffer.TryReceive(out var log))
            batch.Add(log);
        
        if (batch.Any())
        {
            await File.AppendAllLinesAsync(_logFilePath, 
                batch.Select(l => l.ToString()));
            batch.Clear();
        }
    }
}
```

---

## 🔐 错误处理和可靠性

### 串口异常处理

```csharp
public class SerialPortService
{
    private async Task HandlePortErrorAsync(string portName, Exception ex)
    {
        _logger.LogError(ex, "Port {PortName} error", portName);
        
        // 发送错误事件
        ErrorOccurred?.Invoke(this, new ErrorEventArgs(portName, ex));
        
        // 自动重连逻辑
        if (_configs[portName].AutoReconnect)
        {
            await Task.Delay(_configs[portName].ReconnectInterval);
            await TryReconnectAsync(portName);
        }
    }
}
```

### 数据完整性保证

```csharp
// CRC校验
public class DataValidator
{
    public static bool ValidateCRC(byte[] data)
    {
        // CRC16校验实现
        return true;
    }
}
```

---

## 📝 开发实施计划

### Phase 1: 基础功能 (2-3天)

```
[-] 搭建项目基础架构
[ ] 实现SerialPortService
[ ] 实现基本UI布局
[ ] 单串口连接和数据收发
[ ] 基本日志显示
```

### Phase 2: 核心功能 (3-4天)

```
[ ] 多串口同时管理
[ ] LogFilterService实现
[ ] 实时过滤功能
[ ] 命令管理和历史记录
[ ] 数据格式支持(Text/Hex)
```

### Phase 3: 高级功能 (2-3天)

```
[ ] 会话保存和恢复
[ ] 数据导出功能
[ ] 统计信息显示
[ ] 自定义命令集
[ ] 定时发送
```

### Phase 4: 优化和测试 (2天)

```
[ ] 性能优化
[ ] UI美化
[ ] 异常处理完善
[ ] 单元测试
[ ] 用户手册
```

**总计开发时间: 9-12天**

---

## 🎯 技术亮点

1. **多串口并发管理**: 使用Task异步模型,每个串口独立线程处理
2. **实时过滤零延迟**: 使用Rx响应式编程,流式处理日志
3. **高性能日志显示**: ItemsRepeater虚拟化,支持百万级日志
4. **灵活的过滤系统**: 支持正则表达式、逻辑组合、自定义规则
5. **智能错误恢复**: 自动重连、数据缓冲、错误上报机制
6. **现代化UI**: Fluent Design、云母材质、流畅动画

---

## 📚 下一步行动

我已完成串口工具的完整架构设计,包括:

✅ 需求分析(基础功能 + 创新功能)
✅ 系统架构设计(分层架构 + 项目结构)
✅ 核心模块设计(4大核心服务 + 接口定义)
✅ UI设计方案(布局 + 关键组件)
✅ 技术栈选型(NuGet包列表)
✅ 数据流设计
✅ 性能优化策略
✅ 开发实施计划

现在您可以:

1. **审阅方案**: 确认架构设计是否满足需求
2. **开始开发**: 切换到Code模式,我将帮您逐步实现每个模块
3. **调整需求**: 如果需要增加或修改功能,我可以更新设计方案

是否需要我切换到Code模式开始实际开发? 🚀