using System;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 预设命令模型
/// </summary>
public class CommandPreset
{
    /// <summary>
    /// 命令ID
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 命令名称
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 命令描述
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// 命令内容
    /// </summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// 数据格式
    /// </summary>
    public DataFormat Format { get; set; } = DataFormat.Text;

    /// <summary>
    /// 是否在末尾添加换行符
    /// </summary>
    public bool AppendNewLine { get; set; } = true;

    /// <summary>
    /// 命令分类
    /// </summary>
    public string Category { get; set; } = "Default";

    /// <summary>
    /// 快捷键(可选)
    /// </summary>
    public string? Hotkey { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 使用次数
    /// </summary>
    public int UsageCount { get; set; } = 0;

    /// <summary>
    /// 克隆命令
    /// </summary>
    public CommandPreset Clone()
    {
        return new CommandPreset
        {
            Id = Guid.NewGuid(),
            Name = Name,
            Description = Description,
            Command = Command,
            Format = Format,
            AppendNewLine = AppendNewLine,
            Category = Category,
            Hotkey = Hotkey,
            CreatedAt = DateTime.Now,
            UsageCount = 0
        };
    }
}