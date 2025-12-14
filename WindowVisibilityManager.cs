using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Simplified window visibility manager - shows/hides keyboard without focus management
/// </summary>
public class WindowVisibilityManager
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly IntPtr _windowHandle;
    private readonly Window _window;
    private readonly WindowPositionManager _positionManager;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly AutoShowManager _autoShowManager;
    private readonly FrameworkElement _rootElement;
    private readonly BackspaceRepeatHandler _backspaceHandler;
    private readonly TrayIcon _trayIcon;

    public WindowVisibilityManager(
        IntPtr windowHandle,
        Window window,
        WindowPositionManager positionManager,
        KeyboardStateManager stateManager,
        LayoutManager layoutManager,
        AutoShowManager autoShowManager,
        FrameworkElement rootElement,
        BackspaceRepeatHandler backspaceHandler = null,
        TrayIcon trayIcon = null)
    {
        _windowHandle = windowHandle;
        _window = window;
        _positionManager = positionManager;
        _stateManager = stateManager;
        _layoutManager = layoutManager;
        _autoShowManager = autoShowManager;
        _rootElement = rootElement;
        _backspaceHandler = backspaceHandler;
        _trayIcon = trayIcon;
    }

    /// <summary>
    /// Check if window is currently visible
    /// </summary>
    public bool IsVisible()
    {
        return IsWindowVisible(_windowHandle);
    }

    /// <summary>
    /// Show the window
    /// preserveFocus parameter is kept for compatibility but no longer used
    /// </summary>
    public void Show(bool preserveFocus = false)
    {
        Logger.Info($"Show called (preserveFocus parameter ignored)");
        
        try
        {
            // Position window
            _positionManager?.PositionWindow(showWindow: false);
            
            // Show without activation
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            
            Logger.Info($"Window shown. Current foreground: 0x{GetForegroundWindow():X}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
        }
    }

    /// <summary>
    /// Hide the window to tray
    /// </summary>
    public void Hide()
    {
        try
        {
            // Reset all modifiers before hiding
            ResetAllModifiers();
            
            // Hide window
            ShowWindow(_windowHandle, SW_HIDE);
            
            // Notify AutoShowManager about hide (for cooldown)
            _autoShowManager?.NotifyKeyboardHidden();
            
            Logger.Info("Window hidden");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
        }
    }

    /// <summary>
    /// Toggle window visibility
    /// </summary>
    public void Toggle()
    {
        if (IsVisible())
        {
            Logger.Info("Toggle: hiding window");
            Hide();
        }
        else
        {
            Logger.Info("Toggle: showing window");
            Show(preserveFocus: false);
        }
    }

    /// <summary>
    /// Cleanup resources when window is closing
    /// </summary>
    public void Cleanup()
    {
        try
        {
            Logger.Info("WindowVisibilityManager cleanup started");
            
            // Reset all modifiers before closing
            ResetAllModifiers();
            
            // Dispose managed resources
            _backspaceHandler?.Dispose();
            _autoShowManager?.Dispose();
            _trayIcon?.Dispose();
            
            Logger.Info("WindowVisibilityManager cleanup completed");
        }
        catch (Exception ex)
        {
            Logger.Error("Error during WindowVisibilityManager cleanup", ex);
        }
    }

    /// <summary>
    /// Reset all modifier keys before hiding or closing
    /// </summary>
    private void ResetAllModifiers()
    {
        Logger.Info("Resetting all modifiers");
        
        if (_stateManager.IsShiftActive)
        {
            _stateManager.ToggleShift();
        }
        
        if (_stateManager.IsCtrlActive)
        {
            _stateManager.ToggleCtrl();
        }
        
        if (_stateManager.IsAltActive)
        {
            _stateManager.ToggleAlt();
        }
        
        if (_stateManager.IsCapsLockActive)
        {
            _stateManager.ToggleCapsLock();
        }
        
        _layoutManager.UpdateKeyLabels(_rootElement, _stateManager);
    }
}