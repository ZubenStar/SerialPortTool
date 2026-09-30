using Microsoft.UI.Xaml.Controls;
using System;

namespace SerialPortTool.Controls;

/// <summary>
/// Shared list-navigation rules for the two keyboard-first overlays — the F2 command palette and the
/// send-history panel.
/// </summary>
/// <remarks>
/// <para>
/// Plain static methods rather than a shared XAML base class, deliberately. AGENTS.md records that
/// adding one new XAML control once made <c>XamlCompiler</c> fail with no diagnostic at all
/// (<c>MSB3073</c>), so the two overlays keep their own <c>UserControl</c> and root element and share
/// only the navigation rules — which is the part that actually has to agree between them.
/// </para>
/// <para>
/// The rules: only one row is selected at a time, arrow keys clamp at both ends instead of wrapping
/// (wrapping in a long result list is how a user loses their place), and Enter with nothing selected
/// means the first row.
/// </para>
/// </remarks>
internal static class OverlayNavigation
{
    /// <summary>把选择移动 <paramref name="delta"/> 行，并把目标行滚动到可见位置。</summary>
    internal static void MoveSelection(ListView list, int itemCount, int delta)
    {
        if (itemCount == 0)
        {
            return;
        }

        var next = list.SelectedIndex < 0
            ? 0
            : Math.Clamp(list.SelectedIndex + delta, 0, itemCount - 1);

        list.SelectedIndex = next;
        list.ScrollIntoView(list.Items[next]);
    }

    /// <summary>返回「当前应激活的行」的下标；没有任何可选行时返回 <c>-1</c>。</summary>
    internal static int ResolveActivationIndex(ListView list, int itemCount)
    {
        var index = list.SelectedIndex;

        // Enter with nothing selected means "the top row", which is what a user who typed a query and
        // hit Enter expects. An empty result set has nothing to activate.
        if (index < 0 && itemCount > 0)
        {
            index = 0;
        }

        return index >= 0 && index < itemCount ? index : -1;
    }
}
