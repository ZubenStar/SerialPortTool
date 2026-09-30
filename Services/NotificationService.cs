using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using SerialPortTool.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace SerialPortTool.Services;

/// <summary>
/// UI-thread notification stack for <see cref="INotificationService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Holds the collection the shell binds to and owns the auto-dismiss timers. Both matter: the
/// collection is an <see cref="ObservableCollection{T}"/> bound to an <c>ItemsControl</c>, so every
/// mutation has to happen on the UI thread, and the timers are <see cref="DispatcherQueueTimer"/>s on
/// the same dispatcher for the same reason — a background <see cref="System.Threading.Timer"/> would
/// remove an item from a bound collection off-thread and take the window down.
/// </para>
/// <para>
/// The queue is captured at construction. In this application that is the UI thread (the container
/// resolves the service while building <c>MainWindow</c>); when it is <c>null</c> — a unit test, or a
/// headless construction — the marshal is skipped rather than throwing, so the service stays usable
/// without a window.
/// </para>
/// </remarks>
public sealed class NotificationService : INotificationService
{
    /// <summary>
    /// Maximum bars on screen at once; beyond this the oldest is dropped.
    /// </summary>
    /// <remarks>
    /// The stack is a glanceable surface sitting over the log. A wall of bars both hides the data the
    /// user is reading and costs a layout pass per bar, so overflow is dropped rather than scrolled —
    /// the newest message is the one that just happened.
    /// </remarks>
    private const int MaxVisible = 4;

    private readonly ObservableCollection<NotificationItem> _items = new();
    private readonly DispatcherQueue? _dispatcherQueue;

    public NotificationService()
    {
        Items = new ReadOnlyObservableCollection<NotificationItem>(_items);
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <inheritdoc />
    public ReadOnlyObservableCollection<NotificationItem> Items { get; }

    /// <inheritdoc />
    public void Notify(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational,
        string? title = null,
        int autoDismissMs = 4500)
    {
        var item = new NotificationItem
        {
            Message = message,
            Severity = severity,
            Title = title,
        };

        RunOnUiThread(() => Add(item, autoDismissMs));
    }

    /// <inheritdoc />
    public void Dismiss(NotificationItem item) => RunOnUiThread(() => Remove(item));

    /// <inheritdoc />
    public void DismissAll() => RunOnUiThread(() =>
    {
        while (_items.Count > 0)
        {
            RemoveAt(0);
        }
    });

    private void Add(NotificationItem item, int autoDismissMs)
    {
        item.PropertyChanged += OnItemPropertyChanged;
        _items.Add(item);

        while (_items.Count > MaxVisible)
        {
            RemoveAt(0);
        }

        if (autoDismissMs > 0)
        {
            ScheduleAutoDismiss(item, autoDismissMs);
        }
    }

    /// <summary>
    /// Drops the entry when the view closes it, so "the user pressed ✕" and "the timer expired" end
    /// up on one removal path.
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NotificationItem.IsOpen) ||
            sender is not NotificationItem item ||
            item.IsOpen)
        {
            return;
        }

        Dismiss(item);
    }

    private void Remove(NotificationItem item)
    {
        var index = _items.IndexOf(item);
        if (index >= 0)
        {
            RemoveAt(index);
        }
    }

    private void RemoveAt(int index)
    {
        var item = _items[index];
        item.PropertyChanged -= OnItemPropertyChanged;
        _items.RemoveAt(index);
    }

    private void ScheduleAutoDismiss(NotificationItem item, int delayMs)
    {
        var queue = _dispatcherQueue;
        if (queue == null)
        {
            // No dispatcher to time against. Leaving the entry in place is the safe outcome — the
            // user can still close it by hand.
            return;
        }

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(delayMs);
        timer.IsRepeating = false;

        // A local function rather than a lambda, so the handler can unhook itself: the timer holds
        // its own Tick subscription, and a stopped one-shot timer would otherwise keep the handler —
        // and the notification it closes over — reachable until the dispatcher released the timer.
        void OnTick(DispatcherQueueTimer sender, object args)
        {
            sender.Tick -= OnTick;
            sender.Stop();
            Remove(item);
        }

        timer.Tick += OnTick;
        timer.Start();
    }

    private void RunOnUiThread(Action action)
    {
        var queue = _dispatcherQueue;
        if (queue == null || queue.HasThreadAccess)
        {
            action();
            return;
        }

        if (!queue.TryEnqueue(() => action()))
        {
            // The dispatcher is shutting down. Dropping the message is correct here: the window is
            // going away and there is nothing left to show it on.
            return;
        }
    }
}
