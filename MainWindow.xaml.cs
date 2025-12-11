using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private readonly SettingsManager _settingsManager;
    private AutoShowManager _autoShowManager;
    private LongPressPopup _longPressPopup;
    private TrayIcon _trayIcon;

    private bool _isClosing = false;
    private bool _isInitialPositionSet = false;
    private bool _isLongPressHandled = false;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        _settingsManager = new SettingsManager();
        _inputService = new KeyboardInputService(_thisWindowHandle);
        _stateManager = new KeyboardStateManager(_inputService);
        _layoutManager = new LayoutManager();
        _styleManager = new WindowStyleManager(_thisWindowHandle);
        _positionManager = new WindowPositionManager(this, _thisWindowHandle);
        
        // Initialize auto-show manager
        _autoShowManager = new AutoShowManager(_thisWindowHandle);
        _autoShowManager.ShowKeyboardRequested += AutoShowManager_ShowKeyboardRequested;
        _autoShowManager.IsEnabled = _settingsManager.GetAutoShowKeyboard();
        
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += MainWindow_Loaded;
        }
        
        ConfigureWindowSize();
        _styleManager.ApplyNoActivateStyle();
        InitializeTrayIcon();

        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
        
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _longPressPopup = new LongPressPopup(this.Content as FrameworkElement);
        _longPressPopup.CharacterSelected += LongPressPopup_CharacterSelected;
        
        SetupLongPressHandlers(this.Content as FrameworkElement);
        
        Logger.Info("Long-press handlers initialized");
    }

    private void AutoShowManager_ShowKeyboardRequested(object sender, EventArgs e)
    {
        Logger.Info("Auto-show triggered by text input focus");
        ShowWindow();
    }

    private void SetupLongPressHandlers(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag is string tag)
        {
            if (tag != "Shift" && tag != "Lang" && tag != "&.." && 
                tag != "Esc" && tag != "Tab" && tag != "Caps" && 
                tag != "Ctrl" && tag != "Alt" && tag != "Enter" && 
                tag != "Backspace" && tag != " ")
            {
                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(KeyButton_PointerPressed), true);
                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(KeyButton_PointerReleased), true);
                btn.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(KeyButton_PointerCanceled), true);
                btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(KeyButton_PointerCaptureLost), true);
                
                // Logger.Debug($"Long-press handlers added for key: {tag}");
            }
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    SetupLongPressHandlers(fe);
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            SetupLongPressHandlers(scrollContent);
        }
    }

    private void KeyButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isLongPressHandled = false;
        
        if (sender is Button btn)
        {
            Logger.Debug($"PointerPressed on button: {btn.Tag}");
            _longPressPopup?.StartPress(btn, _layoutManager.CurrentLayout.Name);
        }
    }

    private void KeyButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerReleased on button: {(sender as Button)?.Tag}");
        
        if (_longPressPopup != null)
        {
            _isLongPressHandled = false;
        }
        
        _longPressPopup?.CancelPress();
    }

    private void KeyButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCanceled on button: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    private void KeyButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCaptureLost on button: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    private void LongPressPopup_CharacterSelected(object sender, string character)
    {
        Logger.Info($"Long-press character selected: '{character}'");
        _isLongPressHandled = true;
        
        foreach (char c in character)
        {
            _inputService.SendUnicodeChar(c);
        }
    }

    private void ConfigureWindowSize()
    {
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        
        // Apply user's scale setting
        double userScale = _settingsManager.Settings.KeyboardScale;
        
        int physicalWidth = (int)(997 * scalingFactor * userScale);
        int physicalHeight = (int)(330 * scalingFactor * userScale);
        
        Logger.Info($"Window size calculated: {physicalWidth}x{physicalHeight} (DPI scale: {scalingFactor:F2}, User scale: {userScale:P0})");
        
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        _styleManager.ApplyNoActivateStyle();
        
        if (!_isInitialPositionSet && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isInitialPositionSet = true;
            _positionManager?.PositionWindow();
            Logger.Info("Initial window position set");
        }
    }

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

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLongPressHandled)
        {
            _isLongPressHandled = false;
            Logger.Debug("Skipping click - long press was handled");
            return;
        }

        if (sender is not Button button || button.Tag is not string keyCode)
            return;

        _longPressPopup?.HidePopup();

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
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            case "&..":
                _layoutManager.ToggleSymbolMode();
                _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            default:
                SendKey(keyCode);
                
                if (_stateManager.IsShiftActive && _layoutManager.IsLayoutKey(keyCode))
                {
                    _stateManager.ToggleShift();
                    _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                }
                
                _stateManager.ResetCtrlIfActive();
                _stateManager.ResetAltIfActive();
                break;
        }
    }

    private void SendKey(string key)
    {
        IntPtr currentForeground = _inputService.GetForegroundWindowHandle();
        string currentTitle = _inputService.GetWindowTitle(currentForeground);

        Logger.Info($"Clicking '{key}'. Target Window: 0x{currentForeground:X} ({currentTitle}). Modifiers: Ctrl={_stateManager.IsCtrlActive}, Alt={_stateManager.IsAltActive}, Shift={_stateManager.IsShiftActive}, Caps={_stateManager.IsCapsLockActive}");

        if (_inputService.IsKeyboardWindowFocused())
        {
            Logger.Warning("CRITICAL: Keyboard has focus! Keys will not be sent to target app. WS_EX_NOACTIVATE failed.");
        }

        byte controlVk = _inputService.GetVirtualKeyCode(key);
        if (controlVk != 0)
        {
            _inputService.SendVirtualKey(controlVk);
            return;
        }

        var keyDef = _layoutManager.GetKeyDefinition(key);
        if (keyDef != null)
        {
            if (_stateManager.IsCtrlActive || _stateManager.IsAltActive)
            {
                byte vk = _inputService.GetVirtualKeyCodeForLayoutKey(key);
                if (vk != 0)
                {
                    _inputService.SendVirtualKey(vk, skipModifiers: true);
                }
                else
                {
                    Logger.Warning($"No VK code found for '{key}' - shortcuts may not work");
                }
            }
            else
            {
                bool shouldCapitalize = (_stateManager.IsShiftActive || _stateManager.IsCapsLockActive) && keyDef.IsLetter;
                if (_stateManager.IsShiftActive && _stateManager.IsCapsLockActive && keyDef.IsLetter)
                {
                    shouldCapitalize = false;
                }
                bool useShift = _stateManager.IsShiftActive && !keyDef.IsLetter;
                
                string charToSend = (shouldCapitalize || useShift) ? keyDef.ValueShift : keyDef.Value;
                
                foreach (char c in charToSend)
                {
                    _inputService.SendUnicodeChar(c);
                }
            }
        }
        else
        {
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
            _positionManager?.PositionWindow();
            
            ShowWindow(_thisWindowHandle, SW_SHOW);
            this.Activate();
            
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
            ResetAllModifiers();
            
            ShowWindow(_thisWindowHandle, SW_HIDE);
            
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
    /// Reset all modifiers (Shift, Ctrl, Alt, Caps Lock)
    /// </summary>
    private void ResetAllModifiers()
    {
        Logger.Info("Resetting all modifiers before hiding window");
        
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
        
        _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
        
        Logger.Info("All modifiers reset successfully");
    }

    #endregion

    #region Settings and Exit

    private async void ShowSettingsDialog()
    {
        try
        {
            ShowWindow();
            
            var dialog = new SettingsDialog(_settingsManager)
            {
                XamlRoot = this.Content.XamlRoot
            };
            
            await dialog.ShowAsync();
            
            // Update auto-show setting immediately
            if (dialog.RequiresAutoShowUpdate)
            {
                bool newAutoShowValue = _settingsManager.GetAutoShowKeyboard();
                _autoShowManager.IsEnabled = newAutoShowValue;
                Logger.Info($"AutoShow setting updated to: {newAutoShowValue}");
            }
            
            // Handle restart if scale changed
            if (dialog.RequiresRestart)
            {
                await ShowRestartDialog();
            }
            
            Logger.Info("Settings dialog closed");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show settings dialog", ex);
        }
    }

    private async System.Threading.Tasks.Task ShowRestartDialog()
    {
        var restartDialog = new ContentDialog
        {
            Title = "Требуется перезапуск",
            Content = "Для применения изменений размера клавиатуры необходимо перезапустить приложение.\n\nПерезапустить сейчас?",
            PrimaryButtonText = "Перезапустить",
            CloseButtonText = "Позже",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };
        
        var result = await restartDialog.ShowAsync();
        
        if (result == ContentDialogResult.Primary)
        {
            RestartApplication();
        }
    }

    private void RestartApplication()
    {
        try
        {
            Logger.Info("Restarting application...");
            
            string executablePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executablePath))
            {
                System.Diagnostics.Process.Start(executablePath);
                ExitApplication();
            }
            else
            {
                Logger.Error("Could not determine executable path for restart");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to restart application", ex);
        }
    }

    private void ExitApplication()
    {
        try
        {
            _isClosing = true;
            ResetAllModifiers();
            
            _autoShowManager?.Dispose();
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
            args.Handled = true;
            HideWindow();
        }
        else
        {
            _autoShowManager?.Dispose();
            _trayIcon?.Dispose();
        }
    }

    #endregion
}