using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Manages window visibility, show/hide operations, focus restoration, and lifecycle cleanup
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
    private readonly FocusTracker _focusTracker;
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
        FocusTracker focusTracker,
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
        _focusTracker = focusTracker;
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
    /// Show the window with optional focus preservation
    /// </summary>
    public void Show(bool preserveFocus = false)
    {
        try
        {
            IntPtr previousForegroundWindow = IntPtr.Zero;
            if (preserveFocus)
            {
                previousForegroundWindow = GetForegroundWindow();
            }

            _positionManager?.PositionWindow(showWindow: true);

            if (preserveFocus)
            {
                IntPtr tracked = _focusTracker?.GetLastFocusedWindow() ?? IntPtr.Zero;

                if (tracked != IntPtr.Zero && tracked != _windowHandle)
                {
                    Logger.Info($"Restoring focus to last tracked window: 0x{tracked:X}");
                    FocusHelper.RestoreForegroundWindow(tracked);
                }
                else
                {
                    IntPtr prev = GetForegroundWindow();
                    if (prev != IntPtr.Zero && prev != _windowHandle)
                    {
                        Logger.Info($"Fallback restore to previous foreground window: 0x{prev:X}");
                        FocusHelper.RestoreForegroundWindow(prev);
                    }
                }
            }
            else
            {
                ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
                _window.Activate();
                Logger.Info("Window shown and activated");
            }
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
            ResetAllModifiers();
            
            ShowWindow(_windowHandle, SW_HIDE);
            
            // Notify AutoShowManager about hide (for cooldown)
            _autoShowManager?.NotifyKeyboardHidden();
            
            Logger.Info("Window hidden to tray");
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
            Hide();
        }
        else
        {
            Show(preserveFocus: true);
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
            _focusTracker?.Dispose();
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
            Logger.Info("Shift reset");
        }
        
        if (_stateManager.IsCtrlActive)
        {
            _stateManager.ToggleCtrl();
            Logger.Info("Ctrl reset");
        }
        
        if (_stateManager.IsAltActive)
        {
            _stateManager.ToggleAlt();
            Logger.Info("Alt reset");
        }
        
        if (_stateManager.IsCapsLockActive)
        {
            _stateManager.ToggleCapsLock();
            Logger.Info("Caps Lock reset");
        }
        
        _layoutManager.UpdateKeyLabels(_rootElement, _stateManager);
        
        Logger.Info("All modifiers reset successfully");
    }
}