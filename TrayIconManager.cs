using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using H.NotifyIcon;

namespace VirtualKeyboard;

/// <summary>
/// Manages system tray icon and context menu for the virtual keyboard
/// </summary>
public class TrayIconManager : IDisposable
{
    private TaskbarIcon _taskbarIcon;
    private readonly Window _mainWindow;
    private readonly Microsoft.UI.Xaml.Application _winUIApp;
    private bool _isDisposed;

    public TrayIconManager(Window mainWindow, Microsoft.UI.Xaml.Application winUIApp)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _winUIApp = winUIApp ?? throw new ArgumentNullException(nameof(winUIApp));
        InitializeTrayIcon();
    }

    /// <summary>
    /// Initialize the system tray icon and context menu
    /// </summary>
    private void InitializeTrayIcon()
    {
        _taskbarIcon = new TaskbarIcon
        {
            ToolTipText = "Virtual Keyboard",
        };

        // Create context menu
        var contextMenu = new MenuFlyout();
        
        // Show/Hide menu item
        var showHideItem = new MenuFlyoutItem { Text = "Show/Hide Keyboard" };
        showHideItem.Click += (s, e) => ToggleWindowVisibility();
        contextMenu.Items.Add(showHideItem);
        
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        
        // Settings menu item
        var settingsItem = new MenuFlyoutItem { Text = "Settings" };
        settingsItem.Click += (s, e) => ShowSettings();
        contextMenu.Items.Add(settingsItem);
        
        contextMenu.Items.Add(new MenuFlyoutSeparator());
        
        // Exit menu item
        var exitItem = new MenuFlyoutItem { Text = "Exit" };
        exitItem.Click += (s, e) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        // Context menu assignment
        _taskbarIcon.ContextFlyout = contextMenu;

        // FIX: Use TrayLeftMouseUp instead of LeftClick or Activated
        _taskbarIcon.TrayLeftMouseUp += (s, e) => ToggleWindowVisibility();

        // Set icon - using default icon for now
        try
        {
            // You can set custom icon here like:
            // _taskbarIcon.Icon = new Icon("icon.ico");
            // For now, we'll use the default
        }
        catch (Exception)
        {
            // Logger.Error("Failed to set tray icon", ex);
        }

        // Logger.Info("Tray icon initialized successfully");
    }

    /// <summary>
    /// Toggle window visibility between shown and hidden
    /// </summary>
    private void ToggleWindowVisibility()
    {
        try
        {
            var appWindow = _mainWindow.AppWindow;
            
            if (appWindow.IsVisible)
            {
                // Hide the window
                appWindow.Hide();
                // Logger.Info("Window hidden to tray");
            }
            else
            {
                // Show and activate the window
                appWindow.Show();
                // Logger.Info("Window restored from tray");
            }
        }
        catch (Exception)
        {
            // Logger.Error("Failed to toggle window visibility", ex);
        }
    }

    /// <summary>
    /// Show settings dialog
    /// </summary>
    private async void ShowSettings()
    {
        try
        {
            // Ensure window is visible before showing settings
            if (!_mainWindow.AppWindow.IsVisible)
            {
                _mainWindow.AppWindow.Show();
            }

            // Logger.Info("Settings requested (not yet implemented)");
            
            // For now, just show a message using WinUI ContentDialog
            var dialog = new ContentDialog
            {
                Title = "Settings",
                Content = "Settings dialog will be implemented in future updates.",
                CloseButtonText = "OK",
                XamlRoot = _mainWindow.Content.XamlRoot
            };
            
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // Logger.Error("Failed to show settings", ex);
        }
    }

    /// <summary>
    /// Exit the application completely
    /// </summary>
    private void ExitApplication()
    {
        try
        {
            // Logger.Info("Exit requested from tray menu");
            
            // Dispose tray icon first
            _taskbarIcon?.Dispose();
            
            // Close the WinUI window
            _mainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _mainWindow.Close();
            });
            
            // Exit the WinUI application
            _winUIApp.Exit();
        }
        catch (Exception)
        {
            // Logger.Error("Error during application exit", ex);
        }
    }

    /// <summary>
    /// Show a notification balloon tip
    /// </summary>
    public void ShowNotification(string title, string text)
    {
        try
        {
            if (_taskbarIcon != null && !_isDisposed)
            {
                _taskbarIcon.ShowNotification(title, text);
            }
        }
        catch (Exception)
        {
            // Logger.Error("Failed to show notification", ex);
        }
    }

    /// <summary>
    /// Clean up resources
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            if (_taskbarIcon != null)
            {
                _taskbarIcon.Dispose();
                _taskbarIcon = null;
            }

            _isDisposed = true;
            // Logger.Info("TrayIconManager disposed");
        }
    }
}