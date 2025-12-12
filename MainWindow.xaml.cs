using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using System.Collections.Generic;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    #region Win32 Imports

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint HTCAPTION = 0x0002;

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
    
    // Для управления non-client regions
    private InputNonClientPointerSource _nonClientPointerSource;

    #endregion

    #region State Flags

    private bool _isClosing = false;
    private bool _isInitialPositionSet = false;
    
    // Track last click time to prevent double-click maximize
    private DateTime _lastDragRegionClickTime = DateTime.MinValue;
    private uint _doubleClickTime;

    #endregion

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        // Get system double-click time
        _doubleClickTime = GetDoubleClickTime();
        Logger.Info($"System double-click time: {_doubleClickTime}ms");
        
        // Initialize core services
        _focusTracker = new FocusTracker(_thisWindowHandle);
        _settingsManager = new SettingsManager();
        _inputService = new KeyboardInputService(_thisWindowHandle);
        _stateManager = new KeyboardStateManager(_inputService);
        _layoutManager = new LayoutManager(_settingsManager);
        _styleManager = new WindowStyleManager(_thisWindowHandle);
        _positionManager = new WindowPositionManager(this, _thisWindowHandle);
        
        // Configure window
        ConfigureWindowSize();
        _styleManager.ApplyNoActivateStyle();
        
        // Initialize non-client pointer source для управления интерактивными областями
        InitializeNonClientPointerSource();
        
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

    private void InitializeNonClientPointerSource()
    {
        try
        {
            // Получаем WindowId из handle
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_thisWindowHandle);
            
            // Получаем InputNonClientPointerSource для управления non-client областями
            _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(windowId);
            
            Logger.Info("InputNonClientPointerSource initialized");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize InputNonClientPointerSource", ex);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var rootElement = this.Content as FrameworkElement;
        
        // Initialize long press popup
        _longPressPopup = new LongPressPopup(rootElement, _stateManager);
        
        // Set initial layout for long press popup
        _longPressPopup.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
        
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
        
        // Update key labels to match current layout
        _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
        
        // Initialize auto-show manager
        _autoShowManager = new AutoShowManager(_thisWindowHandle);
        _autoShowManager.ShowKeyboardRequested += AutoShowManager_ShowKeyboardRequested;
        _autoShowManager.IsEnabled = _settingsManager.GetAutoShowKeyboard();
        
        // ВАЖНО: Настраиваем интерактивные области после загрузки UI
        UpdateInteractiveRegions();
        
        Logger.Info("MainWindow fully initialized");
    }

    private void RootElement_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Обновляем интерактивные области при изменении размера
        UpdateInteractiveRegions();
    }

    /// <summary>
    /// Обновляет интерактивные области для кнопок в toolbar
    /// </summary>
    private void UpdateInteractiveRegions()
    {
        if (_nonClientPointerSource == null)
            return;

        try
        {
            var rootElement = this.Content as FrameworkElement;
            if (rootElement == null)
                return;

            // Получаем масштаб для конвертации в физические координаты
            double scale = GetRasterizationScale();

            // Список прямоугольников для интерактивных областей (кнопки)
            var interactiveRects = new List<Windows.Graphics.RectInt32>();

            // Получаем кнопки toolbar
            var toolbarButtons = new[]
            {
                rootElement.FindName("CopyButton") as Button,
                rootElement.FindName("CutButton") as Button,
                rootElement.FindName("PasteButton") as Button,
                rootElement.FindName("DeleteButton") as Button,
                rootElement.FindName("SelectAllButton") as Button
            };

            // Для каждой кнопки создаем интерактивную область
            foreach (var button in toolbarButtons)
            {
                if (button != null && button.ActualWidth > 0 && button.ActualHeight > 0)
                {
                    var rect = GetElementRect(button, rootElement, scale);
                    if (rect.HasValue)
                    {
                        interactiveRects.Add(rect.Value);
                        Logger.Info($"Added interactive region for {button.Name}: X={rect.Value.X}, Y={rect.Value.Y}, W={rect.Value.Width}, H={rect.Value.Height}");
                    }
                }
            }

            // Устанавливаем области как Passthrough (клики проходят к UI элементам)
            if (interactiveRects.Count > 0)
            {
                _nonClientPointerSource.SetRegionRects(
                    NonClientRegionKind.Passthrough,
                    interactiveRects.ToArray()
                );
                
                Logger.Info($"Set {interactiveRects.Count} interactive regions successfully");
            }

            // Теперь настраиваем область перетаскивания (DragRegion)
            var dragRegion = rootElement.FindName("DragRegion") as Border;
            if (dragRegion != null && dragRegion.ActualWidth > 0 && dragRegion.ActualHeight > 0)
            {
                var dragRect = GetElementRect(dragRegion, rootElement, scale);
                if (dragRect.HasValue)
                {
                    _nonClientPointerSource.SetRegionRects(
                        NonClientRegionKind.Caption,
                        new[] { dragRect.Value }
                    );
                    
                    Logger.Info($"Set drag region: X={dragRect.Value.X}, Y={dragRect.Value.Y}, W={dragRect.Value.Width}, H={dragRect.Value.Height}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update interactive regions", ex);
        }
    }

    /// <summary>
    /// Получает прямоугольник элемента в физических координатах
    /// </summary>
    private Windows.Graphics.RectInt32? GetElementRect(FrameworkElement element, FrameworkElement root, double scale)
    {
        try
        {
            var transform = element.TransformToVisual(root);
            var bounds = transform.TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight)
            );

            // Конвертируем в физические координаты (non-DPI aware)
            return new Windows.Graphics.RectInt32
            {
                X = (int)Math.Round(bounds.X * scale),
                Y = (int)Math.Round(bounds.Y * scale),
                Width = (int)Math.Round(bounds.Width * scale),
                Height = (int)Math.Round(bounds.Height * scale)
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to get element rect for {element?.Name}", ex);
            return null;
        }
    }

    /// <summary>
    /// Получает масштаб растеризации для конвертации в физические координаты
    /// </summary>
    private double GetRasterizationScale()
    {
        try
        {
            var rootElement = this.Content as FrameworkElement;
            if (rootElement?.XamlRoot?.RasterizationScale > 0)
            {
                return rootElement.XamlRoot.RasterizationScale;
            }
        }
        catch { }

        // Fallback: используем DPI
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        return dpi / 96.0;
    }

    private void ConfigureWindowSize()
    {
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float dpiScale = dpi / 96f;
        double userScale = _settingsManager.Settings.KeyboardScale;

        int baseWidth = 997;
        int baseHeight = 342; // Increased from 330 to accommodate toolbar
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
        
        // Configure custom title bar
        ConfigureTitleBar();
    }

    private void ConfigureTitleBar()
    {
        // Hide the default title bar
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var titleBar = this.AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            
            // Set title bar button colors - close button will be visible with default color
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            
            // Keep default foreground color for visibility
            // Setting to null uses system default (black icon on light background)
            
            Logger.Info("Custom title bar configured - close button visible");
        }
        else
        {
            Logger.Warning("Title bar customization not supported");
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

    #region Window Drag Handler - БОЛЬШЕ НЕ НУЖЕН

    // Этот обработчик больше не нужен, так как перетаскивание 
    // теперь управляется через InputNonClientPointerSource
    // Но оставляем его для обратной совместимости
    private void DragRegion_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Обработка двойного клика все еще может быть полезна
        if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
        {
            DateTime now = DateTime.Now;
            double timeSinceLastClick = (now - _lastDragRegionClickTime).TotalMilliseconds;
            
            if (timeSinceLastClick < _doubleClickTime)
            {
                Logger.Info($"Double-click detected on drag region ({timeSinceLastClick:F0}ms) - blocked to prevent maximize");
                e.Handled = true;
                _lastDragRegionClickTime = DateTime.MinValue;
                return;
            }
            
            _lastDragRegionClickTime = now;
        }
    }

    #endregion

    #region Clipboard Operations

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Copy requested");
        _inputService.SendCtrlKey('C');
    }

    private void CutButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Cut requested");
        _inputService.SendCtrlKey('X');
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Paste requested");
        _inputService.SendCtrlKey('V');
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Delete requested");
        _inputService.SendKey(0x2E); // VK_DELETE
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        Logger.Info("Select All requested");
        _inputService.SendCtrlKey('A');
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
            
            // Update layouts if changed (this includes default layout changes)
            if (dialog.RequiresLayoutUpdate)
            {
                _layoutManager.RefreshAvailableLayouts();
                var rootElement = this.Content as FrameworkElement;
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                Logger.Info("Keyboard layouts refreshed and default layout applied");
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