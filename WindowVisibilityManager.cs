using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with real-time focus tracking and auto-show support.
/// Uses WinEventFocusTracker with strict input validation algorithm.
/// </summary>
public class WindowVisibilityManager : IDisposable
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

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
    
    private WinEventFocusTracker _focusTracker;
    private PointerInputTracker _pointerTracker;
    
    private bool _isDisposed = false;
    private bool _autoShowEnabled = false;
    private readonly object _showLock = new object();

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
        
        // Initialize PointerInputTracker once
        try 
        {
            _pointerTracker = new PointerInputTracker();
            Logger.Info("✅ PointerInputTracker initialized in WindowVisibilityManager");
        }
        catch (Exception ex)
        {
            Logger.Error("❌ Failed to init PointerInputTracker", ex);
        }

        InitializeAutoShow();
        
        Logger.Info("WindowVisibilityManager initialized with strict input validation algorithm");
    }

    private void InitializeAutoShow()
    {
        _autoShowEnabled = _settingsManager.GetAutoShowOnTextInput();
        
        if (_autoShowEnabled)
        {
            EnableAutoShow();
        }
        else
        {
            Logger.Info("Auto-show is disabled in settings");
        }
    }

    private void EnableAutoShow()
    {
        if (_focusTracker == null)
        {
            try
            {
                Logger.Info("🔄 Creating WinEvent Focus Tracker with strict validation...");
                
                if (_pointerTracker == null)
                {
                    _pointerTracker = new PointerInputTracker();
                }

                _focusTracker = new WinEventFocusTracker(_windowHandle, _pointerTracker);
                _focusTracker.SetKeyboardVisibilityChecker(() => IsVisible());
                _focusTracker.TextInputFocused += OnTextInputFocused;
                _focusTracker.NonTextInputFocused += OnNonTextInputFocused;
                
                Logger.Info("✅ Native Focus Tracker enabled (WinEvents + MSAA + Strict Validation)");
            }
            catch (Exception ex)
            {
                Logger.Error("❌ Failed to enable Focus Tracker", ex);
                _focusTracker = null;
            }
        }
    }

    private void DisableAutoShow()
    {
        if (_focusTracker != null)
        {
            _focusTracker.TextInputFocused -= OnTextInputFocused;
            _focusTracker.NonTextInputFocused -= OnNonTextInputFocused;
            _focusTracker.Dispose();
            _focusTracker = null;
            Logger.Info("Focus tracker disabled");
        }
    }

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

    private async void OnTextInputFocused(object sender, TextInputFocusEventArgs e)
    {
        Logger.Info($"🎯 AUTO-SHOW TRIGGERED! ControlType: {e.ControlType}, Class: '{e.ClassName}'");
        
        lock (_showLock)
        {
            if (IsVisible())
            {
                Logger.Debug("Keyboard already visible - skipping");
                return;
            }
        }
        
        await Task.Delay(100);
        
        Logger.Info("📱 Showing keyboard...");
        Show(preserveFocus: true);
    }

    private void OnNonTextInputFocused(object sender, FocusEventArgs e)
    {
        // Optional: Auto-hide logic could go here if desired
    }

    public bool IsVisible()
    {
        return IsWindowVisible(_windowHandle);
    }

    public async void Show(bool preserveFocus = true)
    {
        Logger.Info($"Show called with preserveFocus={preserveFocus}");
        
        try
        {
            _positionManager?.PositionWindow(showWindow: false);
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            
            if (preserveFocus && _focusManager.HasValidTrackedWindow())
            {
                await Task.Delay(50);
                await _focusManager.RestoreFocusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
        }
    }

    public void ShowSync(bool preserveFocus = true)
    {
        Show(preserveFocus);
    }

    public void Hide()
    {
        if (!IsVisible()) return;

        try
        {
            ResetAllModifiers();
            ShowWindow(_windowHandle, SW_HIDE);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
        }
    }

    public void Toggle()
    {
        if (IsVisible()) Hide();
        else Show(preserveFocus: true);
    }

    public async Task<bool> RestoreFocusAsync()
    {
        return await _focusManager.RestoreFocusAsync();
    }

    public bool HasFocus()
    {
        return _focusManager.IsKeyboardFocused();
    }

    public void Cleanup()
    {
        if (_isDisposed) return;

        try
        {
            ResetAllModifiers();
            DisableAutoShow();
            
            _focusManager.ClearTrackedWindow();
            _focusManager?.Dispose();
            
            _pointerTracker?.Dispose();
            _backspaceHandler?.Dispose();
            _trayIcon?.Dispose();
            
            _isDisposed = true;
        }
        catch (Exception ex)
        {
            Logger.Error("Error during cleanup", ex);
        }
    }

    private void ResetAllModifiers()
    {
        if (_stateManager.IsShiftActive) _stateManager.ToggleShift();
        if (_stateManager.IsCtrlActive) _stateManager.ToggleCtrl();
        if (_stateManager.IsAltActive) _stateManager.ToggleAlt();
        if (_stateManager.IsCapsLockActive) _stateManager.ToggleCapsLock();
        
        _layoutManager.UpdateKeyLabels(_rootElement, _stateManager);
    }

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