using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

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
        Logger.Info($"Show called with preserveFocus={preserveFocus}");
        
        try
        {
            if (preserveFocus)
            {
                ShowWithFocusPreservation();
            }
            else
            {
                ShowNormal();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
        }
    }

    /// <summary>
    /// Show window normally - keyboard gets focus
    /// </summary>
    private void ShowNormal()
    {
        _positionManager?.PositionWindow(showWindow: true);
        ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
        _window.Activate();
        
        // Pause focus tracking while keyboard is visible
        _focusTracker?.PauseTracking();
        
        Logger.Info("Window shown and activated (no focus preservation)");
    }

    /// <summary>
    /// Show window with focus preservation - async to allow proper restoration
    /// </summary>
    private async void ShowWithFocusPreservation()
    {
        Logger.Info("ShowWithFocusPreservation started");

        // STEP 1: Pause tracking IMMEDIATELY before any operations
        // This prevents capturing focus changes during show/restore sequence
        _focusTracker?.PauseTracking();

        // STEP 2: Get tracked window and capture its focused control
        IntPtr trackedWindow = _focusTracker?.GetLastFocusedWindow() ?? IntPtr.Zero;
        IntPtr focusedControl = IntPtr.Zero;

        Logger.Info($"Tracked window from FocusTracker: 0x{trackedWindow:X}");

        if (trackedWindow != IntPtr.Zero && trackedWindow != _windowHandle)
        {
            focusedControl = CaptureFocusedControl(trackedWindow);
            if (focusedControl != IntPtr.Zero)
            {
                Logger.Debug($"Captured focused control: 0x{focusedControl:X}");
            }
            else
            {
                Logger.Debug("No focused control captured (might be at window level)");
            }
        }

        // STEP 3: Show keyboard window (using SW_SHOWNOACTIVATE to not steal focus)
        _positionManager?.PositionWindow(showWindow: false);
        ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
        Logger.Info("Keyboard window positioned and shown");

        // STEP 4: Small delay to let window render
        await Task.Delay(20);

        // STEP 5: Restore focus to tracked window
        if (trackedWindow != IntPtr.Zero && trackedWindow != _windowHandle)
        {
            Logger.Info($"Restoring focus to tracked window: 0x{trackedWindow:X}");
            
            // Restore foreground window
            bool restored = FocusHelper.RestoreForegroundWindow(trackedWindow);

            if (restored)
            {
                // Additional delay for window activation to settle
                await Task.Delay(15);

                // Restore specific control focus if we captured one
                if (focusedControl != IntPtr.Zero)
                {
                    RestoreControlFocus(trackedWindow, focusedControl);
                }
            }
            else
            {
                Logger.Warning($"Failed to restore foreground to window 0x{trackedWindow:X}");
            }
        }
        else
        {
            Logger.Warning("No tracked window to restore focus to");
        }
    }

    /// <summary>
    /// Capture the currently focused control in a window
    /// </summary>
    private IntPtr CaptureFocusedControl(IntPtr targetWindow)
    {
        try
        {
            uint foregroundThreadId = GetWindowThreadProcessId(targetWindow, out _);
            uint currentThreadId = GetCurrentThreadId();

            bool attached = false;
            try
            {
                if (foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                    if (!attached)
                    {
                        Logger.Debug("Failed to attach thread input for control capture");
                        return IntPtr.Zero;
                    }
                }

                IntPtr focusedControl = GetFocus();
                return focusedControl;
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error capturing focused control", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Restore focus to a specific control within a window
    /// </summary>
    private void RestoreControlFocus(IntPtr targetWindow, IntPtr focusedControl)
    {
        try
        {
            uint targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
            uint currentThreadId = GetCurrentThreadId();

            bool attached = false;
            try
            {
                if (targetThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                    if (!attached)
                    {
                        Logger.Warning("Failed to attach thread input for control focus restoration");
                        return;
                    }
                }

                IntPtr result = SetFocus(focusedControl);
                if (result != IntPtr.Zero)
                {
                    Logger.Info($"Successfully restored focus to control: 0x{focusedControl:X}");
                }
                else
                {
                    Logger.Debug($"SetFocus returned null for control: 0x{focusedControl:X} (might be OK if window-level focus is sufficient)");
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, targetThreadId, false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error restoring control focus", ex);
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
            
            // Resume focus tracking when keyboard is hidden
            _focusTracker?.ResumeTracking();
            
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
            Logger.Info("Toggle: hiding window");
            Hide();
        }
        else
        {
            Logger.Info("Toggle: showing window with focus preservation");
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
            Logger.Debug("Shift reset");
        }
        
        if (_stateManager.IsCtrlActive)
        {
            _stateManager.ToggleCtrl();
            Logger.Debug("Ctrl reset");
        }
        
        if (_stateManager.IsAltActive)
        {
            _stateManager.ToggleAlt();
            Logger.Debug("Alt reset");
        }
        
        if (_stateManager.IsCapsLockActive)
        {
            _stateManager.ToggleCapsLock();
            Logger.Debug("Caps Lock reset");
        }
        
        _layoutManager.UpdateKeyLabels(_rootElement, _stateManager);
        
        Logger.Info("All modifiers reset successfully");
    }
}