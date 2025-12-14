using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with focus preservation (no animations)
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
    private readonly FrameworkElement _rootElement;
    private readonly BackspaceRepeatHandler _backspaceHandler;
    private readonly TrayIcon _trayIcon;
    private readonly FocusManager _focusManager;

    public WindowVisibilityManager(
        IntPtr windowHandle,
        Window window,
        WindowPositionManager positionManager,
        KeyboardStateManager stateManager,
        LayoutManager layoutManager,
        FrameworkElement rootElement,
        BackspaceRepeatHandler backspaceHandler = null,
        TrayIcon trayIcon = null)
    {
        _windowHandle = windowHandle;
        _window = window;
        _positionManager = positionManager;
        _stateManager = stateManager;
        _layoutManager = layoutManager;
        _rootElement = rootElement;
        _backspaceHandler = backspaceHandler;
        _trayIcon = trayIcon;
        _focusManager = new FocusManager(windowHandle);
        
        // Disable DWM transitions for cleaner show/hide
        DwmHelper.DisableTransitions(_windowHandle);
        
        Logger.Info("WindowVisibilityManager initialized (no window animations)");
    }

    /// <summary>
    /// Check if window is currently visible
    /// </summary>
    public bool IsVisible()
    {
        return IsWindowVisible(_windowHandle);
    }

    /// <summary>
    /// Show the window and preserve focus
    /// </summary>
    public async void Show(bool preserveFocus = true)
    {
        Logger.Info($"Show called with preserveFocus={preserveFocus}");
        
        try
        {
            // Save current foreground window
            if (preserveFocus)
            {
                _focusManager.SaveForegroundWindow();
            }

            // Position window
            _positionManager?.PositionWindow(showWindow: false);
            
            // Show window without activation
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            
            Logger.Info($"Window shown. Current foreground: 0x{GetForegroundWindow():X}");

            // Restore focus
            if (preserveFocus && _focusManager.HasValidSavedWindow())
            {
                await Task.Delay(50);
                
                bool restored = await _focusManager.RestoreForegroundWindowAsync();
                
                if (restored)
                {
                    Logger.Info("Focus successfully preserved");
                }
                else
                {
                    Logger.Warning("Could not preserve focus");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
        }
    }

    /// <summary>
    /// Show window synchronously
    /// </summary>
    public void ShowSync(bool preserveFocus = true)
    {
        Show(preserveFocus);
    }

    /// <summary>
    /// Hide window
    /// </summary>
    public void Hide()
    {
        if (!IsVisible())
        {
            Logger.Debug("Window already hidden");
            return;
        }

        try
        {
            Logger.Info("Hiding window");
            
            // Reset modifiers
            ResetAllModifiers();
            
            // Clear saved foreground
            _focusManager.ClearSavedWindow();
            
            // Hide window
            ShowWindow(_windowHandle, SW_HIDE);
            
            Logger.Debug("Window hidden");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
        }
    }

    /// <summary>
    /// Toggle visibility
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
    /// Force restore focus to saved foreground window
    /// </summary>
    public async Task<bool> RestoreFocusAsync()
    {
        return await _focusManager.RestoreForegroundWindowAsync();
    }

    /// <summary>
    /// Check if keyboard currently has focus
    /// </summary>
    public bool HasFocus()
    {
        return _focusManager.IsKeyboardFocused();
    }

    /// <summary>
    /// Cleanup resources
    /// </summary>
    public void Cleanup()
    {
        try
        {
            Logger.Info("WindowVisibilityManager cleanup started");
            
            // Re-enable DWM transitions (cleanup)
            DwmHelper.EnableTransitions(_windowHandle);
            
            // Reset modifiers
            ResetAllModifiers();
            
            // Clear saved window
            _focusManager.ClearSavedWindow();
            
            // Dispose resources
            _backspaceHandler?.Dispose();
            _trayIcon?.Dispose();
            
            Logger.Info("WindowVisibilityManager cleanup completed");
        }
        catch (Exception ex)
        {
            Logger.Error("Error during cleanup", ex);
        }
    }

    /// <summary>
    /// Reset all modifier keys
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