using System;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 过滤规则模型
/// </summary>
public class FilterRule
{
    /// <summary>
    /// 规则ID
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 规则名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 过滤器类型
    /// </summary>
    public FilterType Type { get; set; } = FilterType.Text;

    /// <summary>
    /// 匹配模式(文本或正则表达式)
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 是否包含模式(true=包含匹配的, false=排除匹配的)
    /// </summary>
    public bool IsInclude { get; set; } = true;

    /// <summary>
    /// 日志级别过滤(仅当Type=LogLevel时有效)
    /// </summary>
    public LogLevel? LogLevel { get; set; }

    /// <summary>
    /// 串口名称过滤(仅当Type=PortName时有效)
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// 是否区分大小写(仅文本和正则表达式有效)
    /// </summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 克隆规则
    /// </summary>
    public FilterRule Clone()
    {
        return new FilterRule
        {
            Id = Guid.NewGuid(),
            Name = Name,
            Type = Type,
            Pattern = Pattern,
            IsEnabled = IsEnabled,
            IsInclude = IsInclude,
            LogLevel = LogLevel,
            PortName = PortName,
            CaseSensitive = CaseSensitive,
            CreatedAt = DateTime.Now
        };
    }
}