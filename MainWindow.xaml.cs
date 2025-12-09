using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    // Window visibility constants
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    // P/Invoke for window operations
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    // Services and managers
    private readonly IntPtr _thisWindowHandle;
    private readonly KeyboardInputService _inputService;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly WindowStyleManager _styleManager;
    private readonly WindowPositionManager _positionManager;
    private TrayIcon _trayIcon;

    private bool _isClosing = false;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        // Initialize services and managers
        _inputService = new KeyboardInputService(_thisWindowHandle);
        _stateManager = new KeyboardStateManager(_inputService);
        _layoutManager = new LayoutManager();
        _styleManager = new WindowStyleManager(_thisWindowHandle);
        _positionManager = new WindowPositionManager(this, _thisWindowHandle);
        
        // Configure window
        ConfigureWindow();
        
        // Apply window styles
        _styleManager.ApplyNoActivateStyle();

        // Initialize tray icon
        InitializeTrayIcon();

        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
        
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;
    }

    /// <summary>
    /// Configure window size and properties
    /// </summary>
    private void ConfigureWindow()
    {
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        
        // Calculate window size with extra margin for ScrollViewer and borders
        int physicalWidth = (int)(997 * scalingFactor);
        int physicalHeight = (int)(336 * scalingFactor);
        
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
    }

    /// <summary>
    /// Handle window activation
    /// </summary>
    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        _styleManager.ApplyNoActivateStyle();
        
        // Position window on activation
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            // Use Dispatcher for delayed positioning to ensure window is fully rendered
            DispatcherQueue.TryEnqueue(() =>
            {
                System.Threading.Tasks.Task.Delay(50).ContinueWith(_ =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _positionManager?.PositionWindow();
                    });
                });
            });
        }
    }

    /// <summary>
    /// Initialize tray icon
    /// </summary>
    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon(_thisWindowHandle, "Virtual Keyboard");
            _trayIcon.ShowRequested += TrayIcon_ShowRequested;
            _trayIcon.ToggleVisibilityRequested += TrayIcon_ToggleVisibilityRequested;
            _trayIcon.SettingsRequested += TrayIcon_SettingsRequested;
            _trayIcon.ExitRequested += TrayIcon_ExitRequested;
            _trayIcon.Show();
            
            Logger.Info("Tray icon initialized and shown");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize tray icon", ex);
        }
    }

    /// <summary>
    /// Handle key button click
    /// </summary>
    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string keyCode)
            return;

        switch (keyCode)
        {
            case "Shift":
                _stateManager.ToggleShift();
                _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                break;
                
            case "Caps":
                _stateManager.ToggleCapsLock();
                _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                break;
                
            case "Ctrl":
                _stateManager.ToggleCtrl();
                break;
                
            case "Alt":
                _stateManager.ToggleAlt();
                break;
                
            case "Lang":
                _layoutManager.SwitchLanguage();
                _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                break;
                
            case "&..":
                _layoutManager.ToggleSymbolMode();
                _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                break;
                
            default:
                SendKey(keyCode);
                
                // Auto-reset modifiers after key press
                if (_stateManager.IsShiftActive && _layoutManager.IsLayoutKey(keyCode))
                {
                    _stateManager.ToggleShift();
                    _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                }
                
                // Reset Ctrl and Alt after any key press
                _stateManager.ResetCtrlIfActive();
                _stateManager.ResetAltIfActive();
                break;
        }
    }

    /// <summary>
    /// Send key to target application
    /// </summary>
    private void SendKey(string key)
    {
        IntPtr currentForeground = _inputService.GetForegroundWindowHandle();
        string currentTitle = _inputService.GetWindowTitle(currentForeground);

        Logger.Info($"Clicking '{key}'. Target Window: 0x{currentForeground:X} ({currentTitle}). Modifiers: Ctrl={_stateManager.IsCtrlActive}, Alt={_stateManager.IsAltActive}, Shift={_stateManager.IsShiftActive}, Caps={_stateManager.IsCapsLockActive}");

        if (_inputService.IsKeyboardWindowFocused())
        {
            Logger.Warning("CRITICAL: Keyboard has focus! Keys will not be sent to target app. WS_EX_NOACTIVATE failed.");
        }

        // Check if this is a control key (arrows, etc.)
        byte controlVk = _inputService.GetVirtualKeyCode(key);
        if (controlVk != 0)
        {
            // This is a control key - send VK code
            _inputService.SendVirtualKey(controlVk);
            return;
        }

        // Check if key is in current layout
        var keyDef = _layoutManager.GetKeyDefinition(key);
        if (keyDef != null)
        {
            // For shortcuts (Ctrl/Alt pressed), we need to send VK codes, not Unicode
            if (_stateManager.IsCtrlActive || _stateManager.IsAltActive)
            {
                byte vk = _inputService.GetVirtualKeyCodeForLayoutKey(key);
                if (vk != 0)
                {
                    // Modifiers are already pressed in OS, just send the VK code
                    _inputService.SendVirtualKey(vk, skipModifiers: true);
                }
                else
                {
                    Logger.Warning($"No VK code found for '{key}' - shortcuts may not work");
                }
            }
            else
            {
                // Normal typing - use Unicode
                bool shouldCapitalize = (_stateManager.IsShiftActive || _stateManager.IsCapsLockActive) && keyDef.IsLetter;
                // Shift + Caps Lock cancel each other
                if (_stateManager.IsShiftActive && _stateManager.IsCapsLockActive && keyDef.IsLetter)
                {
                    shouldCapitalize = false;
                }
                // For non-letters, only shift affects output
                bool useShift = _stateManager.IsShiftActive && !keyDef.IsLetter;
                
                string charToSend = (shouldCapitalize || useShift) ? keyDef.ValueShift : keyDef.Value;
                
                // Send each character
                foreach (char c in charToSend)
                {
                    _inputService.SendUnicodeChar(c);
                }
            }
        }
        else
        {
            // For standalone characters not in layout
            if (key.Length == 1 && !char.IsControl(key[0]))
            {
                _inputService.SendUnicodeChar(key[0]);
            }
        }
    }

    #region Tray Icon Event Handlers

    private void TrayIcon_ShowRequested(object sender, EventArgs e)
    {
        ShowWindow();
    }

    private void TrayIcon_ToggleVisibilityRequested(object sender, EventArgs e)
    {
        bool isVisible = IsWindowVisible(_thisWindowHandle);
        
        if (isVisible)
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    private void TrayIcon_SettingsRequested(object sender, EventArgs e)
    {
        ShowSettingsDialog();
    }

    private void TrayIcon_ExitRequested(object sender, EventArgs e)
    {
        ExitApplication();
    }

    #endregion

    #region Window Visibility Management

    private void ShowWindow()
    {
        try
        {
            ShowWindow(_thisWindowHandle, SW_SHOW);
            this.Activate();
            
            // Position window after showing
            System.Threading.Tasks.Task.Delay(100).ContinueWith(_ =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    _positionManager?.PositionWindow();
                });
            });
            
            Logger.Info("Window shown from tray");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
        }
    }

    private void HideWindow()
    {
        try
        {
            ShowWindow(_thisWindowHandle, SW_HIDE);
            Logger.Info("Window hidden to tray");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
        }
    }

    #endregion

    #region Settings and Exit

    private async void ShowSettingsDialog()
    {
        try
        {
            ShowWindow();
            
            var dialog = new ContentDialog
            {
                Title = "Настройки",
                Content = "Окно настроек в разработке.\n\nЗдесь можно будет настроить:\n- Язык интерфейса\n- Горячие клавиши\n- Автозапуск\n- Прозрачность окна",
                CloseButtonText = "Закрыть",
                XamlRoot = this.Content.XamlRoot
            };
            
            await dialog.ShowAsync();
            Logger.Info("Settings dialog shown");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show settings dialog", ex);
        }
    }

    private void ExitApplication()
    {
        try
        {
            _isClosing = true;
            _trayIcon?.Dispose();
            Logger.Info("Application exiting");
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Logger.Error("Error during application exit", ex);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (!_isClosing)
        {
            // Minimize to tray instead of closing
            args.Handled = true;
            HideWindow();
        }
        else
        {
            // Actually closing - cleanup
            _trayIcon?.Dispose();
        }
    }

    #endregion
}