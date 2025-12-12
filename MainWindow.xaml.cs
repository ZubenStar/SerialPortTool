using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialPortTool.Helpers;
using SerialPortTool.ViewModels;
using System;
using System.Linq;

namespace SerialPortTool;

/// <summary>
/// Main window for the Serial Port Tool application
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        
        // Get ViewModel from DI container
        ViewModel = App.Current.Services.GetRequiredService<MainViewModel>();
        
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
            appWindow.Resize(new Windows.Graphics.SizeInt32(1200, 800));
        }
        
        // Auto-scroll support for ItemsRepeater
        ViewModel.DisplayLogs.CollectionChanged += DisplayLogs_CollectionChanged;
        
        // Disable auto-scroll when user manually scrolls
        LogScrollViewer.ViewChanged += LogScrollViewer_ViewChanged;
        
        // Subscribe to ViewModel property changes for match count
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        
        // Subscribe to baud rate suggestion events
        ViewModel.BaudRateSuggested += ViewModel_BaudRateSuggested;
        
        // Initialize baud rate alert UI
        InitializeBaudRateAlert();
    }
    
    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.MatchCount))
        {
            MatchCountTextBlock.Text = $"匹配: {ViewModel.MatchCount} 条";
            MatchCountTextBlock.Visibility = ViewModel.IsRegexValid && !string.IsNullOrEmpty(ViewModel.SearchText)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else if (e.PropertyName == nameof(ViewModel.IsRegexValid) || e.PropertyName == nameof(ViewModel.RegexErrorMessage))
        {
            if (ViewModel.IsRegexValid)
            {
                RegexErrorTextBlock.Visibility = Visibility.Collapsed;
                SearchBox.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);
            }
            else
            {
                RegexErrorTextBlock.Text = ViewModel.RegexErrorMessage;
                RegexErrorTextBlock.Visibility = Visibility.Visible;
                MatchCountTextBlock.Visibility = Visibility.Collapsed;
                SearchBox.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            }
        }
    }
    
    private bool _isAutoScrolling = false;
    
    private void LogScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        // Don't disable auto-scroll automatically - let user control it via checkbox
        // This method is now only used to detect when to re-enable auto-scroll
        if (!_isAutoScrolling && !e.IsIntermediate && !ViewModel.AutoScroll)
        {
            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer != null)
            {
                // Re-enable auto-scroll if user manually scrolls back to bottom
                var distanceFromBottom = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
                if (distanceFromBottom <= 10)
                {
                    ViewModel.AutoScroll = true;
                }
            }
        }
    }
    
    private void DisplayLogs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Auto-scroll when new logs are added
        if (ViewModel.AutoScroll && e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            // Ensure LogScrollViewer is loaded before attempting to scroll
            if (LogScrollViewer == null)
                return;
                
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    // Double-check in case it's still null on the dispatcher thread
                    if (LogScrollViewer == null)
                        return;
                        
                    _isAutoScrolling = true;
                    // Small delay to ensure layout is updated
                    LogScrollViewer.UpdateLayout();
                    LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, false);
                    _isAutoScrolling = false;
                }
                catch
                {
                    _isAutoScrolling = false;
                }
            });
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "关于 SerialPortTool",
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };

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
        if (OpenPortListView.SelectedItem is ViewModels.PortViewModel portVm &&
            !string.IsNullOrEmpty(SendTextBox.Text))
        {
            await portVm.SendDataCommand.ExecuteAsync(null);
            SendTextBox.Text = string.Empty;
        }
    }

    private async void ClosePort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string portName)
        {
            await ViewModel.ClosePortCommand.ExecuteAsync(portName);
        }
    }

    private void SearchBox_DropDownClosed(object sender, object e)
    {
        // When dropdown closes (after user may have selected an item)
        if (sender is ComboBox comboBox)
        {
            // Clear the selection to prevent auto-selection behavior
            comboBox.SelectedIndex = -1;
            
            // The Text binding updates ViewModel.SearchText automatically
            // Just trigger filter
            ViewModel.FilterLogs();
        }
    }
    
    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // When the ComboBox loses focus
        if (sender is ComboBox comboBox)
        {
            // Ensure selection is cleared
            comboBox.SelectedIndex = -1;
        }
    }
    
    private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // When user presses Enter key
        if (e.Key == Windows.System.VirtualKey.Enter && sender is ComboBox comboBox)
        {
            var searchText = comboBox.Text?.Trim();
            
            // Only add to recent searches if text is not empty
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                ViewModel.AddToRecentSearches(searchText);
            }
            
            // Mark event as handled to prevent further processing
            e.Handled = true;
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
        // Copy all logs to clipboard
        var allText = string.Join("\n", ViewModel.DisplayLogs.Select(l => l.FormattedText));
        
        if (!string.IsNullOrEmpty(allText))
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(allText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            
            ViewModel.StatusMessage = $"Copied {ViewModel.DisplayLogs.Count} logs to clipboard";
        }
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
    
    private void ShowBaudRateAlert(string portName, int currentBaudRate, int suggestedBaudRate, string reason)
    {
        _suggestedPortName = portName;
        _suggestedBaudRate = suggestedBaudRate;
        
        BaudRateAlertTitle.Text = $"检测到 {portName} 波特率可能不匹配";
        BaudRateAlertMessage.Text = $"当前: {currentBaudRate}, 建议: {suggestedBaudRate}\n原因: {reason}";
        
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
}