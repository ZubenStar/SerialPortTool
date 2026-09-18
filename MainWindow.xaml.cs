using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>Fallback when the design token cannot be read; mirrors AppSidebarWidth in Tokens.xaml.</summary>
    private const double SidebarWidthFallback = 272;

    public MainViewModel ViewModel { get; }

    // Flag to prevent duplicate history saves
    private string _lastSavedSearchText = string.Empty;
    private DateTime _lastSaveTime = DateTime.MinValue;

    // Flag to track if current text is from selecting history
    private bool _isFromHistorySelection = false;

    // Update-related services. Resolved from the container (Window has a parameterless ctor for XAML).
    private readonly IUpdateService _updateService;
    private readonly IUpdateInstallerService _updateInstallerService;
    private readonly ISettingsService _settingsService;

    // Shell state.
    private AppThemePreference _themePreference = AppThemePreference.System;
    private readonly Storyboard _sidebarStoryboard = new();
    private double _sidebarAnimationTarget;
    private bool _sidebarAppliedCollapsed;
    private bool _animationsEnabled = true;

    /// <summary>
    /// Darkness the current <see cref="MicaBackdrop"/> was created for; <c>null</c> when no material
    /// is in use. Prevents the backdrop being reassigned while it is still connecting at startup.
    /// </summary>
    private bool? _appliedBackdropDark;

    // 启动静默检查的延迟，避免与窗口初始化 / 串口扫描抢资源
    private static readonly TimeSpan SilentUpdateCheckDelay = TimeSpan.FromSeconds(5);

    // 保证同一时刻只有一个更新相关对话框（WinUI 3 不允许并发 ContentDialog）
    private bool _isUpdateDialogOpen;
    private bool _silentUpdateCheckStarted;

    public MainWindow()
    {
        // IMPORTANT: Get ViewModel BEFORE InitializeComponent for x:Bind to work
        ViewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        _updateService = App.Current.Services.GetRequiredService<IUpdateService>();
        _updateInstallerService = App.Current.Services.GetRequiredService<IUpdateInstallerService>();
        _settingsService = App.Current.Services.GetRequiredService<ISettingsService>();

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

        // 窗口首次激活后再启动静默更新检查（此时 Content.XamlRoot 才可用）
        Activated += OnFirstActivated;

        // Debug: Monitor search history changes
        ViewModel.RecentSearchTexts.CollectionChanged += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"RecentSearchTexts changed: Action={e.Action}, Count={ViewModel.RecentSearchTexts.Count}");
            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems)
                {
                    System.Diagnostics.Debug.WriteLine($"  Added: {item}");
                }
            }

            // Force ComboBox to refresh - this is a workaround for WinUI 3 binding issues
            DispatcherQueue.TryEnqueue(() =>
            {
                // Trigger a UI update by accessing the ItemsSource
                var count = SearchBox.Items.Count;
                System.Diagnostics.Debug.WriteLine($"ComboBox Items Count: {count}");
            });
        };
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
        if (e.PropertyName == nameof(ViewModel.MatchCount))
        {
            MatchCountTextBlock.Text = $"匹配 {ViewModel.MatchCount} 条";
            MatchCountTextBlock.Visibility = ViewModel.IsRegexValid && !string.IsNullOrEmpty(ViewModel.SearchText)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(ViewModel.IsRegexValid) || e.PropertyName == nameof(ViewModel.RegexErrorMessage))
        {
            UpdateRegexState();
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
    /// Reports a bad regex inline instead of recolouring the search box border.
    /// </summary>
    /// <remarks>
    /// The previous version assigned hand-built <c>SolidColorBrush</c>s (literal green / red) to
    /// <c>SearchBox.BorderBrush</c>. That hard-coded a light-theme colour and stomped the themed
    /// border; the inline message plus its warning glyph is a stronger, keyboard- and
    /// screen-reader-visible signal that also survives an appearance change.
    /// </remarks>
    private void UpdateRegexState()
    {
        if (ViewModel.IsRegexValid)
        {
            RegexErrorIcon.Visibility = Visibility.Collapsed;
            RegexErrorTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        RegexErrorTextBlock.Text = ViewModel.RegexErrorMessage;
        RegexErrorIcon.Visibility = Visibility.Visible;
        RegexErrorTextBlock.Visibility = Visibility.Visible;
        MatchCountTextBlock.Visibility = Visibility.Collapsed;
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

        ApplyTitleBarColors(isDark);
        RefreshBackdropTheme(isDark);
        ViewModel.ApplyEffectiveTheme(isDark);
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

    private async void PortListView_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.ListView listView &&
            listView.SelectedItem is string portName &&
            !string.IsNullOrEmpty(portName))
        {
            await ViewModel.OpenPortCommand.ExecuteAsync(portName);
            listView.SelectedItem = null; // Deselect after opening
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        // Sync selected port from UI to ViewModel
        ViewModel.SelectedPort = OpenPortListView.SelectedItem as ViewModels.PortViewModel;
        await ViewModel.SendCommand.ExecuteAsync(null);
    }

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

    private void SearchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // When user selects an item from the dropdown
        if (sender is ComboBox comboBox && e.AddedItems.Count > 0 && e.AddedItems[0] is string selectedText)
        {
            System.Diagnostics.Debug.WriteLine($"SelectionChanged: Selected '{selectedText}'");

            // Mark that this text is from selecting a history item
            _isFromHistorySelection = true;

            // Update ViewModel
            ViewModel.SearchText = selectedText;

            // Trigger filter
            ViewModel.FilterLogs();
        }
    }

    private void SearchBox_DropDownClosed(object sender, object e)
    {
        // When dropdown closes
        if (sender is ComboBox comboBox)
        {
            System.Diagnostics.Debug.WriteLine($"DropDownClosed: Text='{comboBox.Text}'");

            // Ensure ViewModel has the current text
            if (!string.IsNullOrWhiteSpace(comboBox.Text))
            {
                ViewModel.SearchText = comboBox.Text;
            }

            // Trigger filter
            ViewModel.FilterLogs();
        }
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // When the ComboBox loses focus
        if (sender is ComboBox comboBox)
        {
            var searchText = comboBox.Text?.Trim() ?? string.Empty;
            System.Diagnostics.Debug.WriteLine($"LostFocus: Text='{searchText}', IsFromHistorySelection={_isFromHistorySelection}");

            // DON'T clear SelectedItem - this causes recursive LostFocus events
            // Let the ComboBox manage it naturally

            // If the text is from selecting a history item, don't add it again
            if (_isFromHistorySelection)
            {
                System.Diagnostics.Debug.WriteLine($"Skipping save - text is from history selection");
                _isFromHistorySelection = false; // Reset flag
                _lastSavedSearchText = searchText; // Update last saved
                _lastSaveTime = DateTime.Now;
                return;
            }

            // Save to history if text is not empty
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                var timeSinceLastSave = DateTime.Now - _lastSaveTime;
                if (searchText != _lastSavedSearchText || timeSinceLastSave.TotalSeconds > 1)
                {
                    ViewModel.AddToRecentSearches(searchText);
                    _lastSavedSearchText = searchText;
                    _lastSaveTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"Saved to history: '{searchText}'");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Skipped duplicate save: '{searchText}'");
                }
            }
        }
    }

    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // When user presses Enter key
        if (e.Key == Windows.System.VirtualKey.Enter && sender is ComboBox comboBox)
        {
            var searchText = comboBox.Text?.Trim() ?? string.Empty;
            System.Diagnostics.Debug.WriteLine($"KeyDown Enter: Text='{searchText}'");

            // Clear the history selection flag - this is manual input
            _isFromHistorySelection = false;

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                // Update ViewModel
                ViewModel.SearchText = searchText;

                // Add to recent searches (using debounce logic)
                var timeSinceLastSave = DateTime.Now - _lastSaveTime;
                if (searchText != _lastSavedSearchText || timeSinceLastSave.TotalSeconds > 1)
                {
                    ViewModel.AddToRecentSearches(searchText);
                    _lastSavedSearchText = searchText;
                    _lastSaveTime = DateTime.Now;
                    System.Diagnostics.Debug.WriteLine($"Saved to history (Enter): '{searchText}'");
                }

                // Trigger filter
                ViewModel.FilterLogs();

                // Close dropdown and clear selection
                comboBox.IsDropDownOpen = false;
                comboBox.SelectedItem = null;
            }

            // Mark event as handled to prevent further processing
            e.Handled = true;
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
        // Clear the current search text
        ViewModel.SearchText = string.Empty;

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
            ViewModel.ClearRecentSearches();
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
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
            // Log error - you may want to show a dialog to the user
            System.Diagnostics.Debug.WriteLine($"Error opening log folder: {ex.Message}");
        }
    }

    private void SelectAllLogs_Click(object sender, RoutedEventArgs e)
    {
        LogListView.SelectAll();
    }

    private void CopySelectedLogs_Click(object sender, RoutedEventArgs e)
    {
        // Ctrl+C keeps working through the control itself; this is the discoverable toolbar entry.
        if (!LogListView.CopySelection())
        {
            ViewModel.StatusMessage = "没有选中的日志";
        }
    }

    private void TogglePause_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.IsPaused = !ViewModel.IsPaused;
    }

    private void LogListView_CopyCompleted(object? sender, int count)
    {
        ViewModel.StatusMessage = $"已复制 {count} 条日志到剪贴板";
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
        
        BaudRateAlertBorder.Visibility = Visibility.Visible;
    }

    private void HideBaudRateAlert()
    {
        BaudRateAlertBorder.Visibility = Visibility.Collapsed;
        _suggestedPortName = null;
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

        _ = RunSilentUpdateCheckAsync();
    }

    /// <summary>
    /// 启动后的静默检查：网络失败静默、命中「跳过此版本」静默，仅在有新版本时提示。
    /// </summary>
    private async Task RunSilentUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(SilentUpdateCheckDelay);

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
