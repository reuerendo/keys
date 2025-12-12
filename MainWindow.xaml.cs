using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    #region Win32 Imports

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    #endregion

    #region Services and Managers

    private readonly IntPtr _thisWindowHandle;
    private readonly KeyboardInputService _inputService;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly WindowStyleManager _styleManager;
    private readonly WindowPositionManager _positionManager;
    private readonly SettingsManager _settingsManager;
    private readonly FocusTracker _focusTracker;
    
    private AutoShowManager _autoShowManager;
    private BackspaceRepeatHandler _backspaceHandler;
    private KeyboardEventCoordinator _eventCoordinator;
    private WindowVisibilityManager _visibilityManager;
    private LongPressPopup _longPressPopup;
    private TrayIcon _trayIcon;

    #endregion

    #region State Flags

    private bool _isClosing = false;
    private bool _isInitialPositionSet = false;

    #endregion

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        // Initialize core services
        _focusTracker = new FocusTracker(_thisWindowHandle);
        _settingsManager = new SettingsManager();
        _inputService = new KeyboardInputService(_thisWindowHandle);
        _stateManager = new KeyboardStateManager(_inputService);
        _layoutManager = new LayoutManager(_settingsManager); // Pass settings manager
        _styleManager = new WindowStyleManager(_thisWindowHandle);
        _positionManager = new WindowPositionManager(this, _thisWindowHandle);
        
        // Configure window
        ConfigureWindowSize();
        _styleManager.ApplyNoActivateStyle();
        
        // Initialize tray icon
        InitializeTrayIcon();
        
        // Setup event handlers
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += MainWindow_Loaded;
        }
        
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;

        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
    }

    #region Initialization

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var rootElement = this.Content as FrameworkElement;
        
        // Initialize long press popup
        _longPressPopup = new LongPressPopup(rootElement, _stateManager);
        
        // Initialize specialized handlers
        _backspaceHandler = new BackspaceRepeatHandler(_inputService);
        _eventCoordinator = new KeyboardEventCoordinator(_inputService, _stateManager, _layoutManager, _longPressPopup);
        
        // Initialize visibility manager
        _visibilityManager = new WindowVisibilityManager(
            _thisWindowHandle,
            this,
            _positionManager,
            _stateManager,
            _layoutManager,
            _focusTracker,
            _autoShowManager,
            rootElement
        );
        
        // Setup handlers
        _backspaceHandler.SetupHandlers(rootElement);
        _eventCoordinator.SetupLongPressHandlers(rootElement);
        
        // Initialize button references
        _stateManager.InitializeButtonReferences(rootElement);
        _layoutManager.InitializeLangButton(rootElement);
        
        // Initialize auto-show manager
        _autoShowManager = new AutoShowManager(_thisWindowHandle);
        _autoShowManager.ShowKeyboardRequested += AutoShowManager_ShowKeyboardRequested;
        _autoShowManager.IsEnabled = _settingsManager.GetAutoShowKeyboard();
        
        Logger.Info("MainWindow fully initialized");
    }

    private void ConfigureWindowSize()
    {
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float dpiScale = dpi / 96f;
        double userScale = _settingsManager.Settings.KeyboardScale;

        int baseWidth = 997;
        int baseHeight = 330;
        int physicalWidth = (int)(baseWidth * dpiScale * userScale);
        int physicalHeight = (int)(baseHeight * dpiScale * userScale);

        Logger.Info($"Window size: {physicalWidth}x{physicalHeight} (DPI: {dpiScale:F2}, User: {userScale:P0})");

        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));

        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Width = baseWidth;
            rootElement.Height = baseHeight;
            rootElement.HorizontalAlignment = HorizontalAlignment.Left;
            rootElement.VerticalAlignment = VerticalAlignment.Top;

            if (rootElement is Grid rootGrid && rootGrid.RenderTransform is ScaleTransform scaleTransform)
            {
                scaleTransform.ScaleX = userScale;
                scaleTransform.ScaleY = userScale;
                Logger.Info($"Applied ScaleTransform to existing transform: {userScale}x");
            }
            else
            {
                var transform = new ScaleTransform
                {
                    ScaleX = userScale,
                    ScaleY = userScale
                };
                rootElement.RenderTransform = transform;
                Logger.Info($"Created and applied new ScaleTransform: {userScale}x");
            }
        }

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        _styleManager.ApplyNoActivateStyle();
        _styleManager.RemoveMinMaxButtons();
        
        if (!_isInitialPositionSet && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isInitialPositionSet = true;
            _positionManager?.PositionWindow();
            Logger.Info("Initial window position set");
        }
    }

    #endregion

    #region Tray Icon

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon(_thisWindowHandle, "Virtual Keyboard");
            _trayIcon.ShowRequested += (s, e) => _visibilityManager?.Show(preserveFocus: false);
            _trayIcon.ToggleVisibilityRequested += (s, e) => _visibilityManager?.Toggle();
            _trayIcon.SettingsRequested += (s, e) => ShowSettingsDialog();
            _trayIcon.ExitRequested += (s, e) => ExitApplication();
            _trayIcon.Show();
            
            Logger.Info("Tray icon initialized and shown");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize tray icon", ex);
        }
    }

    #endregion

    #region Auto-Show Event Handler

    private void AutoShowManager_ShowKeyboardRequested(object sender, EventArgs e)
    {
        Logger.Info("Auto-show triggered by text input focus");
        _visibilityManager?.Show(preserveFocus: true);
    }

    #endregion

    #region Key Button Click Handler

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string keyCode)
            return;

        _eventCoordinator?.HandleKeyButtonClick(keyCode, this.Content as FrameworkElement);
    }

    #endregion

    #region Settings Dialog

    private async void ShowSettingsDialog()
    {
        try
        {
            _visibilityManager?.Show(preserveFocus: false);
            
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
            
            // Update layouts if changed
            if (dialog.RequiresLayoutUpdate)
            {
                _layoutManager.RefreshAvailableLayouts();
                var rootElement = this.Content as FrameworkElement;
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                Logger.Info("Keyboard layouts refreshed");
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

    #endregion

    #region Application Exit

    private void ExitApplication()
    {
        try
        {
            _isClosing = true;
            
            // Cleanup
            _backspaceHandler?.Dispose();
            _autoShowManager?.Dispose();
            _focusTracker?.Dispose();
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
            _visibilityManager?.Hide();
        }
        else
        {
            _backspaceHandler?.Dispose();
            _autoShowManager?.Dispose();
            _focusTracker?.Dispose();
            _trayIcon?.Dispose();
        }
    }

    #endregion
}