# WinUI 3 开发环境配置完整指南

> **目标**: 安装并配置 Visual Studio 2022 和 .NET SDK,准备开发串口工具应用
> 
> **预计时间**: 30-60分钟(取决于网速)

---

## 📦 第一步: 安装 Visual Studio 2022

### 1.1 下载 Visual Studio 2022

访问官方下载页面:
```
https://visualstudio.microsoft.com/zh-hans/downloads/
```

**推荐版本**: Visual Studio 2022 Community (免费)

**直接下载链接**:
- [Visual Studio 2022 Community](https://visualstudio.microsoft.com/thank-you-downloading-visual-studio/?sku=Community&channel=Release&version=VS2022)

### 1.2 运行安装程序

1. 运行下载的 `VisualStudioSetup.exe`
2. 在 Visual Studio Installer 中点击"安装"

### 1.3 选择工作负载 (关键步骤)

**必选工作负载**:

✅ **.NET桌面开发**
   - 包含 .NET 9.0 SDK
   - 包含 WPF 和 WinForms 设计器
   
✅ **通用Windows平台开发**
   - 包含 Windows SDK
   - 包含 UWP 工具
   
✅ **Windows应用开发**
   - 包含 Windows App SDK
   - 包含 WinUI 3 模板

### 1.4 选择单个组件 (可选但推荐)

在"单个组件"标签页中,搜索并选择:

✅ `.NET 9.0 Runtime`
✅ `Windows 11 SDK (10.0.22621.0)`
✅ `MSIX Packaging Tools`
✅ `Git for Windows`

### 1.5 开始安装

1. 点击右下角"安装"按钮
2. 等待安装完成(大约需要下载 10-20GB)
3. 安装完成后重启计算机

---

## ✅ 第二步: 验证安装

### 2.1 验证 .NET SDK

打开 **命令提示符** 或 **PowerShell**,运行:

```powershell
dotnet --version
```

**期望输出**: `9.0.x` (例如: 9.0.0)

如果显示版本号,说明 .NET SDK 安装成功! ✅

### 2.2 验证 Visual Studio

1. 打开 Visual Studio 2022
2. 点击"创建新项目"
3. 在搜索框中输入 "WinUI"
4. 应该能看到 "空白应用,打包(WinUI 3 in Desktop)" 模板

如果能看到 WinUI 3 模板,说明安装成功! ✅

---

## 🚀 第三步: 创建第一个 WinUI 3 项目

### 方法 A: 使用 Visual Studio (推荐新手)

1. 打开 Visual Studio 2022
2. 点击"创建新项目"
3. 搜索 "WinUI"
4. 选择 "空白应用,打包(WinUI 3 in Desktop)"
5. 点击"下一步"
6. 项目名称: `SerialPortTool`
7. 位置: `d:\Workspace\Playground\SerialPortTool`
8. 点击"创建"
9. 选择目标版本:
   - 目标版本: Windows 11, version 22H2 (build 22621)
   - 最低版本: Windows 10, version 1809 (build 17763)
10. 点击"确定"

**等待项目创建完成,首次创建可能需要几分钟。**

### 方法 B: 使用命令行 (推荐熟练开发者)

打开 PowerShell,运行:

```powershell
# 进入工作目录
cd d:\Workspace\Playground

# 安装 WinUI 3 模板(首次需要)
dotnet new install Microsoft.WindowsAppSDK.Templates

# 创建项目
dotnet new winui -n SerialPortTool

# 进入项目目录
cd SerialPortTool

# 构建项目验证
dotnet build
```

如果构建成功,说明项目创建成功! ✅

---

## 🔧 第四步: 安装必要的 NuGet 包

### 使用 Visual Studio Package Manager

1. 在 Visual Studio 中打开 `SerialPortTool` 项目
2. 右键点击项目 → "管理 NuGet 程序包"
3. 点击"浏览"标签
4. 搜索并安装以下包:

```
CommunityToolkit.Mvvm (版本 8.2.2 或更高)
WinUIEx (版本 2.3.4 或更高)
System.IO.Ports (版本 9.0.0 或更高)
Microsoft.Extensions.DependencyInjection (版本 9.0.0 或更高)
Microsoft.Extensions.Hosting (版本 9.0.0 或更高)
Serilog.Sinks.File (版本 5.0.0 或更高)
CommunityToolkit.WinUI.Controls.DataGrid (版本 8.0.8 或更高)
```

### 或使用命令行

在项目目录中打开 PowerShell,运行:

```powershell
dotnet add package CommunityToolkit.Mvvm
dotnet add package WinUIEx
dotnet add package System.IO.Ports
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Serilog.Sinks.File
dotnet add package CommunityToolkit.WinUI.Controls.DataGrid
dotnet add package Newtonsoft.Json
```

---

## 📝 第五步: 配置项目设置

### 5.1 编辑 .csproj 文件

在 Visual Studio 中:
1. 在解决方案资源管理器中右键项目
2. 选择"编辑项目文件"

确保包含以下配置:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows10.0.22621.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>SerialPortTool</RootNamespace>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <Platforms>x64;ARM64</Platforms>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <PublishProfile>win-$(Platform).pubxml</PublishProfile>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <Nullable>enable</Nullable>
    <LangVersion>12</LangVersion>
  </PropertyGroup>
</Project>
```

### 5.2 运行测试

在 Visual Studio 中:
1. 按 `F5` 或点击"开始调试"
2. 应该看到一个空白的 WinUI 3 窗口打开

如果窗口成功打开,恭喜!环境配置完成! 🎉

---

## 🐛 常见问题解决

### 问题 1: "找不到 WinUI 3 模板"

**解决方案**:
1. 打开 Visual Studio Installer
2. 点击"修改"
3. 确保勾选了"Windows应用开发"工作负载
4. 点击"修改"重新安装

### 问题 2: "无法加载 Windows App SDK"

**解决方案**:
```powershell
# 手动安装 Windows App SDK
winget install Microsoft.WindowsAppRuntime.1.6
```

### 问题 3: "Build 失败,找不到 SDK"

**解决方案**:
1. 打开 Visual Studio Installer
2. 点击"修改" → "单个组件"
3. 搜索 "Windows 11 SDK"
4. 确保安装了 `Windows 11 SDK (10.0.22621.0)`

### 问题 4: "dotnet 命令找不到"

**解决方案**:
1. 重启计算机
2. 手动添加 .NET 到环境变量:
   - 右键"此电脑" → "属性" → "高级系统设置"
   - "环境变量" → "系统变量" → "Path"
   - 添加: `C:\Program Files\dotnet\`
3. 重新打开命令行

---

## ✅ 安装完成检查清单

完成以下检查,确保环境配置正确:

- [ ] Visual Studio 2022 已安装
- [ ] 包含 .NET桌面开发 工作负载
- [ ] 包含 Windows应用开发 工作负载
- [ ] `dotnet --version` 显示 9.0.x
- [ ] 能在 VS 中看到 WinUI 3 项目模板
- [ ] 成功创建 WinUI 3 项目
- [ ] 所有 NuGet 包已安装
- [ ] 项目能成功构建(F5运行)
- [ ] 空白窗口能正常打开

**如果所有项目都打勾,您已准备好开始开发! 🚀**

---

## 📞 下一步

环境配置完成后,请告诉我:

1. **已完成安装**: 我将开始创建串口工具的项目文件和代码
2. **遇到问题**: 描述具体错误信息,我将帮助解决
3. **需要演示**: 我可以提供更详细的截图说明

**准备好后,我们将开始实际编码! 💻**

---

## 📚 相关资源

- [Visual Studio 2022 官方文档](https://learn.microsoft.com/zh-cn/visualstudio/)
- [WinUI 3 入门教程](https://learn.microsoft.com/zh-cn/windows/apps/winui/winui3/)
- [Windows App SDK 文档](https://learn.microsoft.com/zh-cn/windows/apps/windows-app-sdk/)
- [.NET 9 下载页面](https://dotnet.microsoft.com/zh-cn/download/dotnet/9.0)