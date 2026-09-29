using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;

namespace SerialPortTool.Models;

/// <summary>
/// One transient notification in the shell's bottom-right stack.
/// </summary>
/// <remarks>
/// Carries <see cref="InfoBarSeverity"/> directly rather than an app-local enum plus a converter:
/// this is a WinUI-only application, the value has exactly one consumer (the <c>InfoBar</c> in the
/// <c>MainWindow</c> notification host), and a converter would add a lookup per bound item for no
/// gain. Introduce the enum — and the converter — only if a second, non-<c>InfoBar</c> consumer
/// ever appears.
/// </remarks>
public partial class NotificationItem : ObservableObject
{
    /// <summary>The notification text.</summary>
    public required string Message { get; init; }

    /// <summary>Optional bold headline shown above <see cref="Message"/>.</summary>
    public string? Title { get; init; }

    /// <summary>Drives the icon, the accent stripe and the bar's edge colour.</summary>
    public InfoBarSeverity Severity { get; init; } = InfoBarSeverity.Informational;

    /// <summary>
    /// <c>true</c> while the bar is on screen. Two-way with the <c>InfoBar</c>'s own
    /// <c>IsOpen</c>, so the close button is what removes the entry: the service watches this
    /// property and drops the item, rather than the view reaching into the service.
    /// </summary>
    [ObservableProperty]
    private bool _isOpen = true;
}
