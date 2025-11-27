using System;
using System.Collections.Generic;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 日志过滤服务接口
/// </summary>
public interface ILogFilterService
{
    /// <summary>
    /// 添加过滤规则
    /// </summary>
    void AddFilter(FilterRule rule);

    /// <summary>
    /// 移除过滤规则
    /// </summary>
    void RemoveFilter(Guid filterId);

    /// <summary>
    /// 更新过滤规则
    /// </summary>
    void UpdateFilter(FilterRule rule);

    /// <summary>
    /// 清空所有过滤规则
    /// </summary>
    void ClearFilters();

    /// <summary>
    /// 获取所有激活的过滤规则
    /// </summary>
    IEnumerable<FilterRule> GetActiveFilters();

    /// <summary>
    /// 获取所有过滤规则
    /// </summary>
    IEnumerable<FilterRule> GetAllFilters();

    /// <summary>
    /// 判断日志是否应该显示
    /// </summary>
    bool ShouldDisplay(LogEntry entry);

    /// <summary>
    /// 批量过滤日志
    /// </summary>
    IEnumerable<LogEntry> FilterLogs(IEnumerable<LogEntry> logs);

    /// <summary>
    /// 获取文本中的高亮区域
    /// </summary>
    IEnumerable<HighlightSpan> GetHighlights(string text);

    /// <summary>
    /// 启用/禁用过滤规则
    /// </summary>
    void SetFilterEnabled(Guid filterId, bool enabled);

    /// <summary>
    /// 过滤器变化事件
    /// </summary>
    event EventHandler? FiltersChanged;
}

/// <summary>
/// 高亮区域
/// </summary>
public class HighlightSpan
{
    /// <summary>
    /// 起始位置
    /// </summary>
    public int Start { get; set; }

    /// <summary>
    /// 长度
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// 高亮文本
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// 规则ID
    /// </summary>
    public Guid RuleId { get; set; }
}