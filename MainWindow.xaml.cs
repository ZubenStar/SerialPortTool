using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void NewConnection_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Show new connection dialog
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

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            ViewModel.SearchText = textBox.Text;
            ViewModel.FilterLogs();
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearLogsCommand.Execute(null);
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

    private void CopySelectedLogs_Click(object sender, RoutedEventArgs e)
    {
        // Copy all visible logs (same as select all for now)
        SelectAllLogs_Click(sender, e);
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
}