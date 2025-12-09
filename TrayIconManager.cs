using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.UI.Xaml;
using Application = System.Windows.Forms.Application;

namespace VirtualKeyboard;

/// <summary>
/// Manages system tray icon and context menu for the virtual keyboard
/// </summary>
public class TrayIconManager : IDisposable
{
    private NotifyIcon _notifyIcon;
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
        _notifyIcon = new NotifyIcon
        {
            // Use a simple keyboard icon (you can replace with custom icon file)
            Icon = SystemIcons.Application,
            Text = "Virtual Keyboard",
            Visible = true
        };

        // Create context menu
        var contextMenu = new ContextMenuStrip();
        
        // Show/Hide menu item
        var showHideItem = new ToolStripMenuItem("Show/Hide Keyboard");
        showHideItem.Click += (s, e) => ToggleWindowVisibility();
        contextMenu.Items.Add(showHideItem);
        
        contextMenu.Items.Add(new ToolStripSeparator());
        
        // Settings menu item
        var settingsItem = new ToolStripMenuItem("Settings");
        settingsItem.Click += (s, e) => ShowSettings();
        contextMenu.Items.Add(settingsItem);
        
        contextMenu.Items.Add(new ToolStripSeparator());
        
        // Exit menu item
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (s, e) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;

        // Double-click to show/hide window
        _notifyIcon.DoubleClick += (s, e) => ToggleWindowVisibility();

        Logger.Info("Tray icon initialized successfully");
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
                Logger.Info("Window hidden to tray");
            }
            else
            {
                // Show and activate the window
                appWindow.Show();
                Logger.Info("Window restored from tray");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to toggle window visibility", ex);
        }
    }

    /// <summary>
    /// Show settings dialog
    /// </summary>
    private void ShowSettings()
    {
        try
        {
            // Ensure window is visible before showing settings
            if (!_mainWindow.AppWindow.IsVisible)
            {
                _mainWindow.AppWindow.Show();
            }

            // TODO: Implement settings dialog
            Logger.Info("Settings requested (not yet implemented)");
            
            // For now, just show a message
            System.Windows.Forms.MessageBox.Show(
                "Settings dialog will be implemented in future updates.",
                "Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show settings", ex);
        }
    }

    /// <summary>
    /// Exit the application completely
    /// </summary>
    private void ExitApplication()
    {
        try
        {
            Logger.Info("Exit requested from tray menu");
            
            // Clean up tray icon
            _notifyIcon.Visible = false;
            
            // Close the WinUI window
            _mainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                _mainWindow.Close();
            });
            
            // Exit the WinUI application
            _winUIApp.Exit();
        }
        catch (Exception ex)
        {
            Logger.Error("Error during application exit", ex);
        }
    }

    /// <summary>
    /// Show a notification balloon tip
    /// </summary>
    public void ShowNotification(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        try
        {
            if (_notifyIcon != null && !_isDisposed)
            {
                _notifyIcon.ShowBalloonTip(3000, title, text, icon);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show notification", ex);
        }
    }

    /// <summary>
    /// Clean up resources
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            _isDisposed = true;
            Logger.Info("TrayIconManager disposed");
        }
    }
}