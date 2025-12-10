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
    private LongPressPopup _longPressPopup;
    private TrayIcon _trayIcon;

    private bool _isClosing = false;
    private bool _isInitialPositionSet = false;
    private bool _isLongPressHandled = false; // Track if long press was triggered

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
        
        // Subscribe to Content.Loaded event
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += MainWindow_Loaded;
        }
        
        // Configure window size (but not position yet)
        ConfigureWindowSize();
        
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
    /// Handle window loaded event
    /// </summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Initialize long-press popup after UI is ready
        _longPressPopup = new LongPressPopup(this.Content as FrameworkElement);
        _longPressPopup.CharacterSelected += LongPressPopup_CharacterSelected;
        
        // Setup pointer handlers for all buttons
        SetupLongPressHandlers(this.Content as FrameworkElement);
        
        Logger.Info("Long-press handlers initialized");
    }

    /// <summary>
    /// Setup long-press handlers for all key buttons
    /// </summary>
    private void SetupLongPressHandlers(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag is string tag)
        {
            // Only setup for character keys, not control keys
            if (tag != "Shift" && tag != "Lang" && tag != "&.." && 
                tag != "Esc" && tag != "Tab" && tag != "Caps" && 
                tag != "Ctrl" && tag != "Alt" && tag != "Enter" && 
                tag != "Backspace" && tag != " ")
            {
                // Add pointer handlers BEFORE click handlers
                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(KeyButton_PointerPressed), true);
                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(KeyButton_PointerReleased), true);
                btn.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(KeyButton_PointerCanceled), true);
                btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(KeyButton_PointerCaptureLost), true);
                
                Logger.Debug($"Long-press handlers added for key: {tag}");
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

    /// <summary>
    /// Handle pointer pressed on key button
    /// </summary>
    private void KeyButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isLongPressHandled = false;
        
        if (sender is Button btn)
        {
            Logger.Debug($"PointerPressed on button: {btn.Tag}");
            _longPressPopup?.StartPress(btn, _layoutManager.CurrentLayout.Name);
        }
    }

    /// <summary>
    /// Handle pointer released on key button
    /// </summary>
    private void KeyButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerReleased on button: {(sender as Button)?.Tag}");
        
        // Check if popup is open - if so, long press was triggered
        if (_longPressPopup != null)
        {
            // We need to check if popup was shown
            // The popup hides itself when a character is selected
            _isLongPressHandled = false; // Reset for next press
        }
        
        _longPressPopup?.CancelPress();
    }

    /// <summary>
    /// Handle pointer canceled
    /// </summary>
    private void KeyButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCanceled on button: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    /// <summary>
    /// Handle pointer capture lost
    /// </summary>
    private void KeyButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCaptureLost on button: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    /// <summary>
    /// Handle character selected from long-press popup
    /// </summary>
    private void LongPressPopup_CharacterSelected(object sender, string character)
    {
        Logger.Info($"Long-press character selected: '{character}'");
        _isLongPressHandled = true;
        
        // Send the selected character
        foreach (char c in character)
        {
            _inputService.SendUnicodeChar(c);
        }
    }

    /// <summary>
    /// Configure window size and properties
    /// </summary>
    private void ConfigureWindowSize()
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
        
        // Set initial position only once, before first activation
        if (!_isInitialPositionSet && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isInitialPositionSet = true;
            _positionManager?.PositionWindow();
            Logger.Info("Initial window position set");
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
        // Skip if long press was handled
        if (_isLongPressHandled)
        {
            _isLongPressHandled = false;
            Logger.Debug("Skipping click - long press was handled");
            return;
        }

        if (sender is not Button button || button.Tag is not string keyCode)
            return;

        // Hide long-press popup if open
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
                // Update long-press popup layout
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            case "&..":
                _layoutManager.ToggleSymbolMode();
                _layoutManager.UpdateKeyLabels(this.Content as FrameworkElement, _stateManager);
                // Update long-press popup layout
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
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
            // Position window BEFORE showing it
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