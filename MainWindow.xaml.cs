using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        
        // Initialize long press popup
        _longPressPopup = new LongPressPopup(rootElement, _stateManager);
        _longPressPopup.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
        
        // Initialize specialized handlers
        _backspaceHandler = new BackspaceRepeatHandler(_inputService);
        
        // Initialize event coordinator (without focus tracker)
        _eventCoordinator = new KeyboardEventCoordinator(
            _inputService, 
            _stateManager, 
            _layoutManager, 
            _longPressPopup);
        
        // Initialize auto-show manager first
        _autoShowManager = new AutoShowManager(_thisWindowHandle);
        _autoShowManager.ShowKeyboardRequested += AutoShowManager_ShowKeyboardRequested;
        _autoShowManager.IsEnabled = _settingsManager.GetAutoShowKeyboard();
        
        // Initialize visibility manager (without focus tracker)
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
        
        Logger.Info("MainWindow fully initialized");
    }

    private void RootElement_SizeChanged(object sender, SizeChangedEventArgs e)
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
			return; // Don't restore focus on initial setup
		}

		// If window gets activated (shouldn't happen with WS_EX_NOACTIVATE, but just in case)
		if (e.WindowActivationState == WindowActivationState.CodeActivated || 
			e.WindowActivationState == WindowActivationState.PointerActivated)
		{
			Logger.Warning($"Window was activated: {e.WindowActivationState}");
			
			// Immediately restore focus to the foreground application
			if (_visibilityManager != null)
			{
				await Task.Delay(10); // Small delay to ensure activation is complete
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
			
			// Show with focus preservation
			_trayIcon.ShowRequested += (s, e) => _visibilityManager?.Show(preserveFocus: true);
			
			// Toggle with focus preservation
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

    #region Auto-Show Event Handler

    private void AutoShowManager_ShowKeyboardRequested(object sender, EventArgs e)
    {
        Logger.Info("Auto-show triggered by text input focus");
        _visibilityManager?.Show(preserveFocus: true);
    }

    #endregion

    #region Window Drag Handler

    private void DragRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
        {
            Logger.Debug("Drag region clicked (dragging handled by system via Caption region)");
        }
    }

    #endregion

    #region Clipboard Operations

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Copy();
    }

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Cut();
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Paste();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.Delete();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        _clipboardManager?.SelectAll();
    }

    #endregion

    #region Key Button Click Handler

	private async void KeyButton_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Button button || button.Tag is not string keyCode)
			return;

		_eventCoordinator?.HandleKeyButtonClick(keyCode, this.Content as FrameworkElement);
		
		// After handling the key, check if we accidentally took focus and restore it
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
            
            // Cleanup through visibility manager
            _visibilityManager?.Cleanup();
            
            // Restore window procedure
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
            _visibilityManager?.Hide();
        }
        else
        {
            _visibilityManager?.Cleanup();
            _styleManager?.RestoreWindowProc();
        }
    }

    #endregion
}