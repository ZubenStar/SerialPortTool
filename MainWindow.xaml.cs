using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SerialPortTool.Core.Enums;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using SerialPortTool.Services;
using SerialPortTool.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace SerialPortTool;

/// <summary>
/// Main window for the Serial Port Tool application
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Below this client width the configuration rail folds itself away.</summary>
    private const int SidebarAutoCollapseWidth = 900;

    private const int SidebarAnimationMs = 150;

    /// <summary>Length of the appearance crossfade that softens a light/dark switch.</summary>
    private const int ThemeTransitionMs = 180;

    /// <summary>Reveal / dismiss lengths for panel-like surfaces (currently the baud-rate banner).</summary>
    private const int PanelRevealMs = 160;
    private const int PanelDismissMs = 120;

    /// <summary>Fallback when the design token cannot be read; mirrors AppSidebarWidth in Tokens.xaml.</summary>
    private const double SidebarWidthFallback = 272;

    public MainViewModel ViewModel { get; }

    // Update-related services. Resolved from the container (Window has a parameterless ctor for XAML).
    private readonly IUpdateService _updateService;
    private readonly IUpdateInstallerService _updateInstallerService;
    private readonly ISettingsService _settingsService;
    private readonly INotificationService _notifications;

    // Shell state.
    private AppThemePreference _themePreference = AppThemePreference.System;
    private readonly Storyboard _sidebarStoryboard = new();
    private double _sidebarAnimationTarget;
    private bool _sidebarAppliedCollapsed;
    private bool _animationsEnabled = true;

    /// <summary>Darkness the shell last applied — the crossfade only runs when this actually flips.</summary>
    private bool _appliedEffectiveIsDark;

    /// <summary>False until <see cref="UpdateEffectiveTheme"/> has run once, so startup never fades.</summary>
    private bool _hasAppliedEffectiveTheme;

    /// <summary>
    /// Bumped every time the baud-rate banner is shown. A fade-out that is still in flight is
    /// cancelled by comparing against it, so it cannot collapse a banner that was just re-shown.
    /// </summary>
    private int _alertAnimationGeneration;

    /// <summary>
    /// Darkness the current <see cref="MicaBackdrop"/> was created for; <c>null</c> when no material
    /// is in use. Prevents the backdrop being reassigned while it is still connecting at startup.
    /// </summary>
    private bool? _appliedBackdropDark;

    // 启动静默检查的延迟，避免与窗口初始化 / 串口扫描抢资源
    private static readonly TimeSpan SilentUpdateCheckDelay = TimeSpan.FromSeconds(5);

    // 运行期补查周期：程序长时间开着时也能发现新版本（启动检查只在启动那一刻联网）
    private static readonly TimeSpan RuntimeUpdateCheckInterval = TimeSpan.FromHours(24);

    // 保证同一时刻只有一个更新相关对话框（WinUI 3 不允许并发 ContentDialog）
    private bool _isUpdateDialogOpen;
    private bool _silentUpdateCheckStarted;

    // 运行期补查定时器；随窗口关闭停止
    private DispatcherQueueTimer? _runtimeUpdateCheckTimer;
    private bool _silentUpdateCheckRunning;

    public MainWindow()
    {
        // IMPORTANT: Get ViewModel BEFORE InitializeComponent for x:Bind to work
        ViewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        _updateService = App.Current.Services.GetRequiredService<IUpdateService>();
        _updateInstallerService = App.Current.Services.GetRequiredService<IUpdateInstallerService>();
        _settingsService = App.Current.Services.GetRequiredService<ISettingsService>();
        _notifications = App.Current.Services.GetRequiredService<INotificationService>();

        InitializeComponent();

        // Set window properties
        Title = ViewModel.Title;

        // Set window icon
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Images", "logo.ico");
        if (System.IO.File.Exists(iconPath))
        {
            this.AppWindow.SetIcon(iconPath);
        }

        // Set window size
        var appWindow = this.AppWindow;
        if (appWindow != null)
        {
            appWindow.Resize(new Windows.Graphics.SizeInt32(1280, 840));
        }

        // ---------------------------------------------------------------------------------
        // Shell: appearance, title bar, sidebar.
        //
        // Order matters. The appearance is applied before the ViewModel subscriptions below so the
        // first painted frame already uses the saved palette (App.OnLaunched read the setting before
        // this window was resolved), and before SetupTitleBar so the caption buttons get coloured on
        // the same pass.
        // ---------------------------------------------------------------------------------
        // UISettings is a system API and can be refused on locked-down or unusual configurations;
        // assuming animations are enabled is a far better outcome than failing to start.
        try
        {
            _animationsEnabled = new UISettings().AnimationsEnabled;
        }
        catch (Exception ex)
        {
            _animationsEnabled = true;
            Serilog.Log.Warning(ex, "Could not read the system animation preference; assuming animations are enabled");
        }

        ViewModel.InitializeThemePreference(App.Current.InitialThemePreference);
        _themePreference = ViewModel.ThemePreference;
        ApplyThemePreference(_themePreference);

        SetupBackdrop();
        SetupTitleBar();

        _sidebarAppliedCollapsed = IsSidebarCollapsedEffective();
        ApplySidebarWidth(_sidebarAppliedCollapsed ? 0 : GetSidebarWidth(), _sidebarAppliedCollapsed, animate: false);

        // ActualTheme may settle after RequestedTheme is assigned (and it changes again when the user
        // flips Windows between light and dark while we are on "follow the system").
        RootLayout.ActualThemeChanged += (_, _) => UpdateEffectiveTheme();

        _sidebarStoryboard.Completed += (_, _) =>
        {
            _sidebarStoryboard.Stop();
            ApplySidebarWidth(_sidebarAnimationTarget, _sidebarAnimationTarget <= 0, animate: false);
        };

        // NOTE: log-list auto-scroll, copy, multi-select, and keyboard shortcuts are now
        // self-contained inside the LogListView UserControl. We only handle the CopyCompleted
        // event below to push a status message into the ViewModel.

        // Subscribe to ViewModel property changes for match count
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        // Subscribe to baud rate suggestion events
        ViewModel.BaudRateSuggested += ViewModel_BaudRateSuggested;

        // Initialize baud rate alert UI
        InitializeBaudRateAlert();

        // Initialize custom baud rate UI based on saved settings
        InitializeCustomBaudRateUI();

        // ---------------------------------------------------------------------------------
        // Window-level keyboard shortcuts.
        //
        // Handled with a plain KeyDown on the root content Grid rather than a
        // KeyboardAccelerator. Accelerators were tried first and never fired in this app:
        // Ctrl+Shift+L and Ctrl+Alt+L both did nothing at all while the same keys work in other
        // programs, and Alt is the menu-activation key, which a MenuBar is entitled to consume
        // first. KeyDown on the element that spans the window is the mechanism the log list
        // already uses successfully for Ctrl+C / Ctrl+A, so it is the known-good path here.
        //
        // handledEventsToo: true is required - the search box and the send box mark their own
        // (unrelated) editing keys as handled, and that must not swallow this gesture.
        // ---------------------------------------------------------------------------------
        RootLayout.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(OnRootKeyDown),
            handledEventsToo: true);

        // 窗口首次激活后再启动静默更新检查（此时 Content.XamlRoot 才可用）
        Activated += OnFirstActivated;

        // 运行期补查定时器随窗口关闭一并停止，避免窗口销毁后仍去弹对话框
        Closed += (_, _) => StopRuntimeUpdateCheckTimer();

    }

    private void InitializeCustomBaudRateUI()
    {
        // Update UI based on UseCustomBaudRate setting after a short delay
        // to allow ViewModel initialization to complete
        DispatcherQueue.TryEnqueue(() =>
        {
            bool useCustom = ViewModel.UseCustomBaudRate;
            BaudRateComboBox.Visibility = useCustom ? Visibility.Collapsed : Visibility.Visible;
            CustomBaudRateTextBox.Visibility = useCustom ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.MatchCount) ||
            e.PropertyName == nameof(ViewModel.IsRegexValid) ||
            e.PropertyName == nameof(ViewModel.RegexErrorMessage) ||
            e.PropertyName == nameof(ViewModel.SearchText))
        {
            UpdateSearchDiagnostics();
        }
        else if (e.PropertyName == nameof(ViewModel.ThemePreference))
        {
            // The ViewModel owns the preference (and therefore persistence); applying it is the
            // window's job because the window owns the visual tree.
            ApplyThemePreference(ViewModel.ThemePreference);
        }
        else if (e.PropertyName == nameof(ViewModel.IsSidebarCollapsed))
        {
            UpdateResponsiveSidebar(animate: true);
        }
    }

    /// <summary>
    /// Keeps the inline diagnostics beside the search box in step with the committed query: a regex
    /// that does not parse owns the line (glyph + message), otherwise the match count shows as soon
    /// as something is actually applied.
    /// </summary>
    /// <remarks>
    /// The previous version assigned hand-built <c>SolidColorBrush</c>s (literal green / red) to
    /// <c>SearchBox.BorderBrush</c>. That hard-coded a light-theme colour and stomped the themed
    /// border; the inline message plus its warning glyph is a stronger, keyboard- and
    /// screen-reader-visible signal that also survives an appearance change. Text mode is always
    /// valid, so an error can only ever come from the regex switch.
    /// </remarks>
    private void UpdateSearchDiagnostics()
    {
        if (!ViewModel.IsRegexValid)
        {
            RegexErrorTextBlock.Text = ViewModel.RegexErrorMessage;
            RegexErrorIcon.Visibility = Visibility.Visible;
            RegexErrorTextBlock.Visibility = Visibility.Visible;
            MatchCountTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        RegexErrorIcon.Visibility = Visibility.Collapsed;
        RegexErrorTextBlock.Visibility = Visibility.Collapsed;

        MatchCountTextBlock.Text = $"匹配 {ViewModel.MatchCount} 条";
        MatchCountTextBlock.Visibility = string.IsNullOrEmpty(ViewModel.SearchText)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    #region Appearance

    private void ApplyThemePreference(AppThemePreference preference)
    {
        _themePreference = preference;

        RootLayout.RequestedTheme = preference switch
        {
            AppThemePreference.Light => ElementTheme.Light,
            AppThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        SyncThemeMenu();
        UpdateEffectiveTheme();
    }

    /// <summary>
    /// Pushes the resolved appearance down to the system title bar buttons and the ViewModel.
    /// </summary>
    private void UpdateEffectiveTheme()
    {
        // Sampled from ActualTheme, not from the preference: "follow the system" only becomes
        // light or dark once the element has actually been themed.
        var isDark = RootLayout.ActualTheme == ElementTheme.Dark;

        // Only a real flip is worth a transition, and never the first resolution: the shell applies
        // the saved appearance before the window is shown, and fading that would put a pointless
        // flash on every launch of a dark-theme install.
        var changed = _hasAppliedEffectiveTheme && _appliedEffectiveIsDark != isDark;
        _hasAppliedEffectiveTheme = true;
        _appliedEffectiveIsDark = isDark;

        ApplyTitleBarColors(isDark);
        RefreshBackdropTheme(isDark);
        ViewModel.ApplyEffectiveTheme(isDark);

        if (changed)
        {
            RunThemeTransition();
        }
    }

    /// <summary>
    /// Softens an appearance switch with a short crossfade of the whole content root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A dip from ~70% back to full opacity rather than a fade through the *other* palette's
    /// background: under Mica the root has no background at all (it is cleared so the material shows
    /// through — see <see cref="SetupBackdrop"/>), so an overlay colour would be wrong on exactly the
    /// machines that render the material, and a full 0→1 fade reads as a blink.
    /// </para>
    /// <para>
    /// <c>Opacity</c> is composition-backed, so this needs no <c>EnableDependentAnimation</c> and the
    /// UI thread only pays for the assignment. Skipped entirely when the system reports animations
    /// disabled — the same guard the sidebar width animation uses. Note the system caption buttons
    /// are painted outside this tree, so they still change instantly; only the app's own chrome and
    /// content cross-fade.
    /// </para>
    /// </remarks>
    private void RunThemeTransition()
    {
        if (!_animationsEnabled)
        {
            return;
        }

        RunOpacityFade(RootLayout, from: 0.7, to: 1.0, ThemeTransitionMs, EasingMode.EaseOut);
    }

    /// <summary>
    /// Re-creates the backdrop so a light material cannot linger after an appearance switch (and the
    /// reverse).
    /// </summary>
    /// <remarks>
    /// Guarded twice on purpose. It must not reassign the material while the one created at startup is
    /// still connecting — <see cref="SetupBackdrop"/> records the darkness it was created for, so the
    /// <c>ActualThemeChanged</c> that fires right after startup is a no-op — and it must not run at all
    /// when no material is in use, which is the machine class the opaque fallback exists for.
    /// </remarks>
    private void RefreshBackdropTheme(bool isDark)
    {
        if (SystemBackdrop is not MicaBackdrop || !MicaController.IsSupported())
        {
            _appliedBackdropDark = null;
            return;
        }

        if (_appliedBackdropDark == isDark)
        {
            return;
        }

        GuardShellStep(nameof(RefreshBackdropTheme), () =>
        {
            _appliedBackdropDark = isDark;
            SystemBackdrop = new MicaBackdrop();
        });
    }

    private void SyncThemeMenu()
    {
        // Null-guarded because these live inside a MenuBarItem: the generated fields exist, but a
        // flyout's content is only guaranteed to be realised once the menu has been opened. A missing
        // check mark degrades the menu; a NullReferenceException here takes down the whole window
        // before it is ever shown.
        if (ThemeSystemMenuItem is not null)
        {
            ThemeSystemMenuItem.IsChecked = _themePreference == AppThemePreference.System;
        }

        if (ThemeLightMenuItem is not null)
        {
            ThemeLightMenuItem.IsChecked = _themePreference == AppThemePreference.Light;
        }

        if (ThemeDarkMenuItem is not null)
        {
            ThemeDarkMenuItem.IsChecked = _themePreference == AppThemePreference.Dark;
        }
    }

    private void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string tag } &&
            Enum.TryParse<AppThemePreference>(tag, out var preference))
        {
            // Assigning the property raises PropertyChanged, which lands back in
            // ViewModel_PropertyChanged and applies the theme. One code path, not two.
            ViewModel.ThemePreference = preference;
        }
    }

    #endregion

    #region Window shell (title bar + sidebar)

    /// <summary>
    /// Runs a window-shell step that depends on OS, policy or hardware capabilities, logging and
    /// carrying on when the platform refuses it.
    /// </summary>
    /// <remarks>
    /// <c>AppWindow.TitleBar</c>, <c>SystemBackdrop</c> and <c>ExtendsContentIntoTitleBar</c> are the
    /// most capability-sensitive APIs in the app: unavailable on Windows 10, disableable by policy or
    /// by the "transparency effects" setting, and known to throw on some virtualised GPUs. None of them
    /// is load-bearing — the window is fully usable without a custom title bar or a material — so a
    /// failure here must degrade the chrome, never take the window down before it is ever shown.
    /// </remarks>
    private void GuardShellStep(string step, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Window shell step {Step} failed; continuing with the plain chrome", step);
        }
    }

    /// <summary>
    /// Applies the translucent material behind the chrome when the machine can render it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback is deliberately "do nothing" rather than "clear the background": XAML declares
    /// <c>AppWindowBackgroundBrush</c> on the root, and a transparent root with no material renders
    /// uninitialised memory. Mica is unavailable on Windows 10, when transparency effects are
    /// switched off, and on some virtualised GPUs — all of which the minimum supported platform
    /// allows.
    /// </para>
    /// <para>
    /// Only the chrome goes transparent; the rail, toolbar, log surface and status bar stay opaque so
    /// the monospace body keeps its contrast.
    /// </para>
    /// </remarks>
    private void SetupBackdrop()
    {
        if (!MicaController.IsSupported())
        {
            SystemBackdrop = null;
            _appliedBackdropDark = null;
            return;
        }

        GuardShellStep(nameof(SetupBackdrop), () =>
        {
            SystemBackdrop = new MicaBackdrop();

            // Records the darkness the material was created for, so the ActualThemeChanged that fires
            // immediately after startup does not replace it again while it is still connecting.
            _appliedBackdropDark = RootLayout.ActualTheme == ElementTheme.Dark;

            // Cleared last so a material that cannot be created leaves the opaque brush in place.
            RootLayout.Background = null;
        });
    }

    /// <summary>
    /// Opts into the custom (extended) title bar.
    /// </summary>
    /// <remarks>
    /// <c>AppWindowTitleBar.IsCustomizationSupported()</c> is false on Windows 10, where the caption
    /// buttons cannot be re-coloured and interactive content inside the drag region is not supported.
    /// In that case we deliberately keep the system title bar: <c>AppTitleBar</c> then reads as the
    /// app's own header band and <c>TitleBarInsetSpacer</c> stays at zero width.
    /// </remarks>
    private void SetupTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            TitleBarInsetSpacer.Width = 0;
            return;
        }

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            // A half-applied extension is worse than none: the caption would be gone with no drag
            // region, leaving a window the user cannot move. Roll back to the system title bar.
            RollBackTitleBarExtension();
            Serilog.Log.Warning(ex, "Custom title bar unavailable; keeping the system title bar");
            return;
        }

        ApplyTitleBarColors(RootLayout.ActualTheme == ElementTheme.Dark);
        AppWindow.Changed += OnAppWindowChanged;
    }

    private void RollBackTitleBarExtension()
    {
        try
        {
            ExtendsContentIntoTitleBar = false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not roll back the extended title bar");
        }

        TitleBarInsetSpacer.Width = 0;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange)
        {
            return;
        }

        GuardShellStep(nameof(OnAppWindowChanged), () =>
        {
            // Caption button metrics change with DPI and window state, so both the reserved space and
            // the responsive rail have to be recomputed rather than set once.
            TitleBarInsetSpacer.Width = sender.TitleBar.RightInset;
            UpdateResponsiveSidebar(animate: false);
        });
    }

    /// <summary>
    /// Colours the system-drawn caption buttons.
    /// </summary>
    /// <remarks>
    /// These are painted by the compositor outside our XAML tree, so nothing in Themes/Tokens.xaml
    /// reaches them. Without an explicit assignment the buttons keep the default (dark) glyph colour
    /// and vanish against the dark graphite caption.
    /// </remarks>
    private void ApplyTitleBarColors(bool isDark)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        GuardShellStep(nameof(ApplyTitleBarColors), () =>
        {
            var titleBar = AppWindow.TitleBar;
            var foreground = isDark ? Colors.White : Colors.Black;
            var inactive = isDark ? Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                                  : Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00);
            var hover = isDark ? Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                               : Windows.UI.Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            var pressed = isDark ? Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
                                 : Windows.UI.Color.FromArgb(0x24, 0x00, 0x00, 0x00);

            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = inactive;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonPressedForegroundColor = foreground;

            // Keep content clear of the caption buttons and of the (usually zero) left inset.
            TitleBarInsetSpacer.Width = titleBar.RightInset;
            AppTitleBar.Padding = new Thickness(12 + titleBar.LeftInset, 0, 0, 0);
        });
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsSidebarCollapsed = !ViewModel.IsSidebarCollapsed;
        UpdateResponsiveSidebar(animate: true);
    }

    private bool IsSidebarCollapsedEffective()
        => ViewModel.IsSidebarCollapsed || AppWindow.Size.Width < SidebarAutoCollapseWidth;

    /// <summary>
    /// Reconciles the rail with the persisted preference and the current window width.
    /// </summary>
    /// <remarks>
    /// Narrowing the window folds the rail away without touching the preference, so widening it again
    /// restores whatever the user actually chose.
    /// </remarks>
    private void UpdateResponsiveSidebar(bool animate)
    {
        var collapsed = IsSidebarCollapsedEffective();
        if (collapsed == _sidebarAppliedCollapsed)
        {
            return;
        }

        _sidebarAppliedCollapsed = collapsed;
        ApplySidebarWidth(collapsed ? 0 : GetSidebarWidth(), collapsed, animate);
    }

    private void ApplySidebarWidth(double width, bool collapsed, bool animate)
    {
        if (!animate || !_animationsEnabled)
        {
            _sidebarStoryboard.Stop();
            SidebarHost.Width = width;
            SidebarHost.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        // Collapsing animates the width down; the element is only hidden once the animation reports
        // completion (see the Completed handler in the constructor).
        SidebarHost.Visibility = Visibility.Visible;
        _sidebarAnimationTarget = width;

        var animation = new DoubleAnimation
        {
            From = SidebarHost.Width,
            To = width,
            Duration = new Duration(TimeSpan.FromMilliseconds(SidebarAnimationMs)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            // Width drives layout, so the compositor cannot run this on its own thread.
            EnableDependentAnimation = true
        };

        Storyboard.SetTarget(animation, SidebarHost);
        Storyboard.SetTargetProperty(animation, "Width");

        _sidebarStoryboard.Children.Clear();
        _sidebarStoryboard.Children.Add(animation);
        _sidebarStoryboard.Begin();
    }

    private static double GetSidebarWidth()
    {
        if (Application.Current.Resources.TryGetValue("AppSidebarWidth", out var value) &&
            value is double width &&
            width > 0)
        {
            return width;
        }

        return SidebarWidthFallback;
    }

    #endregion

    #region Dialog factory

    /// <summary>
    /// Creates an app dialog that follows the current appearance.
    /// </summary>
    /// <remarks>
    /// A ContentDialog is hosted in its own popup root, so it does not inherit the window root's
    /// <c>ElementTheme</c>. Without this the dialogs stay light while the window is dark.
    /// </remarks>
    private ContentDialog CreateDialog() => new()
    {
        XamlRoot = Content.XamlRoot,
        RequestedTheme = RootLayout.RequestedTheme
    };

    #endregion

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDialog();
        dialog.Title = "关于 SerialPortTool";
        dialog.CloseButtonText = "确定";
        dialog.DefaultButton = ContentDialogButton.Close;

        var stackPanel = new StackPanel
        {
            Spacing = 12,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0)
        };

        // App Icon/Title
        var titleText = new TextBlock
        {
            Text = "串口工具 - Multi-Port Serial Monitor",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center
        };
        stackPanel.Children.Add(titleText);

        // Version
        var versionText = new TextBlock
        {
            Text = $"版本: {VersionInfo.Version}",
            FontSize = 14,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center
        };
        stackPanel.Children.Add(versionText);

        // Build Time
        var buildText = new TextBlock
        {
            Text = $"构建时间: {VersionInfo.BuildTime}",
            FontSize = 12,
            Opacity = 0.8,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center
        };
        stackPanel.Children.Add(buildText);

        // Separator
        var separator = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
            Opacity = 0.3,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 8)
        };
        stackPanel.Children.Add(separator);

        // Description
        var descText = new TextBlock
        {
            Text = "一个功能强大的多端口串口监视工具\n支持高速数据传输和实时日志记录",
            FontSize = 12,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        };
        stackPanel.Children.Add(descText);

        // Features
        var featuresText = new TextBlock
        {
            Text = "✓ 多端口同时监控\n✓ 支持高达6Mbps波特率\n✓ 正则表达式搜索\n✓ 自动文件日志记录\n✓ 实时数据统计",
            FontSize = 11,
            Opacity = 0.8,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Left,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0)
        };
        stackPanel.Children.Add(featuresText);

        // Copyright
        var copyrightText = new TextBlock
        {
            Text = $"© {DateTime.Now.Year} SerialPortTool",
            FontSize = 10,
            Opacity = 0.6,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 12, 0, 0)
        };
        stackPanel.Children.Add(copyrightText);

        dialog.Content = stackPanel;
        await dialog.ShowAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Exit();
    }

    // =============================================================================================
    // v2.5.0 — port notes (F6), presets (F3), send history panel (F8)
    // =============================================================================================

    /// <summary>
    /// 编辑一个串口的备注 / 标签 / 分组。
    /// </summary>
    /// <remarks>
    /// Built in code through <see cref="CreateDialog"/>, like the quick-send editor. A separate port picker
    /// rather than a button inside the row: clicking anything inside a <c>ListViewItem</c> selects that
    /// item, and here selection IS "open the port" — a per-row edit button would open a port as a side
    /// effect of trying to rename it.
    /// </remarks>
    private async void EditPortNotes_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.AvailablePorts.Count == 0)
        {
            ViewModel.StatusMessage = "还没有扫描到串口，先点「扫描」";
            return;
        }

        var portBox = new ComboBox
        {
            Header = "串口",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 240,
        };

        // An explicit template: a ComboBox over AvailablePortItem would otherwise render the CLR type name,
        // which is the lesson recorded on SerialParameterOption<T>.ToString.
        portBox.ItemTemplate = (Microsoft.UI.Xaml.DataTemplate)RootLayout.Resources["PortNameItemTemplate"];
        portBox.ItemsSource = ViewModel.AvailablePorts;
        portBox.SelectedIndex = 0;

        var notesBox = new TextBox { Header = "备注", PlaceholderText = "例如：实验台左侧那台传感器" };
        var tagsBox = new TextBox { Header = "标签（逗号或空格分隔）", PlaceholderText = "例如：温度, 现场" };
        var groupBox = new TextBox { Header = "分组（可留空）", PlaceholderText = "例如：实验台" };

        void LoadFor(AvailablePortItem item)
        {
            notesBox.Text = item.Notes;
            tagsBox.Text = item.Tags;
            groupBox.Text = item.Group;
        }

        LoadFor(ViewModel.AvailablePorts[0]);
        portBox.SelectionChanged += (_, _) =>
        {
            if (portBox.SelectedItem is AvailablePortItem item)
            {
                LoadFor(item);
            }
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(portBox);
        panel.Children.Add(notesBox);
        panel.Children.Add(tagsBox);
        panel.Children.Add(groupBox);

        var dialog = CreateDialog();
        dialog.Title = "端口备注";
        dialog.Content = panel;
        dialog.PrimaryButtonText = "保存";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (portBox.SelectedItem is not AvailablePortItem selected)
        {
            return;
        }

        await ViewModel.SavePortMetadataAsync(selected.PortName, notesBox.Text, tagsBox.Text, groupBox.Text);
    }

    private async void ApplyPortPreset_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ApplyPortPresetAsync(ViewModel.SelectedPortPreset);

    private async void DeletePortPreset_Click(object sender, RoutedEventArgs e)
        => await ViewModel.DeletePortPresetAsync(ViewModel.SelectedPortPreset);

    /// <summary>
    /// 把当前参数存成一套命名档案。
    /// </summary>
    /// <remarks>
    /// A name is asked for rather than derived: a preset named after its own baud rate is indistinguishable
    /// from another one, and nobody renames things later. The user's job is one word; the preset's job is
    /// remembering everything else.
    /// </remarks>
    private async void SavePortPreset_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            Header = "档案名称",
            PlaceholderText = "例如：调试台 921600 8N1",
            MaxLength = PortPreset.MaxNameLength,
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(nameBox);

        var dialog = CreateDialog();
        dialog.Title = "保存配置档案";
        dialog.Content = panel;
        dialog.PrimaryButtonText = "保存";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.SavePortPresetAsync(nameBox.Text);
    }

    private void OpenSendHistory_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenSendHistoryPanel();
        HistoryPanel.Open();
    }

    private void HistoryPanel_Activated(object? sender, string payload)
        => ViewModel.UseSendHistoryEntry(payload);

    private async void HistoryPanel_EntryDeleted(object? sender, string payload)
        => await ViewModel.RemoveSendHistoryEntryAsync(payload);

    private async void HistoryPanel_ClearRequested(object? sender, EventArgs e)
        => await ViewModel.ClearSendHistoryAsync();

    private async void PortListView_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        // AvailablePortItem since v2.5.0 (the row carries device metadata and notes, not just a name). The
        // pattern match is over `object`, so this compiled unchanged and simply stopped opening anything
        // when the element type moved — hence the explicit two-step below.
        //
        // SelectedItem is cleared right after, which re-raises this same handler; the type guard is what
        // keeps that second pass a no-op, exactly as the old `is string` check did.
        if (sender is Microsoft.UI.Xaml.Controls.ListView listView &&
            listView.SelectedItem is AvailablePortItem item &&
            !string.IsNullOrEmpty(item.PortName))
        {
            await ViewModel.OpenPortCommand.ExecuteAsync(item.PortName);
            listView.SelectedItem = null; // Deselect after opening
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendAsync();
    }

    /// <summary>
    /// Enter in the send box sends, and ↑ / ↓ walk the send history.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text box is single-line, so Enter carries no other meaning here. The event is marked handled
    /// so the press cannot also reach a default button.
    /// </para>
    /// <para>
    /// ↑ / ↓ are taken over for the same reason the log list takes over its own wheel gestures rather than
    /// relying on the framework: in a single-line box the arrow keys only move the caret, which is
    /// meaningless, while the recall list is what the user is actually reaching for. The walk itself lives
    /// on the ViewModel, so it can be exercised without a window; this handler only decides that a key
    /// means "recall" and keeps the caret where the user expects it.
    /// </para>
    /// </remarks>
    private async void SendTextBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                e.Handled = true;
                await SendAsync();
                break;

            // Handled only when something was actually recalled, so an empty history leaves the arrow key
            // as an ordinary caret movement rather than swallowing it.
            case Windows.System.VirtualKey.Up:
                if (ViewModel.RecallOlderSendText() is not null)
                {
                    e.Handled = true;
                    MoveSendCaretToEnd();
                }
                break;

            case Windows.System.VirtualKey.Down:
                if (ViewModel.RecallNewerSendText() is not null)
                {
                    e.Handled = true;
                    MoveSendCaretToEnd();
                }
                break;
        }
    }

    /// <summary>
    /// Puts the caret after the recalled text, so typing continues from what was just recalled.
    /// </summary>
    /// <remarks>
    /// Assigning <c>Text</c> from code leaves the selection at the start of the box, which would silently
    /// prepend the next keystroke to the payload.
    /// </remarks>
    private void MoveSendCaretToEnd()
    {
        SendTextBox.SelectionStart = SendTextBox.Text.Length;
        SendTextBox.SelectionLength = 0;
    }

    private async Task SendAsync()
    {
        // Sends broadcast to every open port (see MainViewModel.SendAsync), so there is nothing to sync
        // from the selected row here.
        await ViewModel.SendCommand.ExecuteAsync(null);
    }

    #region Quick-send library

    /// <summary>One-click send of a saved snippet, through the same path as the send box.</summary>
    private async void SendSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SendSnippet snippet })
        {
            return;
        }

        // Closed first: the send is asynchronous and its result (status bar / notification) would be
        // hidden behind an open flyout.
        QuickSendFlyout.Hide();
        await ViewModel.SendSnippetAsync(snippet);
    }

    /// <summary>Deletes a snippet from the library.</summary>
    /// <remarks>
    /// The flyout deliberately stays open: removing one entry next to the others is ordinary editing,
    /// and closing the menu on every delete would make tidying a library tedious.
    /// </remarks>
    private async void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SendSnippet snippet })
        {
            return;
        }

        await ViewModel.RemoveSnippetAsync(snippet);
    }

    /// <summary>
    /// Saves the send box's current contents as a snippet, after asking for a name and a group.
    /// </summary>
    /// <remarks>
    /// Built in code and shown through <see cref="CreateDialog"/>, following the About / update
    /// dialog pattern: a <c>ContentDialog</c> lives in its own popup root, so it does not inherit the
    /// window root's <c>ElementTheme</c> and would stay light in dark mode without that call.
    /// </remarks>
    private async void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.SendText))
        {
            ViewModel.StatusMessage = "发送框为空：先在下方输入要保存的内容";
            return;
        }

        const int labelPrefillMaxLength = 40;
        var flattened = ViewModel.SendText.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var labelPrefill = flattened.Length <= labelPrefillMaxLength
            ? flattened
            : flattened[..labelPrefillMaxLength] + "…";

        var labelBox = new TextBox
        {
            Header = "名称",
            PlaceholderText = "显示在快捷指令列表里",
            Text = labelPrefill,
        };
        var groupBox = new TextBox
        {
            Header = "分组（可留空）",
            PlaceholderText = "例如：AT 指令",
        };
        var contentBox = new TextBox
        {
            Header = "内容",
            Text = ViewModel.SendText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
        };
        var hexBox = new CheckBox
        {
            Content = "按十六进制发送",
            IsChecked = ViewModel.SendAsHex,
        };
        var variablesBox = new CheckBox
        {
            Content = "解释 ${date} ${time} ${datetime} ${epoch} 与 \\r \\n \\t 转义",
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(labelBox);
        panel.Children.Add(groupBox);
        panel.Children.Add(contentBox);
        panel.Children.Add(hexBox);
        panel.Children.Add(variablesBox);

        var dialog = CreateDialog();
        dialog.Title = "保存快捷指令";
        dialog.Content = panel;
        dialog.PrimaryButtonText = "保存";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.AddSnippetAsync(new SendSnippet
        {
            Label = labelBox.Text,
            Group = groupBox.Text,
            Content = contentBox.Text,
            IsHex = hexBox.IsChecked == true,
            UseVariables = variablesBox.IsChecked == true,
        });
    }

    #endregion

    #region Keyword highlighting

    /// <summary>Deletes one highlight rule. The flyout stays open, like the quick-send list.</summary>
    private async void DeleteHighlightRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HighlightRule rule })
        {
            return;
        }

        await ViewModel.RemoveHighlightRuleAsync(rule);
    }

    /// <summary>Enables or disables one rule.</summary>
    /// <remarks>
    /// The write goes through the ViewModel rather than a TwoWay binding: enabling a rule has to
    /// recompile the matcher and persist, and a binding onto the non-observable persistence model
    /// could do neither.
    /// </remarks>
    private async void ToggleHighlightRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: HighlightRule rule } box)
        {
            return;
        }

        await ViewModel.SetHighlightRuleEnabledAsync(rule, box.IsChecked == true);
    }

    /// <summary>
    /// Asks for the pattern, the kind of matching and the colour, then adds the rule.
    /// </summary>
    /// <remarks>
    /// The colour list is the port-identity palette, so a highlight can deliberately be tied to a
    /// channel's colour and there is no second colour picker to keep in sync. The rows are composed in
    /// code (a swatch plus a name) instead of via <c>XamlReader</c>, which keeps the dialog readable
    /// and avoids a XAML string that the compiler cannot check. A rejected pattern is reported through
    /// the status bar by the ViewModel, which validates before compiling — so nothing broken is ever
    /// stored or compiled.
    /// </remarks>
    private async void AddHighlightRule_Click(object sender, RoutedEventArgs e)
    {
        var patternBox = new TextBox
        {
            Header = "匹配内容",
            PlaceholderText = @"例如 ERROR、TIMEOUT，或开启正则后的 \d+ms",
            // Prefilled from the current query because "highlight what I just searched for" is the
            // overwhelmingly common way this feature gets used.
            Text = ViewModel.SearchText,
        };
        var regexBox = new CheckBox { Content = "按正则表达式匹配" };
        var caseBox = new CheckBox { Content = "区分大小写" };

        var colorBox = new ComboBox
        {
            Header = "颜色",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 240,
        };

        foreach (var option in ViewModel.PortColorOptions)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                Background = option.Brush,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = option.Name,
                VerticalAlignment = VerticalAlignment.Center,
            });

            colorBox.Items.Add(row);
        }

        // Slots[2] — the palette's red — is the one a keyword rule almost always wants.
        colorBox.SelectedIndex = 2;

        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(patternBox);
        panel.Children.Add(regexBox);
        panel.Children.Add(caseBox);
        panel.Children.Add(colorBox);

        var dialog = CreateDialog();
        dialog.Title = "添加高亮规则";
        dialog.Content = panel;
        dialog.PrimaryButtonText = "添加";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Primary;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.AddHighlightRuleAsync(new HighlightRule
        {
            Pattern = patternBox.Text,
            IsRegex = regexBox.IsChecked == true,
            IsCaseSensitive = caseBox.IsChecked == true,
            ColorHex = ViewModel.PortColorOptions[colorBox.SelectedIndex].Hex,
            Enabled = true,
        });
    }

    #endregion

    private async void SelectTuningBin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".bin");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.SetTuningBinFilePathAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"选择 tuning bin 失败: {ex.Message}";
        }
    }

    private async void SelectTuningDescriptor_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".json");

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await ViewModel.SetTuningDescriptorFilePathAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"选择 tuning JSON 失败: {ex.Message}";
        }
    }

    private async void ClosePort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string portName)
        {
            await ViewModel.ClosePortCommand.ExecuteAsync(portName);
        }
    }

    /// <summary>Commits the draft as the active query (搜索 button).</summary>
    private void ExecuteSearch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ExecuteSearchCommand.Execute(null);
    }

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Enter is the only key that commits; anything else is just typing into the draft, which
        // deliberately filters nothing until it is committed.
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        ViewModel.ExecuteSearchCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Re-runs a query picked from the history flyout, then closes the flyout.</summary>
    private void ApplyRecentSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string searchText)
        {
            ViewModel.ApplyRecentSearchCommand.Execute(searchText);
            SearchHistoryFlyout.Hide();
        }
    }
    
    private void DeleteSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string searchText)
        {
            ViewModel.RemoveFromRecentSearches(searchText);
        }
    }

    private void ClearSearchText_Click(object sender, RoutedEventArgs e)
    {
        // Drops both the draft and the applied query, so the full log list comes straight back.
        ViewModel.ClearSearchCommand.Execute(null);

        // Focus the search box for user convenience
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void ClearSearchHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDialog();
        dialog.Title = "清空搜索历史";
        dialog.Content = "确定要清空所有搜索历史记录吗？";
        dialog.PrimaryButtonText = "确定";
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Close;
        
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.ClearSearchHistoryCommand.Execute(null);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => OpenLogFolder();

    /// <summary>
    /// Every window-level keyboard shortcut, resolved from one place. Reached for any key pressed
    /// while the window has focus, whichever control owns that focus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a <c>KeyDown</c> handler instead of a <c>KeyboardAccelerator</c>: accelerators
    /// never fired in this application (Ctrl+Shift+L and Ctrl+Alt+L both did nothing at all), and
    /// this is the mechanism the log list already uses for Ctrl+C / Ctrl+A. Keep new window-level
    /// gestures in this one method - it is the single place to look.
    /// </para>
    /// <para>
    /// <c>Handled</c> is set so the key does not continue through the tree; the shared
    /// <see cref="OpenLogFolder"/> body keeps the menu entry and the keyboard entry identical.
    /// The 工具 menu item carries its own <c>KeyboardAccelerator</c> for display, and it cannot
    /// double-fire with this handler: a flyout is a separate popup root, so its key events never
    /// travel through the window's element tree, and a MenuFlyoutItem only routes keys while its
    /// flyout is open.
    /// </para>
    /// </remarks>
    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Checked before the switch: the font-size gestures are the only ones here that need a modifier, and the
        // switch below reads e.Key alone.
        if (TryHandleLogFontSizeShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            // F2 rather than Ctrl+K / Ctrl+Shift+P: letter combinations get taken by IMEs and resident
            // tools in a Chinese-language session, and so did the KeyboardAccelerator route (see the note
            // where this handler is registered). F9 below is the same choice for the same reason.
            case Windows.System.VirtualKey.F2:
                e.Handled = true;
                ToggleCommandPalette();
                break;

            case Windows.System.VirtualKey.F9:
                e.Handled = true;
                OpenLogFolder();
                break;
        }
    }

    /// <summary>
    /// Ctrl+加号 / Ctrl+减号 / Ctrl+0 调整与重置日志字号。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked before the switch in <see cref="OnRootKeyDown"/> because it is the only gesture there that needs
    /// a modifier, and that switch reads <c>e.Key</c> alone.
    /// </para>
    /// <para>
    /// Both the numeric keypad (<c>Add</c> / <c>Subtract</c>) and the main row are accepted. The main row's
    /// <c>+</c> / <c>-</c> arrive as the unnamed OEM virtual keys 0xBB / 0xBD, hence the casts — requiring the
    /// numpad would make the shortcut look broken on a laptop keyboard. <c>Ctrl+=</c> lands on the same key as
    /// <c>Ctrl++</c>, which is the behaviour every app that supports this has.
    /// </para>
    /// </remarks>
    private bool TryHandleLogFontSizeShortcut(Windows.System.VirtualKey key)
    {
        if (!IsControlDown())
        {
            return false;
        }

        switch (key)
        {
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)0xBB: // VK_OEM_PLUS — '=' / '+'
                ViewModel.AdjustLogFontSize(1);
                return true;

            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)0xBD: // VK_OEM_MINUS — '-' / '_'
                ViewModel.AdjustLogFontSize(-1);
                return true;

            case Windows.System.VirtualKey.Number0:
            case Windows.System.VirtualKey.NumberPad0:
                ViewModel.ResetLogFontSize();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 是否按住了 Ctrl。
    /// </summary>
    /// <remarks>
    /// Read explicitly, the way AGENTS.md prescribes for any window-level gesture that needs a modifier and the
    /// way <c>LogListView</c> already does it for Ctrl+C / Ctrl+A.
    /// </remarks>
    private static bool IsControlDown()
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Opens the log directory in the shell. Shared by 工具 → 打开日志文件夹 and F9.
    /// </summary>
    /// <remarks>
    /// Failure stays silent (a debug trace only) by design: a missing folder or a refused shell
    /// launch is not worth interrupting log monitoring for.
    /// </remarks>
    private void OpenLogFolder()
    {
        try
        {
            var logDirectory = ViewModel.GetLogDirectory();
            if (!string.IsNullOrEmpty(logDirectory) && System.IO.Directory.Exists(logDirectory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error opening log folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Exports the current view — or just the selection, when there is one — to a file the user picks.
    /// </summary>
    /// <remarks>
    /// The snapshot is taken here, on the UI thread, before the first <c>await</c>. The bound display
    /// buffer may only be enumerated on this thread, and taking it up front also means the file
    /// describes one consistent moment instead of whatever happened to arrive while the picker was
    /// open.
    /// </remarks>
    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var selection = LogListView.GetSelectedEntries();

        // A selection means the user has already said which rows they want; exporting the whole view
        // from under them at that point would be ignoring the answer they just gave.
        IReadOnlyList<LogEntry> entries = selection.Count > 0
            ? selection
            : ViewModel.DisplayLogs.ToArray();
        var scopeLabel = selection.Count > 0 ? "选中行" : "当前视图（含筛选与搜索）";

        if (entries.Count == 0)
        {
            ViewModel.StatusMessage = "没有可导出的日志";
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"serial-log-{DateTime.Now:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("文本文件", new List<string> { ".txt" });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            // Cancelled at the picker. Not a failure, and not worth a status message that would
            // overwrite whatever the user was reading.
            return;
        }

        await ViewModel.ExportLogsAsync(entries, file.Path, scopeLabel);
    }

    /// <summary>
    /// F2's entry point: opens the palette, or closes it when it is already up.
    /// </summary>
    /// <remarks>
    /// A toggle rather than open-only, because the same key that summons a palette is the one users
    /// press to get rid of it. The palette itself decides nothing here — it owns only its own visibility.
    /// </remarks>
    private void ToggleCommandPalette()
    {
        if (CommandPalette.IsOpen)
        {
            CommandPalette.Close();
        }
        else
        {
            CommandPalette.Open();
        }
    }

    /// <summary>
    /// Runs whatever the activated palette row meant.
    /// </summary>
    /// <remarks>
    /// The palette has already closed itself by the time this runs, so a slow action (opening a port goes
    /// through the whole connect path) cannot leave the overlay sitting on top of its own result. Ports go
    /// through the very same commands the sidebar uses, which is what keeps the two in step — including
    /// the confirmation prompt and the error reporting, neither of which is reimplemented here.
    /// </remarks>
    private async void CommandPalette_Activated(object? sender, PaletteEntry entry)
    {
        switch (entry.Kind)
        {
            case PaletteEntryKind.Snippet when entry.Snippet is { } snippet:
                await ViewModel.SendSnippetAsync(snippet);
                break;

            case PaletteEntryKind.OpenPort:
                ViewModel.ClosePortCommand.Execute(entry.PortName);
                break;

            case PaletteEntryKind.AvailablePort:
                ViewModel.OpenPortCommand.Execute(entry.PortName);
                break;
        }
    }

    private void SelectAllLogs_Click(object sender, RoutedEventArgs e)
    {
        LogListView.SelectAll();
    }

    private void CopySelectedLogs_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+C keeps working through the control itself; this is the discoverable toolbar entry.
        // CopySelection() reports failure both for "nothing selected" and for "clipboard refused";
        // CopyFailed has already explained the latter, so only the former gets a message here.
        _clipboardCopyFailed = false;
        if (!LogListView.CopySelection() && !_clipboardCopyFailed)
        {
            ViewModel.StatusMessage = "没有选中的日志";
        }
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPaused = !ViewModel.IsPaused;
    }

    /// <summary>Swatch glyph for the port-colour menu items (the same FillColorDroplet shape the XAML used).</summary>
    private const string PortColorSwatchGlyph = "\uE91F";

    /// <summary>Set by <see cref="LogListView_CopyFailed"/> so the toolbar click does not overwrite it.</summary>
    private bool _clipboardCopyFailed;

    private void LogListView_CopyCompleted(object? sender, int count)
    {
        ViewModel.StatusMessage = $"已复制 {count} 条日志到剪贴板";
        _notifications.Notify($"已复制 {count} 条日志", InfoBarSeverity.Success);
    }

    /// <remarks>
    /// A clipboard refusal is a normal, recoverable situation (another process has the clipboard
    /// open), so it degrades to a message rather than an exception dialog. It also gets the sticky
    /// treatment on purpose: the user pressed Ctrl+C and the clipboard stayed empty, which is exactly
    /// the kind of failure a status-bar line loses. The status line keeps the short form and the bar
    /// stays until it is dismissed.
    /// </remarks>
    private void LogListView_CopyFailed(object? sender, string message)
    {
        _clipboardCopyFailed = true;
        ViewModel.StatusMessage = $"复制失败：剪贴板被其他程序占用，请稍后重试（{message}）";
        _notifications.Notify(
            $"剪贴板被其他程序占用（{message}）。请稍后重试。",
            InfoBarSeverity.Error,
            title: "复制失败",
            autoDismissMs: 0);
    }

    private void CustomBaudRateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            bool isChecked = checkBox.IsChecked ?? false;
            BaudRateComboBox.Visibility = isChecked ? Visibility.Collapsed : Visibility.Visible;
            CustomBaudRateTextBox.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    
    #region Baud Rate Alert Handling
    
    private string? _suggestedPortName;
    private int _suggestedBaudRate;
    
    private void InitializeBaudRateAlert()
    {
        // Initially hide the alert
        BaudRateAlertBorder.Visibility = Visibility.Collapsed;
    }
    
    private void ViewModel_BaudRateSuggested(object? sender, MainViewModel.BaudRateSuggestionEventArgs e)
    {
        _suggestedPortName = e.PortName;
        _suggestedBaudRate = e.SuggestedBaudRate;
        
        BaudRateAlertTitle.Text = $"检测到 {e.PortName} 波特率可能不匹配";
        BaudRateAlertMessage.Text = $"当前: {e.CurrentBaudRate}, 建议: {e.SuggestedBaudRate}\n原因: {e.Reason}\n置信度: {e.Confidence:F2}";
        
        // 如果置信度很高，显示自动修复按钮
        AutoFixBaudRateButton.Visibility = e.ShouldAutoSwitch ? Visibility.Visible : Visibility.Collapsed;
        
        ShowBaudRateAlert();
    }

    /// <summary>
    /// Reveals the banner with a short opacity ramp.
    /// </summary>
    /// <remarks>
    /// Opacity rather than height on purpose: the banner lives in an <c>Auto</c> grid row, so
    /// animating that row would be a layout animation (<c>EnableDependentAnimation</c>, i.e. UI-thread
    /// work on every frame) for a surface that is appearing as one piece anyway. The row collapsing
    /// the instant the dismiss ramp ends is deliberate too — by then the content is already invisible,
    /// so nothing the eye is following moves.
    /// </remarks>
    private void ShowBaudRateAlert()
    {
        _alertAnimationGeneration++;
        BaudRateAlertBorder.Visibility = Visibility.Visible;

        if (!_animationsEnabled)
        {
            BaudRateAlertBorder.Opacity = 1.0;
            return;
        }

        RunOpacityFade(BaudRateAlertBorder, from: 0.0, to: 1.0, PanelRevealMs, EasingMode.EaseOut);
    }

    private void HideBaudRateAlert()
    {
        _suggestedPortName = null;

        var generation = ++_alertAnimationGeneration;
        if (!_animationsEnabled || BaudRateAlertBorder.Visibility != Visibility.Visible)
        {
            BaudRateAlertBorder.Visibility = Visibility.Collapsed;
            BaudRateAlertBorder.Opacity = 1.0;
            return;
        }

        RunOpacityFade(
            BaudRateAlertBorder,
            from: BaudRateAlertBorder.Opacity,
            to: 0.0,
            PanelDismissMs,
            EasingMode.EaseIn,
            onCompleted: () =>
            {
                // A show that landed while this fade was in flight owns the banner now.
                if (generation != _alertAnimationGeneration)
                {
                    return;
                }

                BaudRateAlertBorder.Visibility = Visibility.Collapsed;
                BaudRateAlertBorder.Opacity = 1.0;
            });
    }

    /// <summary>
    /// Runs a one-shot opacity fade and lets the storyboard be collected when it ends.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="Storyboard"/> per call instead of a reused field: these fire on user
    /// actions (an appearance switch, a banner appearing), never on a hot path, and building one per
    /// call is what allows the completion callback to be closed over without leaving a handler
    /// subscribed to a shared instance for the life of the window. <c>FillBehavior.Stop</c> returns
    /// the target to its base opacity when the animation ends, so no animated value stays held on the
    /// composition tree — and <c>Opacity</c> is composition-backed, so no
    /// <c>EnableDependentAnimation</c> is required or wanted here.
    /// </remarks>
    private static void RunOpacityFade(
        DependencyObject target,
        double from,
        double to,
        int durationMs,
        EasingMode easing,
        Action? onCompleted = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
            EasingFunction = new CubicEase { EasingMode = easing },
            FillBehavior = FillBehavior.Stop
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        if (onCompleted is not null)
        {
            storyboard.Completed += (_, _) => onCompleted();
        }

        storyboard.Begin();
    }
    
    private async void AutoFixBaudRate_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_suggestedPortName))
        {
            try
            {
                ViewModel.StatusMessage = $"正在自动修复 {_suggestedPortName} 的波特率...";
                
                // Use the ViewModel's existing method to switch baud rate
                await ViewModel.SwitchPortBaudRateAsync(_suggestedPortName, _suggestedBaudRate);
                
                HideBaudRateAlert();
                ViewModel.StatusMessage = $"已成功将 {_suggestedPortName} 波特率切换到 {_suggestedBaudRate}";
            }
            catch (Exception ex)
            {
                ViewModel.StatusMessage = $"自动修复失败: {ex.Message}";
            }
        }
    }
    
    private void DismissAlert_Click(object sender, RoutedEventArgs e)
    {
        HideBaudRateAlert();
        ViewModel.StatusMessage = "已忽略波特率建议";
    }

    #endregion

    #region Log font size

    /// <summary>
    /// Ctrl+滚轮 调整日志字号。
    /// </summary>
    /// <remarks>
    /// The control reports the intent rather than changing its own <c>FontSize</c>: the size is persisted by the
    /// ViewModel, and a control that quietly wrote to its own dependency property would need the shell to
    /// observe it back — which is the same reason <c>CopyCompleted</c> / <c>CopyFailed</c> exist.
    /// </remarks>
    private void LogListView_FontSizeZoomRequested(object? sender, int delta)
        => ViewModel.AdjustLogFontSize(delta);

    private void IncreaseLogFontSize_Click(object sender, RoutedEventArgs e)
        => ViewModel.AdjustLogFontSize(1);

    private void DecreaseLogFontSize_Click(object sender, RoutedEventArgs e)
        => ViewModel.AdjustLogFontSize(-1);

    private void ResetLogFontSize_Click(object sender, RoutedEventArgs e)
        => ViewModel.ResetLogFontSize();

    #endregion

    #region Send target selection

    /// <summary>
    /// 构建「目标」菜单。
    /// </summary>
    /// <remarks>
    /// Rebuilt on every open, like the port-colour menu and for the same reason: a <c>MenuFlyout</c> has no
    /// <c>ItemsSource</c>, and the alternative — a cache — would need change hooks for both the port list and
    /// the selection. A handful of items behind an explicit user action is not worth either.
    /// </remarks>
    private void SendTargetFlyout_Opening(object? sender, object e)
    {
        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        flyout.Items.Clear();

        var selectAllItem = new ToggleMenuFlyoutItem
        {
            Text = "全部",
            IsChecked = ViewModel.IsSendingToAllPorts,
        };
        selectAllItem.Click += SelectAllSendTargets_Click;
        flyout.Items.Add(selectAllItem);

        if (ViewModel.OpenPorts.Count == 0)
        {
            // An empty menu with no explanation reads as a bug; this also tells the user why the list is
            // empty at the one moment they are looking for a port that is not there.
            flyout.Items.Add(new MenuFlyoutItem { Text = "还没有打开串口", IsEnabled = false });
            return;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        foreach (var port in ViewModel.OpenPorts)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = port.PortName,
                // Tag carries the port name back, the same shape the colour menu uses for its slot hex.
                Tag = port.PortName,
                IsChecked = ViewModel.IsSendTargetSelected(port.PortName),
            };
            item.Click += ToggleSendTarget_Click;
            flyout.Items.Add(item);
        }
    }

    /// <summary>
    /// 勾选 / 取消一个目标端口。
    /// </summary>
    /// <remarks>
    /// The new state is derived from the ViewModel rather than read from <c>item.IsChecked</c>: the order in
    /// which <c>ToggleMenuFlyoutItem</c> updates that property relative to raising <c>Click</c> is not part of
    /// the contract, while the model's own answer always is. The item's tick may therefore briefly disagree
    /// after the click — invisible, because the flyout closes on it — and the next open rebuilds from the
    /// model.
    /// </remarks>
    private void ToggleSendTarget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string portName })
        {
            return;
        }

        ViewModel.SetSendTargetSelected(portName, !ViewModel.IsSendTargetSelected(portName));
    }

    private void SelectAllSendTargets_Click(object sender, RoutedEventArgs e)
        => ViewModel.SelectAllSendTargets();

    #endregion

    #region Port Color Selection

    // The port whose swatch opened the flyout. Captured while the flyout opens, because the colour
    // menu items have no reliable link back to their DropDownButton: the DataContext they see may be
    // the window's (inherited through the popup root) rather than the row's.
    private ViewModels.PortViewModel? _colorTargetPort;

    private void PortColorFlyout_Opening(object? sender, object e)
    {
        _colorTargetPort = (sender as MenuFlyout)?.Target is FrameworkElement target
            ? target.DataContext as ViewModels.PortViewModel
            : null;

        if (sender is not MenuFlyout flyout)
        {
            return;
        }

        // Built from PortColorPalette on every open rather than declared in XAML or cached. A
        // MenuFlyout has no ItemsSource, so the alternatives were ten hand-written items plus ten
        // AppPortColorNBrush resources (a second copy of the palette, see Models/PortColorSlot.cs)
        // or a cache that would need a change hook to follow an appearance switch. Ten items behind
        // an explicit user action is not worth either.
        flyout.Items.Clear();

        foreach (var option in ViewModel.PortColorOptions)
        {
            var item = new MenuFlyoutItem
            {
                // Tag stays the persisted slot hex: the menu reports the slot back, never the
                // resolved colour. PortColorMenuItem_Click and the settings file both depend on it.
                Tag = option.Hex,
                Text = option.Name,
                Icon = new FontIcon
                {
                    Glyph = PortColorSwatchGlyph,
                    Foreground = option.Brush,
                },
            };
            item.Click += PortColorMenuItem_Click;
            flyout.Items.Add(item);
        }
    }

    private void PortColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem menuItem || menuItem.Tag is not string colorHex)
        {
            return;
        }

        // The target captured on open is authoritative; the rest are fallbacks kept from the previous
        // implementation so a change in how flyout DataContext flows cannot silently break the feature.
        var portVm = _colorTargetPort;

        if (portVm == null && menuItem.DataContext is ViewModels.PortViewModel vm)
        {
            portVm = vm;
        }

        if (portVm == null &&
            menuItem.Parent is MenuFlyout flyout &&
            flyout.Target is FrameworkElement flyoutTarget &&
            flyoutTarget.DataContext is ViewModels.PortViewModel targetVm)
        {
            portVm = targetVm;
        }

        if (portVm == null && OpenPortListView.SelectedItem is ViewModels.PortViewModel selectedVm)
        {
            portVm = selectedVm;
        }

        if (portVm == null)
        {
            System.Diagnostics.Debug.WriteLine("Could not find PortViewModel to change color");
            return;
        }

        // ColorHex stays the palette slot (that is what gets persisted); the rendered colour is
        // derived from it for the appearance that is currently active.
        portVm.ColorHex = colorHex;
        portVm.RefreshDisplayColor(ViewModel.IsDarkTheme);
        // Rows that are already on screen would otherwise keep the previous brush until recycling
        // re-applies the OneWay binding, mixing two palettes in the log — the same failure mode the
        // appearance sweep exists to prevent, triggered here by a per-port change.
        ViewModel.ApplyPortColorChange(portVm.PortName, colorHex);
        ViewModel.SavePortColor(portVm.PortName, colorHex);

        _colorTargetPort = null;
    }

    #endregion

    #region Update Checking

    private const string DefaultReleasePageUrl = "https://github.com/ZubenStar/SerialPortTool/releases/latest";

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_silentUpdateCheckStarted)
        {
            return;
        }

        _silentUpdateCheckStarted = true;
        Activated -= OnFirstActivated;

        StartRuntimeUpdateCheckTimer();
        _ = RunSilentUpdateCheckAsync(isStartup: true);
    }

    /// <summary>
    /// 开启运行期补查：窗口存活期间每 <see cref="RuntimeUpdateCheckInterval"/> 静默检查一次。
    /// </summary>
    /// <remarks>
    /// 必须用 <see cref="DispatcherQueueTimer"/> 而不是后台定时器：命中新版本时要在 UI 线程弹 ContentDialog，
    /// 且要与手动检查共用 <c>_isUpdateDialogOpen</c> 这一个并发守卫。窗口关闭时显式停止（构造函数里的
    /// <c>Closed</c> 订阅），所以既不会拖慢退出，也不会在窗口销毁后再拉起对话框。
    /// 系统休眠期间定时器不推进，唤醒后最坏延后一个周期，这是已知且可接受的限制。
    /// </remarks>
    private void StartRuntimeUpdateCheckTimer()
    {
        if (_runtimeUpdateCheckTimer != null)
        {
            return;
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = RuntimeUpdateCheckInterval;
        timer.IsRepeating = true;
        timer.Tick += OnRuntimeUpdateCheckTick;
        _runtimeUpdateCheckTimer = timer;
        timer.Start();
    }

    private void StopRuntimeUpdateCheckTimer()
    {
        var timer = _runtimeUpdateCheckTimer;
        if (timer == null)
        {
            return;
        }

        _runtimeUpdateCheckTimer = null;
        timer.Stop();
        timer.Tick -= OnRuntimeUpdateCheckTick;
    }

    private void OnRuntimeUpdateCheckTick(DispatcherQueueTimer sender, object args)
    {
        _ = RunSilentUpdateCheckAsync(isStartup: false);
    }

    /// <summary>
    /// 静默检查：网络失败静默、命中「跳过此版本」或处于节流窗口（失败退避 1 小时 / 成功冷却 30 分钟）时静默，
    /// 仅在有新版本时提示。已知有更新时 <see cref="IUpdateService"/> 直接返回本地缓存的结果、不联网
    /// （该缓存 24 小时后作废并重新联网确认），所以「稍后」之后的下一次启动同样会立刻再次提示。
    /// </summary>
    /// <remarks>
    /// 是否联网、是否复用缓存、是否被节流都由 <see cref="IUpdateService"/> 决定，这里只负责“什么时候问”。
    /// </remarks>
    /// <param name="isStartup">
    /// <c>true</c>：启动检查，先延迟 <see cref="SilentUpdateCheckDelay"/> 让窗口初始化 / 串口扫描先跑完；
    /// <c>false</c>：运行期补查，立即执行。
    /// </param>
    private async Task RunSilentUpdateCheckAsync(bool isStartup)
    {
        if (_silentUpdateCheckRunning)
        {
            // 上一次静默检查尚未结束（例如启动检查的 5s 延迟期间定时器就到点了）：跳过，不要并发发第二次请求。
            return;
        }

        _silentUpdateCheckRunning = true;
        try
        {
            if (isStartup)
            {
                await Task.Delay(SilentUpdateCheckDelay);
            }

            var result = await _updateService.CheckAsync(manual: false);
            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Info == null)
            {
                return;
            }

            await ShowUpdateAvailableDialogAsync(result.Info, isSilent: true);
        }
        catch (Exception ex)
        {
            // 静默路径绝不向用户弹错。
            System.Diagnostics.Debug.WriteLine($"Silent update check failed: {ex.Message}");
        }
        finally
        {
            _silentUpdateCheckRunning = false;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdateDialogOpen)
        {
            return;
        }

        try
        {
            ViewModel.StatusMessage = "正在检查更新…";
            var result = await _updateService.CheckAsync(manual: true);
            ViewModel.StatusMessage = string.Empty;

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Info != null:
                    await ShowUpdateAvailableDialogAsync(result.Info, isSilent: false);
                    break;

                case UpdateCheckStatus.UpToDate:
                case UpdateCheckStatus.Skipped:
                    await ShowMessageDialogAsync(
                        "检查更新",
                        $"当前已是最新版本（v{VersionInfo.Version}）。");
                    break;

                default:
                    await ShowMessageDialogAsync(
                        "检查更新失败",
                        $"{result.FailureReason ?? "未知错误"}\n\n你也可以手动前往发布页下载最新版本。",
                        secondaryText: "前往下载页",
                        secondaryAction: () => OpenReleasePage(DefaultReleasePageUrl));
                    break;
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = string.Empty;
            await ShowMessageDialogAsync("检查更新失败", ex.Message);
        }
    }

    /// <summary>
    /// 发现新版本时弹出更新说明。
    /// </summary>
    /// <remarks>
    /// ContentDialog 只有 Primary / Secondary / Close 三个按钮位，因此按场景分配：
    /// 安装版 + 静默 = 「下载并安装 / 跳过此版本 / 稍后」；
    /// 安装版 + 手动 = 「下载并安装 / 前往下载页 / 关闭」；
    /// 便携版（无法自动安装）= 「前往下载页 / [静默时] 跳过此版本 / 稍后」。
    /// </remarks>
    private async Task ShowUpdateAvailableDialogAsync(UpdateReleaseInfo info, bool isSilent)
    {
        if (_isUpdateDialogOpen)
        {
            return;
        }

        _isUpdateDialogOpen = true;
        try
        {
            var canAutoInstall = _updateService.IsInstalledBuild && info.HasInstaller;

            var panel = new StackPanel
            {
                Spacing = 8,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0)
            };

            panel.Children.Add(new TextBlock
            {
                Text = $"当前版本: v{VersionInfo.Version}  →  最新版本: v{info.LatestVersion}",
                FontSize = 13,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
            });

            if (info.PublishedAt > DateTimeOffset.MinValue)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = $"发布时间: {info.PublishedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                    FontSize = 12,
                    Opacity = 0.8
                });
            }

            var notesText = string.IsNullOrWhiteSpace(info.ReleaseNotes)
                ? "（该版本未提供更新说明）"
                : info.ReleaseNotes.Trim();

            panel.Children.Add(new TextBox
            {
                Text = notesText,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                MaxHeight = 220,
                FontSize = 12
            });

            if (!_updateService.IsInstalledBuild)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "当前为便携版（直接解压运行），将打开下载页，不会自动替换文件。",
                    FontSize = 11,
                    Opacity = 0.8,
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
                });
            }

            var dialog = CreateDialog();
            dialog.Title = $"发现新版本 v{info.LatestVersion}";
            dialog.Content = panel;
            dialog.CloseButtonText = isSilent ? "稍后" : "关闭";
            dialog.DefaultButton = ContentDialogButton.Primary;

            if (canAutoInstall)
            {
                dialog.PrimaryButtonText = "下载并安装";
                dialog.SecondaryButtonText = isSilent ? "跳过此版本" : "前往下载页";
            }
            else if (isSilent)
            {
                // 便携版无法自动安装，但仍要给出下载入口与「跳过此版本」。
                dialog.PrimaryButtonText = "前往下载页";
                dialog.SecondaryButtonText = "跳过此版本";
            }
            else
            {
                dialog.PrimaryButtonText = "前往下载页";
            }

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                if (canAutoInstall)
                {
                    await DownloadAndInstallAsync(info);
                }
                else
                {
                    OpenReleasePage(info.ReleasePageUrl);
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                if (isSilent)
                {
                    await _updateService.SkipVersionAsync(info.LatestVersion);
                    ViewModel.StatusMessage = $"已跳过版本 v{info.LatestVersion}";
                }
                else
                {
                    OpenReleasePage(info.ReleasePageUrl);
                }
            }
        }
        finally
        {
            _isUpdateDialogOpen = false;
        }
    }

    private async Task DownloadAndInstallAsync(UpdateReleaseInfo info)
    {
        if (!_updateService.IsInstalledBuild || !info.HasInstaller)
        {
            OpenReleasePage(info.ReleasePageUrl);
            return;
        }

        if (!await DownloadInstallerWithProgressAsync(info))
        {
            // 用户取消或下载失败（服务内部已记录日志）。
            return;
        }

        try
        {
            // 先把内存中未落盘的设置刷到磁盘，再启动安装器并退出。
            await _settingsService.FlushAsync();
            _updateInstallerService.LaunchInstaller();
        }
        catch (Exception ex)
        {
            await ShowMessageDialogAsync(
                "无法启动更新程序",
                $"{ex.Message}\n\n请手动前往发布页下载最新版本。",
                secondaryText: "前往下载页",
                secondaryAction: () => OpenReleasePage(info.ReleasePageUrl));
            return;
        }

        // 走既有退出路径：App.OnWindowClosed 负责释放服务并强制退出。
        Close();
    }

    /// <summary>
    /// 显示下载进度对话框；返回 <c>true</c> 表示安装包已下载并通过校验。
    /// </summary>
    private async Task<bool> DownloadInstallerWithProgressAsync(UpdateReleaseInfo info)
    {
        using var cancellation = new CancellationTokenSource();

        var statusText = new TextBlock
        {
            Text = "正在下载更新包…",
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        };

        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            IsIndeterminate = info.SetupSizeBytes <= 0
        };

        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(statusText);
        panel.Children.Add(progressBar);

        var dialog = CreateDialog();
        dialog.Title = $"正在下载 v{info.LatestVersion}";
        dialog.Content = panel;
        dialog.CloseButtonText = "取消";
        dialog.DefaultButton = ContentDialogButton.Close;

        var cancelledByUser = false;
        dialog.CloseButtonClick += (s, e) =>
        {
            cancelledByUser = true;
            cancellation.Cancel();
        };

        var progress = new Progress<double>(value =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var percent = Math.Clamp(value * 100d, 0d, 100d);
                progressBar.Value = percent;
                statusText.Text = $"正在下载更新包… {percent:F0}%";
            });
        });

        Task<bool>? downloadTask = null;
        dialog.Opened += (s, e) =>
        {
            downloadTask = _updateInstallerService.DownloadInstallerAsync(
                info.SetupDownloadUrl!,
                info.SetupSizeBytes,
                progress,
                cancellation.Token);

            _ = HideDialogWhenDownloadCompletesAsync(dialog, downloadTask);
        };

        // ShowAsync 会在 Hide() 或用户点「取消」后返回。
        await dialog.ShowAsync();

        if (downloadTask == null)
        {
            return false;
        }

        var downloaded = await downloadTask;
        return downloaded && !cancelledByUser;
    }

    private async Task HideDialogWhenDownloadCompletesAsync(ContentDialog dialog, Task<bool> downloadTask)
    {
        try
        {
            await downloadTask;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update download failed: {ex.Message}");
        }
        finally
        {
            // await 内部使用 ConfigureAwait(false)，这里必须回到 UI 线程再关对话框。
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    dialog.Hide();
                }
                catch
                {
                    // 对话框可能已被「取消」关闭。
                }
            });
        }
    }

    private async Task ShowMessageDialogAsync(
        string title,
        string message,
        string? secondaryText = null,
        Action? secondaryAction = null)
    {
        var dialog = CreateDialog();
        dialog.Title = title;
        dialog.Content = new TextBlock
        {
            Text = message,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        };
        dialog.CloseButtonText = "确定";
        dialog.DefaultButton = ContentDialogButton.Close;

        if (!string.IsNullOrEmpty(secondaryText))
        {
            dialog.SecondaryButtonText = secondaryText;
        }

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            secondaryAction?.Invoke();
        }
    }

    private void OpenReleasePage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"打开下载页失败: {ex.Message}";
        }
    }

    #endregion
}
