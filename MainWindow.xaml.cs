using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media; // Добавлено для CompositeTransform
using System;
using System.Threading.Tasks;

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
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle:X}");
        
        // Initialize core services
        _settingsManager = new SettingsManager();
        _inputService = new KeyboardInputService(_thisWindowHandle);
        _stateManager = new KeyboardStateManager(_inputService);
        _layoutManager = new LayoutManager(_settingsManager);
        _styleManager = new WindowStyleManager(_thisWindowHandle, this);
        _positionManager = new WindowPositionManager(this, _thisWindowHandle);
        _interactiveRegionsManager = new InteractiveRegionsManager(_thisWindowHandle, this);
        _clipboardManager = new ClipboardManager(_inputService);
        
        // Configure window
        _positionManager.ConfigureWindowSize(_settingsManager.Settings.KeyboardScale);
        _styleManager.ApplyNoActivateStyle();
        _styleManager.SubclassWindow();
        _styleManager.ConfigureTitleBar();
        _styleManager.ConfigurePresenter();
        
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

        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
    }

    #region Initialization

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var rootElement = this.Content as FrameworkElement;
        
        // Initialize long press popup WITH ANIMATIONS
        _longPressPopup = new LongPressPopup(rootElement, _stateManager);
        _longPressPopup.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
        
        // Initialize specialized handlers
        _backspaceHandler = new BackspaceRepeatHandler(_inputService);
        
        // Initialize event coordinator
        _eventCoordinator = new KeyboardEventCoordinator(
            _inputService, 
            _stateManager, 
            _layoutManager, 
            _longPressPopup);
        
        // Initialize visibility manager WITH REAL-TIME FOCUS TRACKING
        _visibilityManager = new WindowVisibilityManager(
            _thisWindowHandle,
            this,
            _positionManager,
            _stateManager,
            _layoutManager,
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
            _visibilityManager
        );
        
        // Setup handlers
        _backspaceHandler.SetupHandlers(rootElement);
        _eventCoordinator.SetupLongPressHandlers(rootElement);
        
        // SETUP BUTTON PRESS ANIMATIONS FOR ALL KEYBOARD BUTTONS
        ButtonAnimationHelper.SetupPressAnimations(rootElement);
        Logger.Info("Button press animations initialized for all keys");
        
        // Initialize button references
        _stateManager.InitializeButtonReferences(rootElement);
        _layoutManager.InitializeLangButton(rootElement);
        
        // Update key labels
        _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
        
        // Update interactive regions
        _interactiveRegionsManager?.UpdateRegions();
        
        Logger.Info("MainWindow fully initialized with real-time focus tracking");
    }

    private void RootElement_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
    {
        _interactiveRegionsManager?.UpdateRegions();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        _styleManager.ApplyNoActivateStyle();
        _styleManager.RemoveMinMaxButtons();
        
        // Handle initial position
        if (!_isInitialPositionSet && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _isInitialPositionSet = true;
            _positionManager?.PositionWindow();
            Logger.Info("Initial window position set");
            return;
        }

        // Restore focus if accidentally activated
        if (e.WindowActivationState == WindowActivationState.CodeActivated || 
            e.WindowActivationState == WindowActivationState.PointerActivated)
        {
            Logger.Warning($"Window was activated: {e.WindowActivationState}");
            
            if (_visibilityManager != null)
            {
                await Task.Delay(10);
                bool restored = await _visibilityManager.RestoreFocusAsync();
                
                if (restored)
                {
                    Logger.Info("Focus restored after unwanted activation");
                }
                else
                {
                    Logger.Warning("Failed to restore focus after activation");
                }
            }
        }
    }

    #endregion

    #region Tray Icon

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon(_thisWindowHandle, "Virtual Keyboard");
            
            // Show with animation and focus preservation
            _trayIcon.ShowRequested += (s, e) => _visibilityManager?.Show(preserveFocus: true);
            
            // Toggle with animation
            _trayIcon.ToggleVisibilityRequested += (s, e) => _visibilityManager?.Toggle();
            
            _trayIcon.SettingsRequested += (s, e) => _settingsDialogManager?.ShowSettingsDialog();
            _trayIcon.ExitRequested += (s, e) => ExitApplication();
            _trayIcon.Show();
            
            Logger.Info("Tray icon initialized");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize tray icon", ex);
        }
    }

    #endregion

    #region Window Drag Handler

    private void DragRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
        {
            Logger.Debug("Drag region clicked");
        }
    }

    #endregion

    #region Clipboard Operations

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Copy();
        
        // Add visual feedback animation
        if (sender is Button btn)
        {
            ButtonAnimationHelper.AnimateBounce(btn);
        }
    }

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Cut();
        
        if (sender is Button btn)
        {
            ButtonAnimationHelper.AnimateBounce(btn);
        }
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Paste();
        
        if (sender is Button btn)
        {
            ButtonAnimationHelper.AnimateBounce(btn);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Delete();
        
        if (sender is Button btn)
        {
            ButtonAnimationHelper.AnimateBounce(btn);
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.SelectAll();
        
        if (sender is Button btn)
        {
            ButtonAnimationHelper.AnimateBounce(btn);
        }
    }

    #endregion

    #region Key Button Click Handler

    private async void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string keyCode)
            return;

        // Handle key press
        _eventCoordinator?.HandleKeyButtonClick(keyCode, this.Content as FrameworkElement);
        
        // Restore focus if keyboard accidentally took it
        if (_visibilityManager?.HasFocus() == true)
        {
            Logger.Debug("Keyboard has focus after key click, restoring...");
            await _visibilityManager.RestoreFocusAsync();
        }
    }

    #endregion

    #region Application Exit

    private void ExitApplication()
    {
        try
        {
            _isClosing = true;
            
            // Cleanup - this will properly dispose FocusManager and stop tracking
            _visibilityManager?.Cleanup();
            _styleManager?.RestoreWindowProc();
            
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
            _visibilityManager?.Hide(); // Will use animated hide
        }
        else
        {
            // Proper cleanup on actual close
            _visibilityManager?.Cleanup();
            _styleManager?.RestoreWindowProc();
        }
    }

    #endregion
}