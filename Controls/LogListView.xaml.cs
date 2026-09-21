using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    /// <summary>Raised when the clipboard refused the content. Argument is the failure message.</summary>
    public event EventHandler<string>? CopyFailed;

    private readonly DispatcherQueueTimer? _autoScrollTimer;
    private bool _isAutoScrollPending;
    private INotifyCollectionChanged? _observedSource;

    // ---------------------------------------------------------------------------------------
    // Wheel scrolling.
    //
    // The log rows are deliberately short (MinHeight/Padding/Margin are all 0, one NoWrap
    // 13px line — ~20px per row), so the ListView's own wheel step (a small number of "lines")
    // moves the view by barely a row per notch and reads as "the wheel does nothing". The list
    // is not slow — the step is simply too small for this content. The control therefore takes
    // the wheel over and asks the ScrollViewer for a fixed, viewport-relative offset instead.
    //
    // The handler is attached to the ScrollViewer's content (the items presenter) with
    // handledEventsToo: false, i.e. it runs BEFORE the template's ScrollViewer applies its own
    // step, and sets Handled so that step is not added on top of ours. A second, fallback
    // registration on the ListView itself (handledEventsToo: true) covers wheel gestures that
    // never travel through the items presenter — the empty area below the last row hit-tests
    // the ScrollViewer directly. That fallback is a no-op whenever the primary handler ran,
    // because the primary one has already set Handled (the early return below).
    // ---------------------------------------------------------------------------------------
    private const double WheelStepViewportFraction = 0.2; // one notch ≈ 1/5 of the viewport
    private const double MinWheelStepPixels = 60;         // ≈ 3 rows at the ~20px row height
    private const double MaxWheelStepPixels = 240;        // no half-screen jumps on tall windows
    private const double WheelNotchDelta = 120.0;         // MouseWheelDelta units per detent

    private ScrollViewer? _innerScrollViewer;
    private UIElement? _wheelHost;
    private PointerEventHandler? _wheelHandler;

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

        // Both directions are needed. A control can be unloaded and re-loaded (template re-application,
        // reparenting, a theme change), and the dependency property is not re-assigned in that case —
        // so ItemsSourceProperty's change callback does not run again. Without the Loaded half the
        // CollectionChanged subscription was simply gone after the first unload, and auto-scroll
        // silently stopped for the rest of the session. The wheel interception (below) resolves the
        // template's ScrollViewer for the same reason: a re-applied template hands out a new one.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookWheelInterception();

        if (_observedSource != null || ItemsSource is not INotifyCollectionChanged source)
        {
            return;
        }

        _observedSource = source;
        source.CollectionChanged += OnSourceCollectionChanged;
    }

    /// <summary>
    /// Resolves the ListView's internal <see cref="ScrollViewer"/> and attaches the wheel handler
    /// that drives it, or leaves the wheel alone when the template is not there yet.
    /// </summary>
    /// <remarks>
    /// Re-resolved on every <c>Loaded</c>, and always released first, because the template can be
    /// re-applied (a new ScrollViewer and items presenter) and the dependency properties are not
    /// reassigned in that case — attaching twice would step twice per notch.
    /// </remarks>
    private void HookWheelInterception()
    {
        ReleaseWheelInterception();

        // The content of the template's ScrollViewer is the items presenter. If a future restyle
        // moves it we simply stop intercepting and fall back to the framework's own wheel step
        // rather than swallowing the gesture into a no-op.
        if (FindDescendant<ScrollViewer>(InnerListView) is not { Content: UIElement host } scrollViewer)
        {
            return;
        }

        _innerScrollViewer = scrollViewer;
        _wheelHost = host;
        _wheelHandler = OnWheelIntercepted;

        // Primary: runs before the ScrollViewer's own class handler, so setting Handled there
        // prevents its (tiny) step from being applied on top of ours.
        host.AddHandler(PointerWheelChangedEvent, _wheelHandler, handledEventsToo: false);

        // Fallback: a wheel gesture over the empty area below the last row never passes through
        // the items presenter. It is ignored whenever the primary handler already ran.
        InnerListView.AddHandler(PointerWheelChangedEvent, _wheelHandler, handledEventsToo: true);
    }

    /// <summary>Detaches the wheel handler and drops the resolved ScrollViewer.</summary>
    private void ReleaseWheelInterception()
    {
        if (_wheelHandler != null)
        {
            if (_wheelHost != null)
            {
                try { _wheelHost.RemoveHandler(PointerWheelChangedEvent, _wheelHandler); } catch { }
            }

            try { InnerListView.RemoveHandler(PointerWheelChangedEvent, _wheelHandler); } catch { }
            _wheelHandler = null;
        }

        _wheelHost = null;
        _innerScrollViewer = null;
    }

    /// <summary>
    /// Scrolls the list by a fixed, viewport-relative amount per wheel detent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MouseWheelDelta</c> is a multiple of 120 for a classic mouse and a smaller, arbitrary
    /// value for a precision wheel or a touchpad, so the step is scaled by <c>delta / 120</c>
    /// instead of being applied flat — a touchpad flick has to stay proportional or it becomes
    /// unusable. <c>ChangeView</c> clamps to the scrollable range on its own, so the ends of the
    /// list need no special casing, and the animation is disabled so the offset follows the wheel
    /// immediately (and cannot race the auto-follow <c>ScrollIntoView</c>).
    /// </para>
    /// <para>
    /// Horizontal wheel/tilt gestures are left to the framework: horizontal scrolling is disabled
    /// on this ListView, and consuming the event anyway would swallow them silently.
    /// </para>
    /// </remarks>
    private void OnWheelIntercepted(object sender, PointerRoutedEventArgs e)
    {
        // Already handled by the primary registration (or by something else further down the
        // route): the fallback registration reaches this same handler a second time.
        if (e.Handled)
        {
            return;
        }

        var scrollViewer = _innerScrollViewer;
        if (scrollViewer == null)
        {
            return;
        }

        var properties = e.GetCurrentPoint(scrollViewer).Properties;
        if (properties.IsHorizontalMouseWheel)
        {
            return;
        }

        var delta = properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        var step = Math.Clamp(
            scrollViewer.ViewportHeight * WheelStepViewportFraction,
            MinWheelStepPixels,
            MaxWheelStepPixels);

        var targetOffset = scrollViewer.VerticalOffset - delta / WheelNotchDelta * step;
        scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);

        e.Handled = true;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Public entry point for the toolbar "全选" button.</summary>
    public void SelectAll()
    {
        InnerListView.SelectAll();
    }

    /// <summary>
    /// Public entry point for the toolbar "复制" button.
    /// </summary>
    /// <returns><c>false</c> when nothing is selected (or the clipboard refused the content), so the
    /// caller can say so instead of silently doing nothing. On success <see cref="CopyCompleted"/>
    /// carries the count as usual.</returns>
    public bool CopySelection()
    {
        if (InnerListView.SelectedItems.Count == 0)
        {
            return false;
        }

        return CopySelectedToClipboard();
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
        //
        // We accept both Add and Reset. RangeObservableCollection batches via Reset (WinUI 3
        // ListView mishandles multi-item Add notifications), so a Reset here means "a batch of
        // new items was just appended" for our use case — scrolling to the last item lands on
        // them. Other Reset producers (Clear, FilterLogs replacing the whole set) are also fine
        // to scroll to the bottom; pause + a new search both wipe the view anyway.
        if (IsPaused) return;
        if (e.Action != NotifyCollectionChangedAction.Add &&
            e.Action != NotifyCollectionChangedAction.Reset)
            return;
        if (e.Action == NotifyCollectionChangedAction.Add && (e.NewItems?.Count ?? 0) == 0)
            return;

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

    /// <summary>
    /// Copies the selected rows, returning <c>false</c> when there was nothing to copy or the
    /// clipboard refused the content.
    /// </summary>
    /// <remarks>
    /// <c>Clipboard.SetContent</c> is a cross-process COM call and throws (typically
    /// <c>COMException</c> 0x800401D0, CLIPBRD_E_CANT_OPEN) whenever another process holds the
    /// clipboard open — a browser tab or clipboard manager doing this is routine, not exceptional.
    /// Unhandled, that exception reached <c>Application.UnhandledException</c> and took the whole app
    /// down; with a "copy log" toolbar button and a Ctrl+C shortcut this was reachable by accident.
    /// </remarks>
    private bool CopySelectedToClipboard()
    {
        var selectedItems = InnerListView.SelectedItems;
        if (selectedItems.Count == 0) return false;

        try
        {
            var text = string.Join("\n", selectedItems.Cast<LogEntry>().Select(l => l.FormattedText));

            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
        }
        catch (Exception ex)
        {
            CopyFailed?.Invoke(this, ex.Message);
            return false;
        }

        CopyCompleted?.Invoke(this, selectedItems.Count);
        return true;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ReleaseWheelInterception();

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
