using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace SerialPortTool.Controls;

/// <summary>
/// Shared input-state helpers for the controls and the window.
/// </summary>
/// <remarks>
/// <para>
/// Lives in <c>Controls/</c> rather than <c>Helpers/</c> on purpose: it depends on
/// <c>Microsoft.UI.Input</c>, so it is not UI-free and therefore cannot be linked into the
/// logic-layer test project (that project links UI-free sources only — see AGENTS.md, "Automated
/// tests").
/// </para>
/// <para>
/// One definition for the whole app. This question used to be asked three ways: a private copy on the
/// window, another on the log list, and a third inline read in the list's key handler. A modifier
/// query is exactly the kind of thing that silently drifts when it is copied.
/// </para>
/// </remarks>
internal static class InputModifiers
{
    /// <summary>是否按住了 Ctrl（询问当前 UI 线程的键盘状态）。</summary>
    /// <remarks>
    /// Read explicitly rather than from the event's own modifier state, the way AGENTS.md prescribes
    /// for any window-level gesture that needs a modifier.
    /// </remarks>
    internal static bool IsControlDown()
        => InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
}
