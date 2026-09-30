using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SerialPortTool.Core.Enums;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 串口服务接口
/// </summary>
public interface ISerialPortService
{
    /// <summary>
    /// 数据接收事件
    /// </summary>
    event EventHandler<DataReceivedEventArgs>? DataReceived;

    /// <summary>
    /// 串口状态变化事件
    /// </summary>
    event EventHandler<PortStateChangedEventArgs>? PortStateChanged;

    /// <summary>
    /// 错误发生事件
    /// </summary>
    event EventHandler<ErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// 获取可用的串口列表
    /// </summary>
    Task<IEnumerable<string>> GetAvailablePortsAsync();

    /// <summary>
    /// 打开串口
    /// </summary>
    Task<bool> OpenPortAsync(SerialPortConfig config);

    /// <summary>
    /// 关闭串口
    /// </summary>
    Task ClosePortAsync(string portName);

    /// <summary>
    /// 打开所有可用串口
    /// </summary>
    /// <param name="defaultConfig">各串口共用的默认参数。</param>
    /// <param name="encodingNameForPort">
    /// 逐端口的文本编码（端口名 → 编码名）。返回 null 的端口用 <paramref name="defaultConfig"/> 里的值；
    /// 传入 null 则全部用默认值。之所以是回调而不是打开之后的补充赋值：编码必须在端口打开<em>之前</em>
    /// 登记，否则首批数据会先按上一个编码解码（对 GB18030 设备就是开头几行乱码，且可能被校验层丢弃）。
    /// </param>
    Task<int> OpenAllPortsAsync(SerialPortConfig defaultConfig, Func<string, string?>? encodingNameForPort = null);

    /// <summary>
    /// 关闭所有串口
    /// </summary>
    Task CloseAllPortsAsync();

    /// <summary>
    /// 检查串口是否已打开
    /// </summary>
    bool IsPortOpen(string portName);

    /// <summary>
    /// 发送二进制数据
    /// </summary>
    Task SendDataAsync(string portName, byte[] data);

    /// <summary>
    /// 发送文本数据
    /// </summary>
    Task SendTextAsync(string portName, string text, System.Text.Encoding? encoding = null);

    /// <summary>
    /// 设置某个串口的文本编码：接收解码、数据校验与文本发送都用它。
    /// </summary>
    /// <remarks>
    /// 编码是解码/显示层的事，所以它对本就打开的串口可以即时生效，不需要关闭重开——这与停止位、校验位、
    /// 流控等只能在打开时生效的参数不同。名称由 <c>Helpers.SerialEncodings</c> 规范化，无法识别的名称
    /// 会退回 UTF-8。
    /// </remarks>
    void SetPortTextEncoding(string portName, string encodingName);

    /// <summary>
    /// 取某个串口当前的文本编码名称；没有记录时返回 UTF-8。
    /// </summary>
    string GetPortTextEncodingName(string portName);

    /// <summary>
    /// 获取串口配置
    /// </summary>
    SerialPortConfig? GetPortConfig(string portName);

    /// <summary>
    /// 获取所有打开的串口名称
    /// </summary>
    IEnumerable<string> GetOpenPorts();

    /// <summary>
    /// 获取串口统计信息
    /// </summary>
    PortStatistics GetStatistics(string portName);
}

/// <summary>
/// 数据接收事件参数
/// </summary>
public class DataReceivedEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// 串口状态变化事件参数
/// </summary>
public class PortStateChangedEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public ConnectionState OldState { get; set; }
    public ConnectionState NewState { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// 错误事件参数
/// </summary>
public class ErrorEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public Exception Exception { get; set; } = new Exception();
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

/// <summary>
/// 波特率检测请求事件参数
/// </summary>
public class BaudRateDetectionRequestedEventArgs : EventArgs
{
    public string PortName { get; set; } = string.Empty;
    public int CurrentBaudRate { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}