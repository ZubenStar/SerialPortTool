using Microsoft.UI.Xaml;
using System.Numerics;
using Windows.UI.ViewManagement;

namespace SerialPortTool.Helpers;

/// <summary>
/// The app's single read of the system animation preference, plus the one thing that cannot be
/// expressed declaratively: taking back the depth that markup already applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one read.</b> v2.5.2 added two things that have to be switchable off — the shared
/// hover/press brush transition and the floating surfaces' shadow — and both are decided before any
/// element exists (the transition is zeroed in <c>App.OnLaunched</c>, the shadows are stripped in
/// each control's constructor). Three independent <c>new UISettings()</c> reads would be three
/// chances to disagree about the same machine-level setting; this type is that read, once.
/// </para>
/// <para>
/// <b>Failing to read it assumes animations are on</b>, the same choice <c>MainWindow</c> already
/// made: <c>UISettings</c> is a system API that locked-down or unusual configurations can refuse,
/// and a refused read must never turn into "the app has no hover feedback" — or worse, into a
/// startup failure. The failure is silent by design; there is nothing here the user could act on.
/// </para>
/// </remarks>
internal static class MotionPreferences
{
    /// <summary>
    /// Whether Windows is currently willing to animate. Read once, at first touch, and stable for
    /// the process: the app is a desktop tool whose transitions only matter while the window is on
    /// screen, and re-reading on every animation would add a system call to an input-sized path.
    /// </summary>
    public static bool AnimationsEnabled { get; } = ReadAnimationsEnabled();

    /// <summary>
    /// Removes the depth a floating surface was given in markup when the system preference is off.
    /// </summary>
    /// <param name="surface">The element the markup gave <c>Shadow</c> and a Z translation.</param>
    /// <remarks>
    /// A shadow is depth rather than motion, so this is a judgement call rather than a rule the
    /// framework enforces — but "animations off" is the switch a user flips when they want a UI
    /// without decorative extras, and a ThemeShadow is a composition cost for as long as its element
    /// is alive. Keeping the two on the same switch means one setting explains everything that is
    /// missing, instead of two settings that each half-explain it.
    /// </remarks>
    public static void StripDepthIfDisabled(UIElement surface)
    {
        if (AnimationsEnabled)
        {
            return;
        }

        surface.Shadow = null;
        surface.Translation = Vector3.Zero;
    }

    private static bool ReadAnimationsEnabled()
    {
        try
        {
            return new UISettings().AnimationsEnabled;
        }
        catch (System.Exception ex)
        {
            // Logged rather than silently absorbed: "the fades are on and I did not ask for them"
            // is exactly the report this line answers, and the failure would otherwise leave no
            // trace anywhere. Serilog is configured in the App constructor, which runs before
            // anything can touch this type.
            Serilog.Log.Warning(ex, "Could not read the system animation preference; assuming animations are enabled");
            return true;
        }
    }
}
