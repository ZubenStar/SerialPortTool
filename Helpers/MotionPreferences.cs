using Windows.UI.ViewManagement;

namespace SerialPortTool.Helpers;

/// <summary>
/// The app's single read of the system animation preference.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why one read.</b> The same answer decides two things in different places: <c>App.OnLaunched</c>
/// zeroes the shared hover/press <c>BrushTransition</c> before any element exists, and
/// <c>MainWindow</c> gates the sidebar fold, the appearance crossfade and the baud-rate banner on it.
/// Two independent <c>new UISettings()</c> reads would be two chances to disagree about the same
/// machine-level setting — a build where the fades are off but the hover transition is on is
/// unexplainable from the settings alone.
/// </para>
/// <para>
/// <b>Failing to read it assumes animations are on</b>, the choice <c>MainWindow</c> made before this
/// type existed: <c>UISettings</c> is a system API that locked-down or unusual configurations can
/// refuse, and a refused read must never turn into "the app has no hover feedback" — or worse, into a
/// startup failure.
/// </para>
/// </remarks>
internal static class MotionPreferences
{
    /// <summary>
    /// Whether Windows is currently willing to animate. Read once, at first touch, and stable for
    /// the process: the transitions only matter while the window is on screen, and re-reading on
    /// every animation would put a system call on an input-sized path.
    /// </summary>
    public static bool AnimationsEnabled { get; } = ReadAnimationsEnabled();

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
