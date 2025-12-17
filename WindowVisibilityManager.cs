using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with real-time focus tracking and auto-show support
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
    private readonly SettingsManager _settingsManager;
    
    private UIAutomationFocusTracker _uiAutomationTracker;
    private bool _isDisposed = false;
    private bool _autoShowEnabled = false;
    private readonly object _showLock = new object();
    private DateTime _lastAutoShowTime = DateTime.MinValue;
    private const int AUTO_SHOW_DEBOUNCE_MS = 300; // Prevent duplicate shows

    public WindowVisibilityManager(
        IntPtr windowHandle,
        Window window,
        WindowPositionManager positionManager,
        KeyboardStateManager stateManager,
        LayoutManager layoutManager,
        FrameworkElement rootElement,
        SettingsManager settingsManager,
        BackspaceRepeatHandler backspaceHandler = null,
        TrayIcon trayIcon = null)
    {
        _windowHandle = windowHandle;
        _window = window;
        _positionManager = positionManager;
        _stateManager = stateManager;
        _layoutManager = layoutManager;
        _rootElement = rootElement;
        _settingsManager = settingsManager;
        _backspaceHandler = backspaceHandler;
        _trayIcon = trayIcon;
        _focusManager = new FocusManager(windowHandle);
        
        // Initialize auto-show based on settings
        InitializeAutoShow();
        
        Logger.Info("WindowVisibilityManager initialized with real-time focus tracking and auto-show support");
    }

    /// <summary>
    /// Initialize auto-show functionality
    /// </summary>
    private void InitializeAutoShow()
    {
        _autoShowEnabled = _settingsManager.GetAutoShowOnTextInput();
        
        if (_autoShowEnabled)
        {
            EnableAutoShow();
        }
        
        Logger.Info($"Auto-show initialized: {(_autoShowEnabled ? "Enabled" : "Disabled")}");
    }

    /// <summary>
    /// Enable auto-show functionality
    /// </summary>
    private void EnableAutoShow()
    {
        if (_uiAutomationTracker == null)
        {
            try
            {
                Logger.Info("🔄 Creating UI Automation tracker with CLICK DETECTION...");
                
                // Create tracker with click requirement ENABLED (second parameter = true)
                _uiAutomationTracker = new UIAutomationFocusTracker(_windowHandle, requireClickForAutoShow: true);
                
                // Provide visibility checker to avoid unnecessary UI Automation calls
                _uiAutomationTracker.SetKeyboardVisibilityChecker(() => IsVisible());
                
                // Subscribe to events
                _uiAutomationTracker.TextInputFocused += OnTextInputFocused;
                _uiAutomationTracker.NonTextInputFocused += OnNonTextInputFocused;
                
                Logger.Info("✅ UI Automation tracker enabled with CLICK DETECTION for auto-show");
                Logger.Info("   → Keyboard will show when you CLICK on ANY text field");
                Logger.Info("   → Including already-focused fields!");
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Failed to enable UI Automation tracker - auto-show will NOT work", ex);
                _uiAutomationTracker = null;
            }
        }
        else
        {
            Logger.Debug("UI Automation tracker already exists");
        }
    }

    /// <summary>
    /// Disable auto-show functionality
    /// </summary>
    private void DisableAutoShow()
    {
        if (_uiAutomationTracker != null)
        {
            _uiAutomationTracker.TextInputFocused -= OnTextInputFocused;
            _uiAutomationTracker.NonTextInputFocused -= OnNonTextInputFocused;
            _uiAutomationTracker.Dispose();
            _uiAutomationTracker = null;
            
            Logger.Info("UI Automation tracker disabled");
        }
    }

    /// <summary>
    /// Update auto-show setting (called when settings change)
    /// </summary>
    public void UpdateAutoShowSetting()
    {
        bool newSetting = _settingsManager.GetAutoShowOnTextInput();
        
        if (newSetting == _autoShowEnabled)
            return;
        
        _autoShowEnabled = newSetting;
        
        if (_autoShowEnabled)
        {
            EnableAutoShow();
            Logger.Info("Auto-show enabled via settings");
        }
        else
        {
            DisableAutoShow();
            Logger.Info("Auto-show disabled via settings");
        }
    }

    /// <summary>
    /// Handle text input focused event from UI Automation
    /// </summary>
    private async void OnTextInputFocused(object sender, TextInputFocusEventArgs e)
    {
        Logger.Info($"🎯 AUTO-SHOW TRIGGERED! Text input focused - Type: {e.ControlType}, Class: '{e.ClassName}', Password: {e.IsPassword}");
        
        lock (_showLock)
        {
            // Check if keyboard is already visible
            if (IsVisible())
            {
                Logger.Debug("Keyboard already visible - skipping auto-show");
                return;
            }

            // Debounce: prevent multiple rapid shows
            var timeSinceLastShow = (DateTime.UtcNow - _lastAutoShowTime).TotalMilliseconds;
            if (timeSinceLastShow < AUTO_SHOW_DEBOUNCE_MS)
            {
                Logger.Debug($"Auto-show debounced ({timeSinceLastShow:F0}ms since last show)");
                return;
            }

            _lastAutoShowTime = DateTime.UtcNow;
        }
        
        // Show keyboard with a small delay to ensure focus has fully transitioned
        await Task.Delay(100);
        
        Logger.Info("📱 Showing keyboard automatically...");
        
        // Show keyboard with focus preservation
        Show(preserveFocus: true);
    }

    /// <summary>
    /// Handle non-text input focused event from UI Automation
    /// </summary>
    private void OnNonTextInputFocused(object sender, FocusEventArgs e)
    {
        // Currently we don't auto-hide on non-text focus
        // User can manually hide keyboard if needed
        Logger.Debug($"Non-text input focused - Type: {e.ControlType}, Class: '{e.ClassName}'");
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
                    Logger.Info("✔ Focus successfully restored to tracked window");
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
            
            // Disable auto-show
            DisableAutoShow();
            
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