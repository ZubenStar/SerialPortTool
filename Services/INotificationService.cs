using Microsoft.UI.Xaml.Controls;
using SerialPortTool.Models;
using System.Collections.ObjectModel;

namespace SerialPortTool.Services;

/// <summary>
/// Shows short messages that the status bar cannot carry well: a failure the user has to notice, or
/// a confirmation of an action they may have missed.
/// </summary>
/// <remarks>
/// <para>
/// Borrowed from Netcatty's toast layer (<c>components/ui/toast.tsx</c>) — see the 借鉴 Backlog in
/// AGENTS.md. It is deliberately **not** a general-purpose log bus: it carries what the user should
/// *notice*, while the status bar keeps the running commentary. Routing ordinary progress here would
/// make the stack a second, worse status bar.
/// </para>
/// <para>
/// <b>Errors stay until dismissed.</b> A failure that auto-dismisses is a failure the user did not
/// read, so anything they may need to act on is posted with <c>autoDismissMs: 0</c>.
/// </para>
/// </remarks>
public interface INotificationService
{
    /// <summary>
    /// The live stack, oldest first. Bound by the shell's notification host; do not mutate it.
    /// </summary>
    ReadOnlyObservableCollection<NotificationItem> Items { get; }

    /// <summary>
    /// Adds a notification. Safe to call from any thread — the entry is created on the caller's
    /// thread and marshalled to the UI thread before it touches the bound collection.
    /// </summary>
    /// <param name="message">The text to show.</param>
    /// <param name="severity">Drives the icon, the accent stripe and the edge colour.</param>
    /// <param name="title">Optional bold headline shown above the message.</param>
    /// <param name="autoDismissMs">
    /// Time until the entry removes itself, or <c>0</c> to keep it until the user closes it. The
    /// default matches the timings the status bar already uses for transient hints.
    /// </param>
    void Notify(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational,
        string? title = null,
        int autoDismissMs = 4500);

    /// <summary>Removes one entry, if it is still on screen.</summary>
    void Dismiss(NotificationItem item);

    /// <summary>Removes every entry. Used when the log/session state they describe is replaced.</summary>
    void DismissAll();
}
