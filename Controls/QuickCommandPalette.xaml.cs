using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using SerialPortTool.ViewModels;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.System;

namespace SerialPortTool.Controls;

/// <summary>
/// Keyboard-first switcher over the quick-send library and the serial ports, opened with F2.
/// </summary>
/// <remarks>
/// <para>
/// Borrowed from Netcatty's <c>QuickSwitcher</c>. The shortcut is a function key on purpose:
/// <c>Ctrl+Shift+&lt;letter&gt;</c> combinations get eaten by IMEs and resident tools in a
/// Chinese-language session, which is exactly what the note on <c>MainWindow.OnRootKeyDown</c> records
/// (a <c>KeyboardAccelerator</c> was tried first and never fired). F9 was already taken by 打开日志文件夹,
/// so the palette sits on F2.
/// </para>
/// <para>
/// The palette owns <b>no data</b>. It projects the ViewModel's snippet and port collections and raises
/// one event for the single thing it cannot decide — what activating a row means. The alternative, a
/// palette-local snippet list, is the classic way a "search everything" surface ends up disagreeing with
/// the UI it is searching; here the library is read from exactly one place.
/// </para>
/// </remarks>
public sealed partial class QuickCommandPalette : UserControl
{
    /// <summary>Most rows shown at once. A bound, not a policy — the real lists are small.</summary>
    private const int MaxResults = 60;

    public static readonly DependencyProperty SnippetsProperty = DependencyProperty.Register(
        nameof(Snippets),
        typeof(IEnumerable),
        typeof(QuickCommandPalette),
        new PropertyMetadata(null));

    public static readonly DependencyProperty OpenPortsProperty = DependencyProperty.Register(
        nameof(OpenPorts),
        typeof(IEnumerable),
        typeof(QuickCommandPalette),
        new PropertyMetadata(null));

    public static readonly DependencyProperty AvailablePortsProperty = DependencyProperty.Register(
        nameof(AvailablePorts),
        typeof(IEnumerable),
        typeof(QuickCommandPalette),
        new PropertyMetadata(null));

    /// <summary>The saved quick-send library, as a flat list.</summary>
    public IEnumerable? Snippets
    {
        get => (IEnumerable?)GetValue(SnippetsProperty);
        set => SetValue(SnippetsProperty, value);
    }

    /// <summary>Currently open ports (<c>PortViewModel</c>), previewed with their channel colour.</summary>
    public IEnumerable? OpenPorts
    {
        get => (IEnumerable?)GetValue(OpenPortsProperty);
        set => SetValue(OpenPortsProperty, value);
    }

    /// <summary>Detected but unopened port names.</summary>
    public IEnumerable? AvailablePorts
    {
        get => (IEnumerable?)GetValue(AvailablePortsProperty);
        set => SetValue(AvailablePortsProperty, value);
    }

    /// <summary>
    /// Raised when a row is activated, after the overlay has closed itself.
    /// </summary>
    /// <remarks>
    /// Carries the row rather than a command: the control has no business knowing that a snippet means
    /// "broadcast" and a port means "open or close", and keeping that decision in the window is what lets
    /// the palette reuse the sidebar's own commands. <c>EventHandler&lt;T&gt;</c> because the project
    /// already wires exactly this shape from XAML in <c>LogListView</c>.
    /// </remarks>
    public event EventHandler<PaletteEntry>? Activated;

    private readonly ObservableCollection<PaletteEntry> _results = new();

    public QuickCommandPalette()
    {
        InitializeComponent();

        // Assigned once here rather than bound in XAML: the collection is a fixed instance for the
        // lifetime of the control, so a binding would only add a layer that can be got wrong.
        ResultList.ItemsSource = _results;

        // The card's depth is markup, so it has to be taken back here when Windows' animations are
        // switched off. The main window is resolved after App.OnLaunched has read the preference,
        // which is what makes it readable at this point in the lifetime.
        MotionPreferences.StripDepthIfDisabled(PaletteCard);
    }

    /// <summary>True while the overlay is up.</summary>
    public bool IsOpen => Overlay.Visibility == Visibility.Visible;

    /// <summary>Shows the overlay with a fresh query and the first row preselected.</summary>
    public void Open()
    {
        Overlay.Visibility = Visibility.Visible;

        // Clearing the box raises TextChanged only if it was non-empty, so rebuild explicitly — the
        // palette must never open showing the previous session's filtered results.
        QueryBox.Text = string.Empty;
        Rebuild();

        // Focus once the overlay is visible: focusing a collapsed element is a no-op, and a palette that
        // opens with the caret somewhere else is not keyboard-first at all.
        QueryBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Hides the overlay.
    /// </summary>
    /// <remarks>
    /// Leaves nothing behind on purpose: <see cref="Open"/> rebuilds from scratch, so there is no state
    /// that could go stale between invocations (a port opened meanwhile, a snippet deleted).
    /// </remarks>
    public void Close() => Overlay.Visibility = Visibility.Collapsed;

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // TextChanged also fires while the box is being cleared during Close, when the list is not on
        // screen and rebuilding would be wasted work.
        if (IsOpen)
        {
            Rebuild();
        }
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                Close();
                break;

            case VirtualKey.Enter:
                e.Handled = true;
                ActivateSelected();
                break;

            // The caret lives in the box, so the list never sees these keys on its own; handling them
            // here is what makes the palette navigable without touching the mouse.
            case VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
        }
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PaletteEntry entry)
        {
            Activate(entry);
        }
    }

    private void ActivateSelected()
    {
        var index = ResultList.SelectedIndex;

        // Enter with nothing selected means "the top row", which is what a user who typed a query and hit
        // Enter expects. An empty result set has nothing to activate and simply does nothing.
        if (index < 0 && _results.Count > 0)
        {
            index = 0;
        }

        if (index >= 0 && index < _results.Count)
        {
            Activate(_results[index]);
        }
    }

    private void Activate(PaletteEntry entry)
    {
        // Close first: the handler can take a moment (opening a port runs the full connect path), and an
        // overlay left sitting over the result of its own action is a bug in every app that has it.
        Close();
        Activated?.Invoke(this, entry);
    }

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0)
        {
            return;
        }

        var next = ResultList.SelectedIndex < 0
            ? 0
            : Math.Clamp(ResultList.SelectedIndex + delta, 0, _results.Count - 1);

        ResultList.SelectedIndex = next;
        ResultList.ScrollIntoView(_results[next]);
    }

    /// <summary>
    /// Rebuilds the result list for the current query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Clears and refills the same collection instead of swapping the instance: the list is bound once in
    /// the constructor, so replacing it would mean re-binding on every keystroke. This is a plain
    /// <c>ObservableCollection</c> rather than the shell's <c>RangeObservableCollection</c> deliberately
    /// — the single-<c>Reset</c> discipline exists because a multi-item <c>Reset</c> on the log's
    /// <c>ListView</c> throws under load, and that hazard belongs to the log's scale (thousands of rows,
    /// fires while the user scrolls), not to a list of tens of rows that is rebuilt while it holds focus.
    /// </para>
    /// <para>
    /// Every entry is scored before the list is truncated. The previous shape stopped at <see cref="MaxResults"/>
    /// rows in <em>source</em> order, so with more than sixty rows a query that named something exactly could
    /// still answer with sixty ports and hide it. Scoring is a few dozen string comparisons per keystroke,
    /// which is nothing next to the fact that the row the user asked for has to be the first one.
    /// </para>
    /// </remarks>
    private void Rebuild()
    {
        var query = QueryBox.Text.Trim();

        _results.Clear();

        var matches = new List<(PaletteEntry Entry, int Score, int Order)>();
        var order = 0;

        foreach (var entry in BuildEntries())
        {
            var score = PaletteSearchMatcher.Score(query, entry.Title, entry.Keywords);
            if (score is not null)
            {
                matches.Add((entry, score.Value, order));
            }

            order++;
        }

        // Ties fall back to source order, which is what keeps an empty query showing snippets before ports
        // exactly as it always did.
        matches.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Order.CompareTo(right.Order);
        });

        foreach (var match in matches.Take(MaxResults))
        {
            _results.Add(match.Entry);
        }

        // Preselect the top row so Enter always has a target, and ShowSelection keeps it visible while
        // arrow keys move it (the list is not focused, so it will not scroll itself).
        ResultList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        EmptyHint.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Projects the three sources into rows, snippets first.
    /// </summary>
    /// <remarks>
    /// Snippets lead because they are the one action here the user cannot reach any other way without
    /// leaving the keyboard; ports already have a dedicated list in the sidebar. Each projected entry is
    /// built once per open, so this is not a hot path and does not need to be allocation-free.
    /// </remarks>
    private IEnumerable<PaletteEntry> BuildEntries()
    {
        if (Snippets is not null)
        {
            foreach (var snippet in Snippets.OfType<SendSnippet>())
            {
                yield return new PaletteEntry
                {
                    Kind = PaletteEntryKind.Snippet,
                    // An unlabelled snippet is legal (the label is optional on save), and showing a blank
                    // row would make it unselectable by name.
                    Title = string.IsNullOrWhiteSpace(snippet.Label) ? snippet.Content : snippet.Label,
                    Subtitle = string.IsNullOrWhiteSpace(snippet.Group) ? "快捷指令" : snippet.Group,
                    Glyph = "\uE950",
                    Keywords = $"{snippet.Label} {snippet.Group} {snippet.Content}",
                    Snippet = snippet,
                };
            }
        }

        if (OpenPorts is not null)
        {
            foreach (var port in OpenPorts.OfType<PortViewModel>())
            {
                yield return new PaletteEntry
                {
                    Kind = PaletteEntryKind.OpenPort,
                    Title = port.PortName,
                    Subtitle = $"已打开 · {port.StatisticsDisplay}",
                    Glyph = "\uE73E",
                    AccentHex = port.DisplayColorHex,
                    Keywords = $"{port.PortName} 已打开 open",
                    PortName = port.PortName,
                };
            }
        }

        if (AvailablePorts is not null)
        {
            // OfType<AvailablePortItem> rather than OfType<string>: the sidebar list switched from bare
            // port names to this projection in v2.5.0, and OfType silently yields nothing rather than
            // failing — so this once compiled cleanly while every detected port vanished from the palette.
            foreach (var item in AvailablePorts.OfType<AvailablePortItem>())
            {
                // The note is searchable because that is how a user finds "the one I wrote 传感器 on".
                yield return new PaletteEntry
                {
                    Kind = PaletteEntryKind.AvailablePort,
                    Title = item.PortName,
                    Subtitle = item.HasDeviceDetails ? item.DeviceLine : "可打开",
                    Glyph = "\uE710",
                    Keywords = $"{item.SearchText} 可打开 open",
                    PortName = item.PortName,
                };
            }
        }
    }
}
