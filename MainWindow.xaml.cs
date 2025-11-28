using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SerialPortTool.ViewModels;
using System;

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
        
        // Subscribe to scroll events
        ViewModel.ScrollToBottomRequested += OnScrollToBottomRequested;
    }
    
    private void OnScrollToBottomRequested(object? sender, EventArgs e)
    {
        // Ensure we're on the UI thread
        DispatcherQueue.TryEnqueue(() =>
        {
            // Scroll to the last item in the log list
            if (LogsListView?.Items.Count > 0)
            {
                var lastItem = LogsListView.Items[LogsListView.Items.Count - 1];
                LogsListView.ScrollIntoView(lastItem);
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
}