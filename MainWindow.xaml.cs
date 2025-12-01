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
        
        // Subscribe to log update events
        ViewModel.LogsUpdated += OnLogsUpdated;
    }
    
    private const int MaxTextBoxLength = 200000; // Reduced limit for better performance
    private readonly System.Text.StringBuilder _pendingLogs = new();
    private System.Threading.Timer? _updateTimer;
    private readonly object _pendingLogsLock = new();
    
    private void OnLogsUpdated(object? sender, string newLogsText)
    {
        // Batch log updates to reduce UI thread pressure
        lock (_pendingLogsLock)
        {
            _pendingLogs.Append(newLogsText);
            
            // Start or reset the update timer
            if (_updateTimer == null)
            {
                _updateTimer = new System.Threading.Timer(_ =>
                {
                    FlushPendingLogs();
                }, null, 100, System.Threading.Timeout.Infinite);
            }
            else
            {
                _updateTimer.Change(100, System.Threading.Timeout.Infinite);
            }
        }
    }
    
    private void FlushPendingLogs()
    {
        string logsToAdd;
        lock (_pendingLogsLock)
        {
            if (_pendingLogs.Length == 0)
                return;
                
            logsToAdd = _pendingLogs.ToString();
            _pendingLogs.Clear();
        }
        
        // Ensure we're on the UI thread
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Append new logs to the TextBox
                var currentText = LogsTextBox.Text;
                var newText = currentText + logsToAdd;
                
                // Trim old text if it gets too long
                if (newText.Length > MaxTextBoxLength)
                {
                    // Keep only the last 70% of content
                    var startIndex = newText.Length - (int)(MaxTextBoxLength * 0.7);
                    // Find the next newline to avoid cutting in the middle of a line
                    var nextNewline = newText.IndexOf('\n', startIndex);
                    if (nextNewline > 0)
                    {
                        newText = newText.Substring(nextNewline + 1);
                    }
                }
                
                LogsTextBox.Text = newText;
                
                // Auto-scroll if enabled
                if (ViewModel.AutoScroll)
                {
                    LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating logs display: {ex.Message}");
            }
        });
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
        LogsTextBox.Text = string.Empty;
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
        // Select all text in the TextBox
        LogsTextBox.SelectAll();
    }

    private void CopySelectedLogs_Click(object sender, RoutedEventArgs e)
    {
        var selectedText = LogsTextBox.SelectedText;
        
        if (string.IsNullOrEmpty(selectedText))
        {
            // If nothing selected, copy all
            selectedText = LogsTextBox.Text;
        }

        if (!string.IsNullOrEmpty(selectedText))
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(selectedText);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

            var lineCount = selectedText.Split('\n').Length;
            ViewModel.StatusMessage = $"Copied {lineCount} lines to clipboard";
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
}