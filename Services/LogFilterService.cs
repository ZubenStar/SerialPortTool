using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SerialPortTool.Core.Enums;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 日志过滤服务实现
/// </summary>
public class LogFilterService : ILogFilterService
{
    private readonly ILogger<LogFilterService> _logger;
    private readonly List<FilterRule> _filters = new();
    private readonly object _lock = new();

    public event EventHandler? FiltersChanged;

    public LogFilterService(ILogger<LogFilterService> logger)
    {
        _logger = logger;
    }

    public void AddFilter(FilterRule rule)
    {
        lock (_lock)
        {
            _filters.Add(rule);
            _logger.LogInformation("Filter added: {FilterName}", rule.Name);
            OnFiltersChanged();
        }
    }

    public void RemoveFilter(Guid filterId)
    {
        lock (_lock)
        {
            var removed = _filters.RemoveAll(f => f.Id == filterId);
            if (removed > 0)
            {
                _logger.LogInformation("Filter removed: {FilterId}", filterId);
                OnFiltersChanged();
            }
        }
    }

    public void UpdateFilter(FilterRule rule)
    {
        lock (_lock)
        {
            var index = _filters.FindIndex(f => f.Id == rule.Id);
            if (index >= 0)
            {
                _filters[index] = rule;
                _logger.LogInformation("Filter updated: {FilterName}", rule.Name);
                OnFiltersChanged();
            }
        }
    }

    public void ClearFilters()
    {
        lock (_lock)
        {
            _filters.Clear();
            _logger.LogInformation("All filters cleared");
            OnFiltersChanged();
        }
    }

    public IEnumerable<FilterRule> GetActiveFilters()
    {
        lock (_lock)
        {
            return _filters.Where(f => f.IsEnabled).ToList();
        }
    }

    public IEnumerable<FilterRule> GetAllFilters()
    {
        lock (_lock)
        {
            return _filters.ToList();
        }
    }

    public bool ShouldDisplay(LogEntry entry)
    {
        var activeFilters = GetActiveFilters().ToList();
        
        if (!activeFilters.Any())
        {
            return true; // No filters, show everything
        }

        // Check each filter
        foreach (var filter in activeFilters)
        {
            var matches = MatchesFilter(entry, filter);
            
            if (filter.IsInclude)
            {
                // Include mode: if any filter matches, show it
                if (matches) return true;
            }
            else
            {
                // Exclude mode: if any filter matches, hide it
                if (matches) return false;
            }
        }

        // If we have include filters and nothing matched, hide it
        // If we only have exclude filters and nothing matched, show it
        var hasIncludeFilters = activeFilters.Any(f => f.IsInclude);
        return !hasIncludeFilters;
    }

    public IEnumerable<LogEntry> FilterLogs(IEnumerable<LogEntry> logs)
    {
        return logs.Where(ShouldDisplay);
    }

    public IEnumerable<HighlightSpan> GetHighlights(string text)
    {
        var highlights = new List<HighlightSpan>();
        var activeFilters = GetActiveFilters()
            .Where(f => f.Type == FilterType.Text || f.Type == FilterType.Regex)
            .ToList();

        foreach (var filter in activeFilters)
        {
            try
            {
                if (filter.Type == FilterType.Text)
                {
                    var comparison = filter.CaseSensitive 
                        ? StringComparison.Ordinal 
                        : StringComparison.OrdinalIgnoreCase;
                    
                    var index = 0;
                    while ((index = text.IndexOf(filter.Pattern, index, comparison)) >= 0)
                    {
                        highlights.Add(new HighlightSpan
                        {
                            Start = index,
                            Length = filter.Pattern.Length,
                            Text = text.Substring(index, filter.Pattern.Length),
                            RuleId = filter.Id
                        });
                        index += filter.Pattern.Length;
                    }
                }
                else if (filter.Type == FilterType.Regex)
                {
                    var options = filter.CaseSensitive 
                        ? RegexOptions.None 
                        : RegexOptions.IgnoreCase;
                    
                    var regex = new Regex(filter.Pattern, options);
                    var matches = regex.Matches(text);
                    
                    foreach (Match match in matches)
                    {
                        highlights.Add(new HighlightSpan
                        {
                            Start = match.Index,
                            Length = match.Length,
                            Text = match.Value,
                            RuleId = filter.Id
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error applying filter {FilterName}", filter.Name);
            }
        }

        return highlights.OrderBy(h => h.Start).ToList();
    }

    public void SetFilterEnabled(Guid filterId, bool enabled)
    {
        lock (_lock)
        {
            var filter = _filters.FirstOrDefault(f => f.Id == filterId);
            if (filter != null)
            {
                filter.IsEnabled = enabled;
                _logger.LogInformation("Filter {FilterId} enabled: {Enabled}", filterId, enabled);
                OnFiltersChanged();
            }
        }
    }

    private bool MatchesFilter(LogEntry entry, FilterRule filter)
    {
        try
        {
            switch (filter.Type)
            {
                case FilterType.Text:
                    return MatchesText(entry.Content, filter.Pattern, filter.CaseSensitive);

                case FilterType.Regex:
                    return MatchesRegex(entry.Content, filter.Pattern, filter.CaseSensitive);

                case FilterType.PortName:
                    return !string.IsNullOrEmpty(filter.PortName) && 
                           entry.PortName.Equals(filter.PortName, StringComparison.OrdinalIgnoreCase);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error matching filter {FilterName}", filter.Name);
            return false;
        }
    }

    private bool MatchesText(string content, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        
        var comparison = caseSensitive 
            ? StringComparison.Ordinal 
            : StringComparison.OrdinalIgnoreCase;
        
        return content.Contains(pattern, comparison);
    }

    private bool MatchesRegex(string content, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        
        try
        {
            var options = caseSensitive 
                ? RegexOptions.None 
                : RegexOptions.IgnoreCase;
            
            var regex = new Regex(pattern, options);
            return regex.IsMatch(content);
        }
        catch
        {
            return false;
        }
    }

    private void OnFiltersChanged()
    {
        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }
}