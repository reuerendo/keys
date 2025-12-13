using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    #region Services and Managers

    private readonly IntPtr _thisWindowHandle;
    private readonly KeyboardInputService _inputService;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly WindowStyleManager _styleManager;
    private readonly WindowPositionManager _positionManager;
    private readonly SettingsManager _settingsManager;
    private readonly InteractiveRegionsManager _interactiveRegionsManager;
    private readonly ClipboardManager _clipboardManager;
    
    private AutoShowManager _autoShowManager;
    private BackspaceRepeatHandler _backspaceHandler;
    private KeyboardEventCoordinator _eventCoordinator;
    private WindowVisibilityManager _visibilityManager;
    private LongPressPopup _longPressPopup;
    private TrayIcon _trayIcon;
    private SettingsDialogManager _settingsDialogManager;

    #endregion

    #region State Flags

    private bool _isClosing = false;
    private bool _isInitialPositionSet = false;

    #endregion

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("═══════════════════════════════════════════════════════");
        Logger.Info("═══ MainWindow Constructor Started ═══");
        Logger.Info("═══════════════════════════════════════════════════════");
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"Window handle: 0x{_thisWindowHandle:X}");
        
        // Initialize core services
        _settingsManager = new SettingsManager();
        _inputService = new KeyboardInputService(_thisWindowHandle);
        _stateManager = new KeyboardStateManager(_inputService);
        _layoutManager = new LayoutManager(_settingsManager);
        _styleManager = new WindowStyleManager(_thisWindowHandle, this);
        _positionManager = new WindowPositionManager(this, _thisWindowHandle);
        _interactiveRegionsManager = new InteractiveRegionsManager(_thisWindowHandle, this);
        _clipboardManager = new ClipboardManager(_inputService);
        
        // Configure window - CRITICAL: Set styles before any show operations
        Logger.Info("▶ Configuring window properties...");
        _positionManager.ConfigureWindowSize(_settingsManager.Settings.KeyboardScale);
        _styleManager.ApplyNoActivateStyle(); // ✅ CRITICAL: Must be called early
        _styleManager.SubclassWindow();
        _styleManager.ConfigureTitleBar();
        _styleManager.ConfigurePresenter();
        Logger.Info("✅ Window configured");
        
        // Initialize interactive regions
        _interactiveRegionsManager.Initialize();
        
        // Initialize tray icon
        InitializeTrayIcon();
        
        // Setup event handlers
        if (this.Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += MainWindow_Loaded;
            rootElement.SizeChanged += RootElement_SizeChanged;
        }
        
        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;

        Logger.Info($"Log file: {Logger.GetLogFilePath()}");
        Logger.Info("═══════════════════════════════════════════════════════");
        Logger.Info("═══ MainWindow Constructor Completed ═══");
        Logger.Info("═══════════════════════════════════════════════════════");
    }

    #region Initialization

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Logger.Info("▶ MainWindow_Loaded started");
        
        var rootElement = this.Content as FrameworkElement;
        
        // Initialize long press popup
        _longPressPopup = new LongPressPopup(rootElement, _stateManager);
        _longPressPopup.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
        
        // Initialize specialized handlers
        _backspaceHandler = new BackspaceRepeatHandler(_inputService);
        
        // ✅ NO FocusTracker - keyboard never steals focus
        _eventCoordinator = new KeyboardEventCoordinator(
            _inputService, 
            _stateManager, 
            _layoutManager, 
            _longPressPopup);
        
        // Initialize auto-show manager
        _autoShowManager = new AutoShowManager(_thisWindowHandle);
        _autoShowManager.ShowKeyboardRequested += AutoShowManager_ShowKeyboardRequested;
        _autoShowManager.IsEnabled = _settingsManager.GetAutoShowKeyboard();
        Logger.Info($"AutoShow: {(_autoShowManager.IsEnabled ? "Enabled" : "Disabled")}");
        
        // Initialize visibility manager (✅ NO FocusTracker parameter)
        _visibilityManager = new WindowVisibilityManager(
            _thisWindowHandle,
            this,
            _positionManager,
            _stateManager,
            _layoutManager,
            _autoShowManager,
            rootElement,
            _backspaceHandler,
            _trayIcon
        );
        
        // Initialize settings dialog manager
        _settingsDialogManager = new SettingsDialogManager(
            this,
            _settingsManager,
            _layoutManager,
            _stateManager,
            _autoShowManager,
            _visibilityManager
        );
        
        // Setup handlers
        _backspaceHandler.SetupHandlers(rootElement);
        _eventCoordinator.SetupLongPressHandlers(rootElement);
        
        // Initialize button references
        _stateManager.InitializeButtonReferences(rootElement);
        _layoutManager.InitializeLangButton(rootElement);
        
        // Update key labels to match current layout
        _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
        
        // Update interactive regions after UI is loaded
        _interactiveRegionsManager?.UpdateRegions();
        
        Logger.Info("✅ MainWindow fully initialized");
    }

    private void RootElement_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _interactiveRegionsManager?.UpdateRegions();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        Logger.Info($"▶ MainWindow_Activated: State={e.WindowActivationState}");
        
        // ✅ Re-apply styles on activation to ensure they persist
        _styleManager.ApplyNoActivateStyle();
        _styleManager.RemoveMinMaxButtons();
        
        if (!_isInitialPositionSet && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isInitialPositionSet = true;
            _positionManager?.PositionWindow();
            _visibilityManager?.MarkAsPositioned();  // ✅ Mark that positioning is complete
            Logger.Info("✅ Initial window position set");
        }
    }

    #endregion

    #region Tray Icon

    private void InitializeTrayIcon()
    {
        try
        {
            Logger.Info("▶ Initializing tray icon...");
            
            _trayIcon = new TrayIcon(_thisWindowHandle, "Virtual Keyboard");
            
            // Show keyboard without focus preservation (normal show from menu)
            _trayIcon.ShowRequested += (s, e) => {
                Logger.Info("Tray: Show requested");
                _visibilityManager?.Show(preserveFocus: false);
            };
            
            // Toggle with focus preservation
            _trayIcon.ToggleVisibilityRequested += (s, e) => {
                Logger.Info("Tray: Toggle requested");
                _visibilityManager?.Toggle();
            };
            
            _trayIcon.SettingsRequested += (s, e) => {
                Logger.Info("Tray: Settings requested");
                _settingsDialogManager?.ShowSettingsDialog();
            };
            
            _trayIcon.ExitRequested += (s, e) => {
                Logger.Info("Tray: Exit requested");
                ExitApplication();
            };
            
            _trayIcon.Show();
            Logger.Info("✅ Tray icon initialized and shown");
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
        Logger.Info("═══════════════════════════════════════════════════════");
        Logger.Info("AUTO-SHOW triggered by text input focus");
        _visibilityManager?.Show(preserveFocus: true);
        Logger.Info("═══════════════════════════════════════════════════════");
    }

    #endregion

    #region Window Drag Handler

    private void DragRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
        {
            Logger.Debug("Drag region clicked (handled by Caption region)");
        }
    }

    #endregion

    #region Clipboard Operations

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Clipboard: Copy");
        _clipboardManager?.Copy();
    }

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Clipboard: Cut");
        _clipboardManager?.Cut();
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Clipboard: Paste");
        _clipboardManager?.Paste();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Clipboard: Delete");
        _clipboardManager?.Delete();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Clipboard: Select All");
        _clipboardManager?.SelectAll();
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

    #region Application Exit

    private void ExitApplication()
    {
        try
        {
            Logger.Info("═══════════════════════════════════════════════════════");
            Logger.Info("═══ Application Exit Requested ═══");
            
            _isClosing = true;
            
            // Cleanup through visibility manager
            _visibilityManager?.Cleanup();
            
            // Restore window procedure
            _styleManager?.RestoreWindowProc();
            
            Logger.Info("✅ Cleanup completed, exiting...");
            Logger.Info("═══════════════════════════════════════════════════════");
            
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Logger.Error("Error during application exit", ex);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Logger.Info("▶ MainWindow_Closed event");
        
        if (!_isClosing)
        {
            // Minimize to tray instead of closing
            args.Handled = true;
            _visibilityManager?.Hide();
            Logger.Info("Window minimized to tray");
        }
        else
        {
            // Actually closing application
            _visibilityManager?.Cleanup();
            _styleManager?.RestoreWindowProc();
            Logger.Info("Window closed");
        }
    }

    #endregion
}