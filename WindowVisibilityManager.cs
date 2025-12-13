using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Manages window visibility using true non-activating approach
/// NO focus tracking, NO focus restoration - window never steals focus
/// </summary>
public class WindowVisibilityManager
{
    #region Win32 API

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8; // Show window without activation
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_SHOWWINDOW = 0x0040;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOPMOST = 0x00000008L;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    #endregion

    private readonly IntPtr _windowHandle;
    private readonly Window _window;
    private readonly WindowPositionManager _positionManager;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly AutoShowManager _autoShowManager;
    private readonly FrameworkElement _rootElement;
    private readonly BackspaceRepeatHandler _backspaceHandler;
    private readonly TrayIcon _trayIcon;

    private bool _isPositioned = false;

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
    /// Mark that initial positioning is complete
    /// </summary>
    public void MarkAsPositioned()
    {
        _isPositioned = true;
    }

    /// <summary>
    /// Show the window WITHOUT activating it (never steals focus)
    /// </summary>
    public void Show(bool preserveFocus = false)
    {
        Logger.Info("═══════════════════════════════════════════════════════");
        Logger.Info($"SHOW CALLED: preserveFocus={preserveFocus}");
        
        // Log focus state BEFORE show
        LogFocusState("BEFORE SHOW");
        
        try
        {
            // ✅ CRITICAL: Ensure WS_EX_NOACTIVATE is set
            EnsureNoActivateStyle();
            
            // Position window only on first show
            if (!_isPositioned)
            {
                _positionManager?.PositionWindow(showWindow: false);
                _isPositioned = true;
            }
            
            // Show window using non-activating method
            ShowWindowNonActivating();
            
            // Log focus state AFTER show
            LogFocusState("AFTER SHOW");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
        }
        
        Logger.Info("═══════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Show window using true non-activating approach
    /// Uses SetWindowPos with SWP_NOACTIVATE to prevent any focus stealing
    /// </summary>
    private void ShowWindowNonActivating()
    {
        Logger.Info("▶ ShowWindowNonActivating: Starting");
        
        // 1. Save current foreground window
        IntPtr currentForeground = GetForegroundWindow();
        Logger.Info($"  Current foreground: 0x{currentForeground:X}");
        
        // 2. Show window WITHOUT changing Z-order (already topmost via Presenter)
        bool result = SetWindowPos(
            _windowHandle,
            IntPtr.Zero,  // ✅ Don't change Z-order
            0, 0, 0, 0,
            SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW
        );
        
        if (result)
        {
            Logger.Info("✅ SetWindowPos succeeded - window shown without activation");
            
            // 3. Verify foreground hasn't changed
            System.Threading.Thread.Sleep(10); // Small delay for Windows to process
            IntPtr afterShow = GetForegroundWindow();
            
            if (afterShow != currentForeground && currentForeground != IntPtr.Zero)
            {
                Logger.Warning($"⚠ Foreground changed: 0x{currentForeground:X} -> 0x{afterShow:X}");
                Logger.Info("  Restoring original foreground...");
                
                if (SetForegroundWindow(currentForeground))
                {
                    Logger.Info("  ✅ Foreground restored");
                }
                else
                {
                    Logger.Warning("  ⚠ Failed to restore foreground");
                }
            }
            else
            {
                Logger.Info("  ✅ Foreground unchanged");
            }
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Warning($"⚠ SetWindowPos failed with error {error}, trying ShowWindow fallback");
            
            // Fallback: ShowWindow with SW_SHOWNA
            ShowWindow(_windowHandle, SW_SHOWNA);
            Logger.Info("✅ ShowWindow(SW_SHOWNA) used as fallback");
            
            // Check foreground again after fallback
            System.Threading.Thread.Sleep(10);
            IntPtr afterFallback = GetForegroundWindow();
            if (afterFallback != currentForeground && currentForeground != IntPtr.Zero)
            {
                Logger.Warning("⚠ Foreground changed after fallback, restoring...");
                SetForegroundWindow(currentForeground);
            }
        }
        
        Logger.Info("▶ ShowWindowNonActivating: Completed");
    }

    /// <summary>
    /// Ensure WS_EX_NOACTIVATE style is properly set
    /// This is critical for preventing focus theft
    /// </summary>
    private void EnsureNoActivateStyle()
    {
        Logger.Info("▶ EnsureNoActivateStyle: Checking extended styles");
        
        IntPtr exStylePtr = GetWindowLongPtr(_windowHandle, GWL_EXSTYLE);
        long currentExStyle = exStylePtr.ToInt64();
        
        Logger.Info($"  Current extended style: 0x{currentExStyle:X16}");
        
        bool hasNoActivate = (currentExStyle & WS_EX_NOACTIVATE) != 0;
        
        Logger.Info($"  Has WS_EX_NOACTIVATE: {hasNoActivate}");
        
        // Ensure WS_EX_NOACTIVATE is set
        long newExStyle = currentExStyle | WS_EX_NOACTIVATE;
        
        if (newExStyle != currentExStyle)
        {
            Logger.Warning($"⚠ WS_EX_NOACTIVATE missing, applying now");
            SetWindowLongPtr(_windowHandle, GWL_EXSTYLE, (IntPtr)newExStyle);
            Logger.Info($"  New extended style: 0x{newExStyle:X16}");
        }
        else if (hasNoActivate)
        {
            Logger.Info("✅ Extended styles are correct");
        }
        else
        {
            Logger.Error("❌ Failed to verify WS_EX_NOACTIVATE");
        }
    }

    /// <summary>
    /// Hide the window to tray
    /// </summary>
    public void Hide()
    {
        Logger.Info("═══════════════════════════════════════════════════════");
        Logger.Info("HIDE CALLED");
        
        // Log focus state BEFORE hide
        LogFocusState("BEFORE HIDE");
        
        try
        {
            // Reset all modifiers before hiding
            ResetAllModifiers();
            
            // Hide window
            ShowWindow(_windowHandle, SW_HIDE);
            Logger.Info("✅ Window hidden");
            
            // Notify AutoShowManager about hide (for cooldown)
            _autoShowManager?.NotifyKeyboardHidden();
            
            // Log focus state AFTER hide
            LogFocusState("AFTER HIDE");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
        }
        
        Logger.Info("═══════════════════════════════════════════════════════");
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
            Show(preserveFocus: true);
        }
    }

    /// <summary>
    /// Log detailed focus state for debugging
    /// </summary>
    private void LogFocusState(string context)
    {
        try
        {
            Logger.Info($"┌─── FOCUS STATE: {context} ───");
            
            // Get foreground window
            IntPtr foregroundWindow = GetForegroundWindow();
            string foregroundTitle = GetWindowTitle(foregroundWindow);
            uint foregroundPid;
            uint foregroundTid = GetWindowThreadProcessId(foregroundWindow, out foregroundPid);
            
            Logger.Info($"│ Foreground Window: 0x{foregroundWindow:X}");
            Logger.Info($"│   Title: '{foregroundTitle}'");
            Logger.Info($"│   PID: {foregroundPid}, TID: {foregroundTid}");
            Logger.Info($"│   Is Keyboard: {foregroundWindow == _windowHandle}");
            
            // Get focused control
            IntPtr focusedControl = GetFocus();
            Logger.Info($"│ Focused Control: 0x{focusedControl:X}");
            
            // Current thread ID
            uint currentTid = GetCurrentThreadId();
            Logger.Info($"│ Current Thread ID: {currentTid}");
            
            // Keyboard window state
            bool isKeyboardVisible = IsWindowVisible(_windowHandle);
            Logger.Info($"│ Keyboard Visible: {isKeyboardVisible}");
            
            Logger.Info($"└─────────────────────────────────────────");
        }
        catch (Exception ex)
        {
            Logger.Error($"Error logging focus state for {context}", ex);
        }
    }

    /// <summary>
    /// Get window title by handle
    /// </summary>
    private string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "<null>";
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        string title = sb.ToString();
        return string.IsNullOrEmpty(title) ? "<no title>" : title;
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
        Logger.Info("▶ Resetting all modifiers");
        
        if (_stateManager.IsShiftActive)
        {
            _stateManager.ToggleShift();
            Logger.Debug("  Shift reset");
        }
        
        if (_stateManager.IsCtrlActive)
        {
            _stateManager.ToggleCtrl();
            Logger.Debug("  Ctrl reset");
        }
        
        if (_stateManager.IsAltActive)
        {
            _stateManager.ToggleAlt();
            Logger.Debug("  Alt reset");
        }
        
        if (_stateManager.IsCapsLockActive)
        {
            _stateManager.ToggleCapsLock();
            Logger.Debug("  Caps Lock reset");
        }
        
        _layoutManager.UpdateKeyLabels(_rootElement, _stateManager);
        
        Logger.Info("✅ All modifiers reset successfully");
    }
}