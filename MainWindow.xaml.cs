using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
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
}