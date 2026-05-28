using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialPortTool.Models;
using System;
using System.Collections;
using System.Collections.Specialized;
using System.Linq;

namespace SerialPortTool.Controls;

/// <summary>
/// Self-contained log list view. Encapsulates the "locked to latest" + pause UX, multi-select
/// with Ctrl+C/A shortcuts, and the context-flyout copy. Lives in a UserControl so the
/// DataTemplate can use compiled x:Bind — that's the perf-critical difference vs. hosting the
/// ListView directly under a WinUI 3 Window.
/// </summary>
public sealed partial class LogListView : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable),
        typeof(LogListView),
        new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty IsPausedProperty = DependencyProperty.Register(
        nameof(IsPaused),
        typeof(bool),
        typeof(LogListView),
        new PropertyMetadata(false));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// When false (the default), the view auto-follows the latest log entry — every batch of new
    /// items triggers a scroll-to-bottom. When true, no auto-scrolling happens and the user is
    /// free to scroll back through history. The owning ViewModel is also expected to stop pushing
    /// new entries into the bound ItemsSource while paused (file logging continues on disk).
    /// </summary>
    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    /// <summary>Raised after Ctrl+C or context-menu copy completes. Argument is the count copied.</summary>
    public event EventHandler<int>? CopyCompleted;

    private readonly DispatcherQueueTimer? _autoScrollTimer;
    private bool _isAutoScrollPending;
    private INotifyCollectionChanged? _observedSource;

    public LogListView()
    {
        InitializeComponent();

        // Note: InnerListView.ItemsSource is wired in XAML via {x:Bind ItemsSource, Mode=OneWay}.
        // We only handle the change here to manage the CollectionChanged subscription for auto-scroll.

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        if (dispatcher != null)
        {
            _autoScrollTimer = dispatcher.CreateTimer();
            _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(40);
            _autoScrollTimer.IsRepeating = false;
            _autoScrollTimer.Tick += (_, _) => PerformPendingAutoScroll();
        }

        Unloaded += OnUnloaded;
    }

    /// <summary>Public entry point for the toolbar "全选" button.</summary>
    public void SelectAll()
    {
        InnerListView.SelectAll();
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ctrl = (LogListView)d;

        // Manage CollectionChanged subscription so we can fire auto-scroll on Add notifications.
        // The actual InnerListView.ItemsSource update is handled by x:Bind in XAML — don't set it
        // here, that creates an unreliable second source of truth.

        if (ctrl._observedSource != null)
        {
            ctrl._observedSource.CollectionChanged -= ctrl.OnSourceCollectionChanged;
            ctrl._observedSource = null;
        }

        if (e.NewValue is INotifyCollectionChanged incc)
        {
            ctrl._observedSource = incc;
            incc.CollectionChanged += ctrl.OnSourceCollectionChanged;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Auto-scroll to the latest entry whenever new items arrive — unless the user has paused.
        // The pause check is also enforced upstream (the ViewModel stops enqueuing UI batches while
        // paused), but checking here too is defense in depth.
        if (IsPaused) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        if ((e.NewItems?.Count ?? 0) == 0) return;

        _isAutoScrollPending = true;

        if (_autoScrollTimer != null)
        {
            _autoScrollTimer.Stop();
            _autoScrollTimer.Start();
        }
        else
        {
            PerformPendingAutoScroll();
        }
    }

    private void PerformPendingAutoScroll()
    {
        if (!_isAutoScrollPending || IsPaused || InnerListView.Items.Count == 0)
        {
            _isAutoScrollPending = false;
            return;
        }

        try
        {
            _isAutoScrollPending = false;

            var lastItem = InnerListView.Items[InnerListView.Items.Count - 1];
            if (lastItem != null)
            {
                InnerListView.ScrollIntoView(lastItem);
            }
        }
        catch (Exception)
        {
            _isAutoScrollPending = false;
        }
    }

    private void InnerListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            if (e.Key == Windows.System.VirtualKey.C)
            {
                CopySelectedToClipboard();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.A)
            {
                InnerListView.SelectAll();
                e.Handled = true;
            }
        }
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelectedToClipboard();

    private void SelectAllInList_Click(object sender, RoutedEventArgs e) => InnerListView.SelectAll();

    private void CopySelectedToClipboard()
    {
        var selectedItems = InnerListView.SelectedItems;
        if (selectedItems.Count == 0) return;

        var text = string.Join("\n", selectedItems.Cast<LogEntry>().Select(l => l.FormattedText));

        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

        CopyCompleted?.Invoke(this, selectedItems.Count);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_observedSource != null)
        {
            _observedSource.CollectionChanged -= OnSourceCollectionChanged;
            _observedSource = null;
        }
        if (_autoScrollTimer != null)
        {
            try { _autoScrollTimer.Stop(); } catch { }
        }
    }
}
