using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with real-time focus tracking
/// </summary>
public class WindowVisibilityManager : IDisposable
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
    private bool _isDisposed = false;

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
        
        Logger.Info("WindowVisibilityManager initialized with real-time focus tracking");
    }

    /// <summary>
    /// Check if window is currently visible
    /// </summary>
    public bool IsVisible()
    {
        return IsWindowVisible(_windowHandle);
    }

    /// <summary>
    /// Show the window with automatic focus preservation
    /// Focus is automatically tracked in real-time - no manual saving needed!
    /// </summary>
    public async void Show(bool preserveFocus = true)
    {
        Logger.Info($"Show called with preserveFocus={preserveFocus}");
        
        try
        {
            // Position window
            _positionManager?.PositionWindow(showWindow: false);
            
            // Show window without activation
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            
            Logger.Info($"Window shown. Current foreground: 0x{GetForegroundWindow():X}");

            // Restore focus to tracked window
            // The tracker already knows which window was active before the tray click!
            if (preserveFocus && _focusManager.HasValidTrackedWindow())
            {
                await Task.Delay(50);
                
                bool restored = await _focusManager.RestoreFocusAsync();
                
                if (restored)
                {
                    Logger.Info("✓ Focus successfully restored to tracked window");
                }
                else
                {
                    Logger.Warning("Could not restore focus");
                }
            }
            else if (preserveFocus)
            {
                Logger.Warning("No valid window tracked for focus restoration");
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
    /// Hide window - tracker continues to monitor foreground changes
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
            
            // Hide window
            ShowWindow(_windowHandle, SW_HIDE);
            
            Logger.Debug("Window hidden - tracker continues monitoring");
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
    /// Force restore focus to tracked window
    /// </summary>
    public async Task<bool> RestoreFocusAsync()
    {
        return await _focusManager.RestoreFocusAsync();
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
        if (_isDisposed)
            return;

        try
        {
            Logger.Info("WindowVisibilityManager cleanup started");
            
            // Reset modifiers
            ResetAllModifiers();
            
            // Clear tracked window
            _focusManager.ClearTrackedWindow();
            
            // Dispose resources
            _focusManager?.Dispose();
            _backspaceHandler?.Dispose();
            _trayIcon?.Dispose();
            
            _isDisposed = true;
            
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

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }

    ~WindowVisibilityManager()
    {
        Dispose();
    }
}