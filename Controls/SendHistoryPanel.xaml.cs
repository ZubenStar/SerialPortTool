using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SerialPortTool.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.System;

namespace SerialPortTool.Controls;

/// <summary>
/// 可搜索、可单条删除的发送历史面板。
/// </summary>
/// <remarks>
/// <para>
/// <b>It overlays the existing ↑/↓ recall, it does not replace it.</b> Both read the same list from the
/// ViewModel (<c>MainViewModel.RecentSendTexts</c> / <c>SendHistoryEntries</c>), so deleting here changes
/// what ↑ walks, and there is no second copy that could drift out of step. The keyboard recall stays
/// because it is faster for "the last three things", and this panel is for "that one command from Tuesday".
/// </para>
/// <para>
/// Owns no data, exactly like <c>QuickCommandPalette</c>: it projects the ViewModel's history and raises
/// one event per thing it cannot decide. That is what keeps it from disagreeing with the send box it feeds.
/// </para>
/// <para>
/// Search reuses <see cref="PaletteSearchMatcher"/> rather than a private filter. Two search surfaces in
/// one app that sort differently are a subtle bug users cannot name, and the tiering there already handles
/// the case-insensitive / separator-insensitive matching this needs.
/// </para>
/// </remarks>
public sealed partial class SendHistoryPanel : UserControl
{
    /// <summary>Most rows shown. Older entries stay reachable by narrowing the query.</summary>
    private const int MaxResults = 100;

    /// <summary>How much of a payload one row shows. Larger cut-offs make every row look the same.</summary>
    private const int PreviewMaxLength = 120;

    public static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(
        nameof(Entries),
        typeof(IEnumerable),
        typeof(SendHistoryPanel),
        new PropertyMetadata(null));

    /// <summary>完整的发送历史（最新在前）；面板只读取它。</summary>
    public IEnumerable? Entries
    {
        get => (IEnumerable?)GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    /// <summary>A row was chosen — put it in the send box.</summary>
    public event EventHandler<string>? Activated;

    /// <summary>一条记录要被删除。</summary>
    public event EventHandler<string>? Deleted;

    /// <summary>全部记录要被清空。</summary>
    public event EventHandler? ClearRequested;

    private readonly ObservableCollection<string> _rows = new();

    public SendHistoryPanel()
    {
        InitializeComponent();
        ResultList.ItemsSource = _rows;
    }

    /// <summary>True while the overlay is up.</summary>
    public bool IsOpen => Overlay.Visibility == Visibility.Visible;

    /// <summary>Shows the overlay with the full history, newest first.</summary>
    public void Open()
    {
        Overlay.Visibility = Visibility.Visible;

        QueryBox.Text = string.Empty;
        Rebuild();

        // Focus only once visible: focusing a collapsed element is a no-op, and a search surface that
        // opens without the caret in it is not really a search surface.
        QueryBox.Focus(FocusState.Programmatic);
    }

    /// <summary>Hides the overlay.</summary>
    public void Close() => Overlay.Visibility = Visibility.Collapsed;

    private void Scrim_Tapped(object sender, TappedRoutedEventArgs e) => Close();

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
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
        if (e.ClickedItem is string payload)
        {
            Activate(payload);
        }
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string payload })
        {
            // Not deferred: the list is rescored from the ViewModel's collection afterwards, so the row
            // disappears only once its owner actually removed it. Dropping it here would resurrect on the
            // next keystroke.
            Deleted?.Invoke(this, payload);
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void ActivateSelected()
    {
        var index = ResultList.SelectedIndex;
        if (index < 0 && _rows.Count > 0)
        {
            index = 0;
        }

        if (index >= 0 && index < _rows.Count)
        {
            Activate(_rows[index]);
        }
    }

    private void Activate(string payload)
    {
        // Close before raising: filling the send box is instant and the user wants to see the result, not
        // this panel.
        Close();
        Activated?.Invoke(this, payload);
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        var next = ResultList.SelectedIndex < 0
            ? 0
            : Math.Clamp(ResultList.SelectedIndex + delta, 0, _rows.Count - 1);

        ResultList.SelectedIndex = next;
        ResultList.ScrollIntoView(ResultList.Items[next]);
    }

    private void Rebuild()
    {
        _rows.Clear();

        var query = QueryBox.Text;
        var scored = new List<(int Score, int Order, string Payload)>();
        var order = 0;

        foreach (var payload in Entries?.OfType<string>() ?? Enumerable.Empty<string>())
        {
            // Matched against the preview rather than the payload: searching text the user cannot see would
            // make a row match for no visible reason.
            var score = PaletteSearchMatcher.Score(query, BuildPreview(payload), null);
            if (score is null)
            {
                continue;
            }

            scored.Add((score.Value, order, payload));
            order++;
        }

        // Stable sort: an empty query scores every row 0 and must therefore come back newest-first, which
        // is the order users read the list in and the order ↑ walks it.
        foreach (var row in scored.OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Order))
        {
            _rows.Add(row.Payload);

            if (_rows.Count >= MaxResults)
            {
                break;
            }
        }

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 搜索时的匹配文本：换行折成空格并截断长度。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newlines collapse to spaces rather than being preserved: a rows have to be one line, and XAML's own
    /// whitespace handling collapses them the same way when the row renders — so what is searched and what
    /// is displayed agree. Display only ever reads this; <see cref="Activated"/> always hands back the
    /// original payload, newlines included.
    /// </para>
    /// <para>
    /// Truncated last, after flattening, so a long payload is cut at a stable character count instead of
    /// being measured in whatever the line breaks happened to cost.
    /// </para>
    /// </remarks>
    private static string BuildPreview(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return string.Empty;
        }

        var flattened = payload.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();

        return flattened.Length <= PreviewMaxLength
            ? flattened
            : flattened[..PreviewMaxLength] + "…";
    }
}
