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