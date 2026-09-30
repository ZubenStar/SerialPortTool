using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
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

    public static readonly DependencyProperty IsPinnedToBottomProperty = DependencyProperty.Register(
        nameof(IsPinnedToBottom),
        typeof(bool),
        typeof(LogListView),
        new PropertyMetadata(true));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>
    /// When false (the default), the view follows the latest log entry — every batch of new items
    /// triggers a scroll-to-bottom, but only while the view is actually sitting at the bottom
    /// (<see cref="IsPinnedToBottom"/>): scrolling away from it detaches the follow until the view
    /// comes back or 回到最新 is pressed, so history can be read without pausing reception.
    /// When true, no auto-scrolling happens and the user is free to scroll back through history.
    /// The owning ViewModel is also expected to stop pushing new entries into the bound ItemsSource
    /// while paused (file logging continues on disk).
    /// </summary>
    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    /// <summary>
    /// Whether the view is sitting at the newest row — i.e. whether arriving lines are allowed to
    /// pull it along. Bound TwoWay to <c>MainViewModel.IsLogPinnedToBottom</c>, which gates the
    /// display-buffer trim: while the user is reading history, removing rows from the head would
    /// slide the content out from under the cursor.
    /// </summary>
    /// <remarks>
    /// Written by this control whenever a scroll takes the view off the bottom (or back onto it),
    /// and by the ViewModel whenever the list is replaced wholesale (clear, a new search) — those
    /// are the only two writers, and both only write on an actual change, so the TwoWay binding
    /// cannot ping-pong.
    /// </remarks>
    public bool IsPinnedToBottom
    {
        get => (bool)GetValue(IsPinnedToBottomProperty);
        set => SetValue(IsPinnedToBottomProperty, value);
    }

    /// <summary>Raised after Ctrl+C or context-menu copy completes. Argument is the count copied.</summary>
    public event EventHandler<int>? CopyCompleted;

    /// <summary>Raised when the clipboard refused the content. Argument is the failure message.</summary>
    public event EventHandler<string>? CopyFailed;

    /// <summary>
    /// The selected entries in display order, or an empty list when nothing is selected.
    /// </summary>
    /// <remarks>
    /// Returns items rather than their text so a caller can act on them without re-parsing whatever the
    /// clipboard got. Ordered by walking the source instead of reading <c>SelectedItems</c>: that
    /// collection is in selection order, and an export that came out in the order the user happened to
    /// click would be a surprising thing to hand someone. Must be called on the UI thread — it
    /// enumerates the bound collection.
    /// </remarks>
    public IReadOnlyList<LogEntry> GetSelectedEntries()
    {
        if (InnerListView.SelectedItems.Count == 0 || ItemsSource is not IEnumerable source)
        {
            return Array.Empty<LogEntry>();
        }

        var selected = new HashSet<LogEntry>(InnerListView.SelectedItems.OfType<LogEntry>());
        return selected.Count == 0
            ? Array.Empty<LogEntry>()
            : source.OfType<LogEntry>().Where(selected.Contains).ToArray();
    }

    // ---------------------------------------------------------------------------------------
    // Keyword highlighting (v2.2.4).
    //
    // Painted with TextBlock.TextHighlighters rather than inline Runs or an extra TextBlock:
    // highlighters are a property of the element that is already there, so the row stays at two
    // elements. That shape is a deliberate virtualization decision (see the template's own comment),
    // and adding a container per row is exactly what the do-not-regress notes warn against. Nothing
    // here touches the wheel interception or the pinned-to-bottom state machine.
    //
    // The snapshot is supplied by MainViewModel, compiled from the user's rules for the active
    // appearance; this control knows nothing about "rules" beyond "a pattern with a colour". While
    // the receive path is under output pressure the ViewModel suppresses it and every realized row
    // drops its decoration — highlighting is the most expensive per-line work the view does, so it is
    // the first thing to go, and it comes back on its own once the flood passes.
    // ---------------------------------------------------------------------------------------

    public static readonly DependencyProperty HighlightMatcherProperty = DependencyProperty.Register(
        nameof(HighlightMatcher),
        typeof(IHighlightMatcher),
        typeof(LogListView),
        new PropertyMetadata(null, OnHighlightingChanged));

    public static readonly DependencyProperty IsHighlightSuppressedProperty = DependencyProperty.Register(
        nameof(IsHighlightSuppressed),
        typeof(bool),
        typeof(LogListView),
        new PropertyMetadata(false, OnHighlightingChanged));

    /// <summary>
    /// The compiled highlight snapshot, or null when highlighting is off.
    /// </summary>
    /// <remarks>
    /// Replaced wholesale on every rule edit and on every appearance switch, because the resolved
    /// colours come from the palette. Never mutated in place.
    /// </remarks>
    public IHighlightMatcher? HighlightMatcher
    {
        get => (IHighlightMatcher?)GetValue(HighlightMatcherProperty);
        set => SetValue(HighlightMatcherProperty, value);
    }

    /// <summary>
    /// When true, no row is decorated. Driven by <c>IOutputPressureService</c> through the ViewModel.
    /// </summary>
    public bool IsHighlightSuppressed
    {
        get => (bool)GetValue(IsHighlightSuppressedProperty);
        set => SetValue(IsHighlightSuppressedProperty, value);
    }

    /// <summary>
    /// Brushes for the current snapshot, keyed by rule index.
    /// </summary>
    /// <remarks>
    /// Keyed by rule rather than by match on purpose: several matches of one rule must resolve to a
    /// single brush, and the number of rules is small by construction. Cleared whenever the snapshot
    /// or the suppression changes, because a new generation is exactly when the resolved colours may
    /// differ — rules edited, or the appearance switched.
    /// </remarks>
    private readonly Dictionary<int, SolidColorBrush> _highlightBrushes = new();

    private static void OnHighlightingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is LogListView list)
        {
            list._highlightBrushes.Clear();
            list.RefreshRowHighlights();
        }
    }

    /// <summary>
    /// Wires a realized row's TextBlock so its decoration follows whichever item it is recycled onto.
    /// </summary>
    /// <remarks>
    /// <c>Loaded</c> alone would not be enough: containers are recycled, so the same TextBlock is
    /// reused for a different <see cref="LogEntry"/> without ever being reloaded, and
    /// <c>DataContextChanged</c> is what fires on that handover. The unhook-then-hook is deliberate —
    /// <c>Loaded</c> can fire again after a detach/re-attach (the same double-subscription hazard the
    /// wheel handlers document), and a duplicate subscription would decorate twice per recycle.
    /// </remarks>
    private void RowText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock text)
        {
            return;
        }

        text.DataContextChanged -= RowText_DataContextChanged;
        text.DataContextChanged += RowText_DataContextChanged;
        ApplyRowHighlight(text);
    }

    private void RowText_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text)
        {
            text.DataContextChanged -= RowText_DataContextChanged;
        }
    }

    private void RowText_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is TextBlock text)
        {
            ApplyRowHighlight(text);
        }
    }

    /// <summary>Paints, or clears, the decorations on one realized row.</summary>
    private void ApplyRowHighlight(TextBlock text)
    {
        var matcher = HighlightMatcher;

        if (IsHighlightSuppressed ||
            matcher is null ||
            matcher.IsEmpty ||
            text.DataContext is not LogEntry entry)
        {
            if (text.TextHighlighters.Count > 0)
            {
                text.TextHighlighters.Clear();
            }

            return;
        }

        // Per-entry cache keyed by generation. A row is re-realized on every recycle, and the result
        // depends only on the line text and the rule set — never on which container happens to be
        // showing it. Reading FormattedText here costs nothing after the binding has done it once.
        if (entry.HighlightGeneration != matcher.Generation)
        {
            entry.HighlightMatches = new List<HighlightMatch>(matcher.Match(entry.FormattedText));
            entry.HighlightGeneration = matcher.Generation;
        }

        text.TextHighlighters.Clear();

        var matches = entry.HighlightMatches;
        if (matches is null || matches.Count == 0)
        {
            return;
        }

        foreach (var match in matches)
        {
            var highlighter = new TextHighlighter
            {
                // Foreground rather than a translucent Background wash: the palette's dark variants
                // already exist to stay legible on #101317, so this needs no alpha maths and keeps the
                // same answer on all three palettes. The port colour is overridden only inside the
                // match, which is the signal the user asked for.
                Foreground = GetHighlightBrush(matcher, match.RuleIndex),
            };
            highlighter.Ranges.Add(new TextRange
            {
                StartIndex = match.Start,
                Length = match.Length,
            });

            text.TextHighlighters.Add(highlighter);
        }
    }

    /// <summary>Brush for one rule index, built once per snapshot.</summary>
    private SolidColorBrush GetHighlightBrush(IHighlightMatcher matcher, int ruleIndex)
    {
        if (_highlightBrushes.TryGetValue(ruleIndex, out var brush))
        {
            return brush;
        }

        // The hex was already resolved for the active appearance when the snapshot was compiled, so
        // this is a parse, not a palette lookup.
        brush = new SolidColorBrush(PortColorPalette.ParseHex(matcher.ColorHexFor(ruleIndex)));
        _highlightBrushes[ruleIndex] = brush;
        return brush;
    }

    /// <summary>
    /// Re-decorates every realized row.
    /// </summary>
    /// <remarks>
    /// Walks the realized containers instead of forcing a view rebuild: a <c>Reset</c> would discard
    /// and re-create every container — precisely the cost the flush loop is tuned to avoid — just to
    /// change some foreground colours. Rules and the suppression flag are both rare, user-driven
    /// changes, so one viewport walk is the cheap side of that trade.
    /// </remarks>
    private void RefreshRowHighlights()
    {
        if (InnerListView.ItemsPanelRoot is not Panel panel)
        {
            return;
        }

        foreach (var child in panel.Children)
        {
            var text = FindDescendant<TextBlock>(child);
            if (text is not null)
            {
                ApplyRowHighlight(text);
            }
        }
    }

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
    //
    // What changed in v2.2.3: the step used to be applied as one instantaneous jump
    // (ChangeView(..., disableAnimation: true) straight to VerticalOffset - step), which reads as
    // the view teleporting rather than scrolling, and which also *lost* deltas — several wheel
    // events can arrive inside one frame and each one recomputed its target from a VerticalOffset
    // that had not caught up yet. The step size is unchanged (it is derived from the row height
    // and is what makes one notch a real move); only the traversal is new: the deltas are
    // accumulated into a target offset and a short pump walks the view to it with an exponential
    // approach, so a notch is a ~150-200ms glide and a burst of notches adds up instead of
    // overwriting itself.
    // ---------------------------------------------------------------------------------------
    private const double WheelStepViewportFraction = 0.2; // one notch ≈ 1/5 of the viewport
    private const double MaxWheelStepPixels = 240;        // no half-screen jumps on tall windows
    private const double WheelNotchDelta = 120.0;         // MouseWheelDelta units per detent

    // ---------------------------------------------------------------------------------------
    // The step's floor and the "at the bottom" slack are expressed in ROWS rather than pixels,
    // because the rows are no longer a fixed height: the log font size became user-settable
    // (v2.4.0), and the old hard-coded 60px / 24px silently assumed a ~20px row. A larger font
    // would otherwise have made one notch a smaller fraction of the viewport, and the bottom test
    // tighter than a single row.
    //
    // Derived arithmetically from FontSize instead of measured from a realized container: a
    // font-size change is exactly when the realized containers are still laid out at the OLD
    // height, so a measurement taken then is stale at the only moment it matters. The anchor
    // (20px at the default 13px row) was measured by hand, and everything here only has to be
    // accurate to "about a row".
    // ---------------------------------------------------------------------------------------
    private const double DefaultLogFontSize = 13.0;
    private const double RowHeightAtDefaultFontPx = 20.0;
    private const double LineBoxRatio = 1.35;
    private const double MinWheelStepRows = 3.0;      // the note here used to read "≈ 3 rows"
    private const double FollowBottomSlackRows = 1.2; // ...and this one "about one row"

    // Recomputed by UpdateRowMetrics whenever FontSize changes. Instance state rather than
    // constants precisely because the row height now follows the font size.
    private double _minWheelStepPixels = RowHeightAtDefaultFontPx * MinWheelStepRows;
    private double _followBottomSlackPx = RowHeightAtDefaultFontPx * FollowBottomSlackRows;

    // Traversal constants (v2.2.3). These describe how the step is *covered*, not how far it goes:
    //   · TimeConstantMs — exponential approach; ~3x tau covers ~95% of the distance, so one notch
    //     reads as a short glide instead of a teleport, and a held wheel reads as continuous motion.
    //   · FrameIntervalMs — the pump's cadence. dt is *measured* (Stopwatch) rather than assumed, so
    //     a late frame costs smoothness, never distance.
    //   · MaxFrameDeltaMs — a stalled frame (a 1000 lines/s flush landing mid-gesture) must not turn
    //     into a jump, so the elapsed time is clamped before it feeds the exponential.
    //   · EpsilonPx — convergence: below this the pump snaps exactly onto the target and stops, so
    //     the timer only ever runs while a gesture is actually in flight.
    private const double WheelScrollTimeConstantMs = 70.0;
    private const int WheelScrollFrameIntervalMs = 10;
    private const double WheelScrollMaxFrameDeltaMs = 32.0;
    private const double WheelScrollEpsilonPx = 0.5;

    // How close a wheel glide has to be to its destination before the auto-follow is allowed to
    // take the view over again. The exponential tail is long in time but sub-pixel in distance, so
    // waiting for full convergence would freeze the follow for ~0.4 s after every gesture.
    private const double WheelGlideHandoffPx = 8.0;

    // "Is the view at the newest row?" The slack is about one row: without it, sub-pixel rounding
    // at the bottom flips the follow state on and off while data lands. The value itself is
    // `_followBottomSlackPx`, derived from FontSize above.

    // Two guards keep "did the user scroll away?" honest:
    //   · A whole-list replacement (clear, a new search, a trim) rebuilds the view from scratch and
    //     raises ViewChanged for offsets nobody asked for — ignore those for a moment. Only Reset
    //     gets this window: batches of arriving lines are ~20/s, so suppressing on every change
    //     would keep the window permanently open and no scrollbar drag would ever be noticed.
    //   · Our own auto-follow scroll raises ViewChanged as well, and so does an arriving batch that
    //     grows the scrollable extent while the offset is still the old bottom (i.e. "further from
    //     the bottom" without anybody moving). Both land exactly on the offset we last commanded,
    //     so that is recognised instead of suppressed — see IsOwnProgrammaticScroll.
    private const int PinEvaluationSuppressionMs = 250;
    private const double CommandedOffsetTolerancePx = 1.5;

    private ScrollViewer? _innerScrollViewer;
    private UIElement? _wheelHost;
    private PointerEventHandler? _wheelHandler;

    private DispatcherQueueTimer? _wheelSmootherTimer;
    private readonly Stopwatch _wheelFrameClock = new();
    private double? _wheelTargetOffset;
    private double _wheelCurrentOffset;
    private double? _lastCommandedOffset;
    private long _pinEvaluationSuppressedUntilTick;

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

            // Only runs while a wheel gesture is in flight — see StartWheelScrollPump.
            _wheelSmootherTimer = dispatcher.CreateTimer();
            _wheelSmootherTimer.Interval = TimeSpan.FromMilliseconds(WheelScrollFrameIntervalMs);
            _wheelSmootherTimer.IsRepeating = true;
            _wheelSmootherTimer.Tick += OnWheelSmootherTick;
        }

        // Both directions are needed. A control can be unloaded and re-loaded (template re-application,
        // reparenting, a theme change), and the dependency property is not re-assigned in that case —
        // so ItemsSourceProperty's change callback does not run again. Without the Loaded half the
        // CollectionChanged subscription was simply gone after the first unload, and auto-scroll
        // silently stopped for the rest of the session. The wheel interception (below) resolves the
        // template's ScrollViewer for the same reason: a re-applied template hands out a new one.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // FontSize is inherited from Control, so the shell sets it (bound to the persisted LogFontSize) and
        // the rows inherit it. Its change callback is the only hook needed for the row metrics — there is no
        // layout-pass event to piggyback on, and none is wanted: the metrics are computed from the size, not
        // from a measurement.
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => UpdateRowMetrics());
    }

    /// <summary>
    /// 滚轮按住 Ctrl 时请求调整日志字号：+1 放大，-1 缩小。
    /// </summary>
    /// <remarks>
    /// An event rather than the control changing its own FontSize: the size is persisted by the ViewModel, and
    /// a control that quietly wrote to its own dependency property would need the shell to observe it back.
    /// This is the same shape <c>CopyCompleted</c> / <c>CopyFailed</c> use for the same reason.
    /// </remarks>
    public event EventHandler<int>? FontSizeZoomRequested;

    /// <summary>
    /// 依据当前字号重算滚轮步长下限与贴底容差。
    /// </summary>
    /// <remarks>
    /// Called from the <c>FontSize</c> change callback. The default field values already correspond to the
    /// default size, so a control that is never re-sized is correct without this running at all.
    /// </remarks>
    private void UpdateRowMetrics()
    {
        // Pushed down explicitly as well as mirrored by x:Bind in the markup: the metrics must follow the size
        // even if the binding's own change notification does not arrive (the shell assigns FontSize after this
        // control's own XAML has loaded, so "read once at load" would leave the rows one size behind).
        if (InnerListView is not null)
        {
            InnerListView.FontSize = FontSize;
        }

        var rowHeight = RowHeightAtDefaultFontPx + (FontSize - DefaultLogFontSize) * LineBoxRatio;

        _minWheelStepPixels = rowHeight * MinWheelStepRows;
        _followBottomSlackPx = rowHeight * FollowBottomSlackRows;
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

        // Scrollbar drags, keyboard scrolling and touch panning never reach the wheel handler, so
        // they are the only way those gestures can update the follow state.
        scrollViewer.ViewChanged += OnScrollViewChanged;
    }

    /// <summary>Detaches the wheel handler and drops the resolved ScrollViewer.</summary>
    private void ReleaseWheelInterception()
    {
        if (_innerScrollViewer != null)
        {
            // Best-effort: unhooking from an element that is already being torn down can throw, and
            // that must not abort the rest of the release — but the reason is still recorded.
            try { _innerScrollViewer.ViewChanged -= OnScrollViewChanged; } catch (Exception ex) { Serilog.Log.Debug(ex, "Failed to detach the log scroll handler"); }
        }

        // A pending gesture must not survive a template re-application: the new ScrollViewer starts
        // at the same offset, so replaying the old target would yank the view somewhere else.
        CancelWheelScroll();

        if (_wheelHandler != null)
        {
            if (_wheelHost != null)
            {
                try { _wheelHost.RemoveHandler(PointerWheelChangedEvent, _wheelHandler); } catch (Exception ex) { Serilog.Log.Debug(ex, "Failed to detach the wheel handler from the items presenter"); }
            }

            try { InnerListView.RemoveHandler(PointerWheelChangedEvent, _wheelHandler); } catch (Exception ex) { Serilog.Log.Debug(ex, "Failed to detach the fallback wheel handler"); }
            _wheelHandler = null;
        }

        _wheelHost = null;
        _innerScrollViewer = null;
    }

    /// <summary>
    /// Accumulates one wheel detent into the pending scroll target and starts the pump that walks
    /// the view there. The distance covered is a fixed, viewport-relative amount per detent; how
    /// the view gets there is <see cref="OnWheelSmootherTick"/>'s job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MouseWheelDelta</c> is a multiple of 120 for a classic mouse and a smaller, arbitrary
    /// value for a precision wheel or a touchpad, so the step is scaled by <c>delta / 120</c>
    /// instead of being applied flat — a touchpad flick has to stay proportional or it becomes
    /// unusable. The target is accumulated rather than recomputed from <c>VerticalOffset</c>:
    /// several events can arrive inside a single frame, and each of them used to compute its own
    /// target from an offset that had not caught up, so all but one were silently dropped.
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

        // Ctrl+wheel zooms the log text. Handled inside this existing registration rather than by adding a
        // second handler: both registration sites share this one delegate, and a second AddHandler would run in
        // addition to this one — the "one notch scrolls twice" failure the registration notes above exist to
        // prevent. Handled is set for the Ctrl case too, because the fallback registration also reaches this
        // method and would otherwise zoom a second time.
        if (InputModifiers.IsControlDown())
        {
            FontSizeZoomRequested?.Invoke(this, delta > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }

        var step = Math.Clamp(
            scrollViewer.ViewportHeight * WheelStepViewportFraction,
            _minWheelStepPixels,
            MaxWheelStepPixels);

        // First event of a gesture: start from wherever the view actually is. Later events extend
        // the same gesture, so a burst of notches adds up instead of overwriting itself.
        var target = _wheelTargetOffset ?? scrollViewer.VerticalOffset;
        if (_wheelTargetOffset == null)
        {
            _wheelCurrentOffset = target;
        }

        var maxOffset = Math.Max(0, scrollViewer.ScrollableHeight);
        _wheelTargetOffset = Math.Clamp(target - delta / WheelNotchDelta * step, 0, maxOffset);

        // The follow state is decided by where the gesture is *heading*, not by where the view is
        // right now — scrolling back down to the newest row must re-attach immediately, not after
        // the pump finishes.
        SetPinnedToBottom(_wheelTargetOffset.Value >= maxOffset - _followBottomSlackPx);

        StartWheelScrollPump();
        e.Handled = true;
    }

    /// <summary>Starts (or keeps alive) the interpolation pump for the current wheel gesture.</summary>
    private void StartWheelScrollPump()
    {
        var timer = _wheelSmootherTimer;
        if (timer == null)
        {
            // No dispatcher timer (headless construction): fall back to the pre-v2.2.3 behaviour so
            // the wheel still moves the view, just without the glide.
            ApplyWheelTargetImmediate();
            return;
        }

        if (!timer.IsRunning)
        {
            _wheelFrameClock.Restart();
            timer.Start();
        }
    }

    /// <summary>Stops the pump and forgets the gesture. Used on convergence, on unload and by
    /// <see cref="ResumeFollowLatest"/>.</summary>
    private void CancelWheelScroll()
    {
        _wheelTargetOffset = null;

        if (_wheelSmootherTimer != null)
        {
            try { _wheelSmootherTimer.Stop(); } catch (Exception ex) { Serilog.Log.Debug(ex, "Failed to stop the wheel smoother"); }
        }

        _wheelFrameClock.Reset();
    }

    private void ApplyWheelTargetImmediate()
    {
        if (_innerScrollViewer == null || _wheelTargetOffset is not { } target)
        {
            return;
        }

        var maxOffset = Math.Max(0, _innerScrollViewer.ScrollableHeight);
        _innerScrollViewer.ChangeView(null, Math.Clamp(target, 0, maxOffset), null, disableAnimation: true);
        CancelWheelScroll();
    }

    /// <summary>
    /// Walks the view towards the pending wheel target with an exponential approach, one call per
    /// pump tick, until the remaining distance is below the convergence threshold.
    /// </summary>
    /// <remarks>
    /// The offset is tracked locally (<c>_wheelCurrentOffset</c>) rather than read back from
    /// <c>ScrollViewer.VerticalOffset</c>, because that property does not necessarily reflect a
    /// <c>ChangeView</c> from the same tick; reading it back would make the pump re-apply the same
    /// step forever. The pump therefore also snaps exactly onto the target on the final tick, so a
    /// divergence between the two can never leave the view short of the gesture's destination.
    ///
    /// <c>disableAnimation: true</c> is deliberate: this pump *is* the animation. The alternative —
    /// letting the framework animate each <c>ChangeView</c> — retargets differently on mouse vs
    /// touchpad and fights the auto-follow scroll, and is the fallback to try if this interpolation
    /// ever misbehaves on a newer Windows App SDK.
    /// </remarks>
    private void OnWheelSmootherTick(DispatcherQueueTimer sender, object args)
    {
        var scrollViewer = _innerScrollViewer;
        if (scrollViewer == null || _wheelTargetOffset is not { } requested)
        {
            CancelWheelScroll();
            return;
        }

        var maxOffset = Math.Max(0, scrollViewer.ScrollableHeight);
        var target = Math.Clamp(requested, 0, maxOffset);

        var remaining = target - _wheelCurrentOffset;
        if (Math.Abs(remaining) <= WheelScrollEpsilonPx)
        {
            _wheelCurrentOffset = target;
            scrollViewer.ChangeView(null, target, null, disableAnimation: true);
            CancelWheelScroll();
            SetPinnedToBottom(target >= maxOffset - _followBottomSlackPx);
            return;
        }

        var elapsedMs = _wheelFrameClock.Elapsed.TotalMilliseconds;
        _wheelFrameClock.Restart();

        // Clamped so one stalled frame (a big flush landing mid-gesture) cannot become a jump.
        if (elapsedMs < 0)
        {
            elapsedMs = 0;
        }
        else if (elapsedMs > WheelScrollMaxFrameDeltaMs)
        {
            elapsedMs = WheelScrollMaxFrameDeltaMs;
        }

        var alpha = 1.0 - Math.Exp(-elapsedMs / WheelScrollTimeConstantMs);
        _wheelCurrentOffset += remaining * alpha;

        scrollViewer.ChangeView(null, _wheelCurrentOffset, null, disableAnimation: true);
    }

    /// <summary>
    /// Immediately returns to the newest row and re-attaches the follow. Bound to the overlay
    /// button that appears while the view is detached.
    /// </summary>
    public void ResumeFollowLatest()
    {
        CancelWheelScroll();

        var scrollViewer = _innerScrollViewer;
        if (scrollViewer == null)
        {
            if (InnerListView.Items.Count > 0 &&
                InnerListView.Items[InnerListView.Items.Count - 1] is { } lastItem)
            {
                InnerListView.ScrollIntoView(lastItem);
            }

            SetPinnedToBottom(true);
            return;
        }

        SuppressPinEvaluation();
        CommandScroll(Math.Max(0, scrollViewer.ScrollableHeight));
        SetPinnedToBottom(true);
    }

    /// <summary>
    /// Scrolls to <paramref name="offset"/> and remembers the command, so the <c>ViewChanged</c> it
    /// raises is not mistaken for the user scrolling (see <see cref="IsOwnProgrammaticScroll"/>).
    /// </summary>
    private void CommandScroll(double offset)
    {
        var scrollViewer = _innerScrollViewer;
        if (scrollViewer == null)
        {
            return;
        }

        var target = Math.Clamp(offset, 0, Math.Max(0, scrollViewer.ScrollableHeight));
        _lastCommandedOffset = target;
        scrollViewer.ChangeView(null, target, null, disableAnimation: true);
    }

    /// <summary>
    /// True when a reported offset is the one this control last commanded — i.e. the auto-follow,
    /// or an arriving batch that grew the extent while the view sat on the old bottom. Only trusted
    /// while the view is still pinned: once the user has scrolled away, any past command is stale.
    /// </summary>
    private bool IsOwnProgrammaticScroll(double offset) =>
        IsPinnedToBottom &&
        _lastCommandedOffset.HasValue &&
        Math.Abs(offset - _lastCommandedOffset.Value) <= CommandedOffsetTolerancePx;

    /// <summary>
    /// Updates the follow state from the view's current offset. Used for every gesture that is not
    /// a wheel one (scrollbar drag, keyboard, touch panning).
    /// </summary>
    private void EvaluatePinnedToBottom()
    {
        var scrollViewer = _innerScrollViewer;
        if (scrollViewer == null)
        {
            return;
        }

        var maxOffset = Math.Max(0, scrollViewer.ScrollableHeight);
        SetPinnedToBottom(scrollViewer.VerticalOffset >= maxOffset - _followBottomSlackPx);
    }

    /// <summary>
    /// Writes the follow state, but only on a real change — the property is bound TwoWay, so a
    /// redundant write would push a notification back into the ViewModel on every scroll.
    /// </summary>
    private void SetPinnedToBottom(bool value)
    {
        if (IsPinnedToBottom == value)
        {
            return;
        }

        IsPinnedToBottom = value;
    }

    /// <summary>
    /// Holds off <see cref="EvaluatePinnedToBottom"/> briefly, so our own scrolls and the extents
    /// growth of an arriving batch are not mistaken for the user scrolling away.
    /// </summary>
    private void SuppressPinEvaluation() =>
        _pinEvaluationSuppressedUntilTick = Environment.TickCount64 + PinEvaluationSuppressionMs;

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // A wheel gesture owns the state while it is in flight: it is judged by its target, which
        // is where the user is going, not by the intermediate offsets the pump walks through.
        if (_wheelTargetOffset.HasValue)
        {
            return;
        }

        var scrollViewer = _innerScrollViewer;
        if (scrollViewer == null)
        {
            return;
        }

        if (IsOwnProgrammaticScroll(scrollViewer.VerticalOffset))
        {
            return;
        }

        if (Environment.TickCount64 < _pinEvaluationSuppressedUntilTick)
        {
            return;
        }

        EvaluatePinnedToBottom();
    }

    /// <summary>
    /// 深度优先查找第一个 <typeparamref name="T"/> 类型的后代元素。
    /// </summary>
    /// <remarks>
    /// Used for both the list's template-provided <c>ScrollViewer</c> and the row's single
    /// <c>TextBlock</c>. It replaced a second, hand-rolled copy of the same walk that existed only
    /// because the two call sites happened to ask for different types.
    /// </remarks>
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

        // A Reset means the whole list was replaced (clear, a committed search, a trim), so the
        // ViewChanged it raises describes a rebuild, not the user scrolling. Never do this for Add:
        // batches arrive ~20/s, and a window refreshed that often would never let a real scroll
        // through.
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            SuppressPinEvaluation();
        }

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

            // Following is something the view earns by sitting at the newest row. Scroll away from
            // it and arriving data must leave the view exactly where the user put it — that is the
            // whole point of the detached state.
            if (!IsPinnedToBottom)
            {
                return;
            }

            // A wheel glide that is still carrying the view somewhere would fight an auto-follow
            // scroll, so let it arrive first — but only while it still has real distance to cover;
            // the exponential tail is sub-pixel and would otherwise freeze the follow for ~0.4 s.
            if (_wheelTargetOffset.HasValue &&
                Math.Abs(_wheelTargetOffset.Value - _wheelCurrentOffset) > WheelGlideHandoffPx)
            {
                return;
            }

            var scrollViewer = _innerScrollViewer;
            if (scrollViewer == null)
            {
                // Template not resolved (or a restyle moved the ScrollViewer): keep the older,
                // dumber behaviour instead of silently dropping the follow.
                if (InnerListView.Items[InnerListView.Items.Count - 1] is { } lastItem)
                {
                    InnerListView.ScrollIntoView(lastItem);
                }

                return;
            }

            var maxOffset = Math.Max(0, scrollViewer.ScrollableHeight);
            if (scrollViewer.VerticalOffset >= maxOffset - _followBottomSlackPx / 2)
            {
                // Already there. ChangeView on every 50 ms flush is pure overhead, and it used to
                // be a ScrollIntoView that forced the last row to be realized each time.
                return;
            }

            // Straight to the bottom of the scrollable range: no item realization, no animation
            // (the auto-follow must stay out of the wheel pump's way).
            CommandScroll(maxOffset);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed scroll is not worth an error dialog, but "the log stopped
            // following and nothing anywhere says why" is exactly what this line answers.
            _isAutoScrollPending = false;
            Serilog.Log.Debug(ex, "Log auto-scroll failed");
        }
    }

    private void InnerListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (InputModifiers.IsControlDown())
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

    /// <summary>Overlay button: back to the newest row, and start following again.</summary>
    private void FollowLatest_Click(object sender, RoutedEventArgs e) => ResumeFollowLatest();

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
            try { _autoScrollTimer.Stop(); } catch (Exception ex) { Serilog.Log.Debug(ex, "Failed to stop the auto-scroll timer"); }
        }
    }
}
