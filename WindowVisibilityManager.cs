using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with focus preservation and smooth slide animations using Composition API
/// </summary>
public class WindowVisibilityManager
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int ANIMATION_DURATION_MS = 250;
    private const float SLIDE_DISTANCE = 50f; // Distance to slide in pixels

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
    
    private Microsoft.UI.Composition.Visual _visual;
    private Microsoft.UI.Composition.Compositor _compositor;
    private bool _isAnimating = false;

    public WindowVisibilityManager(
        IntPtr windowHandle,
        Window window,
        WindowPositionManager positionManager,
        KeyboardStateManager stateManager,
        LayoutManager layoutManager,
        FrameworkElement rootElement,
        BackspaceRepeatHandler backspaceHandler = null,
        TrayIcon trayIcon = null)
    {
        _windowHandle = windowHandle;
        _window = window;
        _positionManager = positionManager;
        _stateManager = stateManager;
        _layoutManager = layoutManager;
        _rootElement = rootElement;
        _backspaceHandler = backspaceHandler;
        _trayIcon = trayIcon;
        _focusManager = new FocusManager(windowHandle);
        
        InitializeComposition();
    }

    /// <summary>
    /// Initialize Composition API
    /// </summary>
    private void InitializeComposition()
    {
        try
        {
            _visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_rootElement);
            _compositor = _visual.Compositor;
            
            // Ensure opacity is always 1 (no fade animations)
            _visual.Opacity = 1.0f;
            
            // Set initial offset to 0
            _visual.Offset = new Vector3(0, 0, 0);
            
            Logger.Info("Composition API initialized for window animations");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize Composition API", ex);
        }
    }

    /// <summary>
    /// Check if window is currently visible
    /// </summary>
    public bool IsVisible()
    {
        return IsWindowVisible(_windowHandle);
    }

    /// <summary>
    /// Show the window with slide animation and preserve focus
    /// </summary>
    public async void Show(bool preserveFocus = true)
    {
        if (_isAnimating)
        {
            Logger.Debug("Animation already in progress, skipping");
            return;
        }

        Logger.Info($"Show called with preserveFocus={preserveFocus}");
        
        try
        {
            _isAnimating = true;

            // Save current foreground window
            if (preserveFocus)
            {
                _focusManager.SaveForegroundWindow();
            }

            // Position window
            _positionManager?.PositionWindow(showWindow: false);
            
            // Set initial offset BEFORE showing window
            _visual.Offset = new Vector3(0, SLIDE_DISTANCE, 0);
            _visual.Opacity = 1.0f; // Ensure no fade
            
            // Show window without activation
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            
            Logger.Info($"Window shown. Current foreground: 0x{GetForegroundWindow():X}");

            // Small delay to ensure window is fully rendered
            await Task.Delay(20);

            // Create and start slide animation
            var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            offsetAnimation.InsertKeyFrame(1.0f, new Vector3(0, 0, 0));
            offsetAnimation.Target = "Offset";
            
            // Use smooth easing
            var easingFunction = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.25f, 0.1f),
                new Vector2(0.25f, 1.0f));
            offsetAnimation.SetReferenceParameter("easingFunction", easingFunction);

            // Create batch to track completion
            var batch = _compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            
            _visual.StartAnimation("Offset", offsetAnimation);
            
            batch.End();

            batch.Completed += (s, e) =>
            {
                _isAnimating = false;
                Logger.Debug("Show animation completed");
            };

            // Restore focus after brief delay
            if (preserveFocus && _focusManager.HasValidSavedWindow())
            {
                await Task.Delay(50);
                
                bool restored = await _focusManager.RestoreForegroundWindowAsync();
                
                if (restored)
                {
                    Logger.Info("Focus successfully preserved");
                }
                else
                {
                    Logger.Warning("Could not preserve focus");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show window", ex);
            _visual.Offset = new Vector3(0, 0, 0);
            _isAnimating = false;
        }
    }

    /// <summary>
    /// Show window synchronously (for compatibility)
    /// </summary>
    public void ShowSync(bool preserveFocus = true)
    {
        Show(preserveFocus);
    }

    /// <summary>
    /// Hide window with slide animation
    /// </summary>
    public void Hide()
    {
        if (_isAnimating)
        {
            Logger.Debug("Animation in progress, forcing hide");
            _visual.Offset = new Vector3(0, 0, 0);
            ShowWindow(_windowHandle, SW_HIDE);
            _isAnimating = false;
            return;
        }

        if (!IsVisible())
        {
            Logger.Debug("Window already hidden");
            return;
        }

        try
        {
            Logger.Info("Hiding window with slide animation");
            _isAnimating = true;
            
            // Reset modifiers
            ResetAllModifiers();
            
            // Clear saved foreground
            _focusManager.ClearSavedWindow();
            
            // Ensure we start from position 0
            _visual.Offset = new Vector3(0, 0, 0);
            _visual.Opacity = 1.0f; // Ensure no fade
            
            // Create slide down animation
            var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            offsetAnimation.InsertKeyFrame(1.0f, new Vector3(0, SLIDE_DISTANCE, 0));
            offsetAnimation.Target = "Offset";
            
            // Use smooth easing
            var easingFunction = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.75f, 0.0f),
                new Vector2(0.75f, 0.9f));
            offsetAnimation.SetReferenceParameter("easingFunction", easingFunction);

            // Create batch to track completion
            var batch = _compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            
            _visual.StartAnimation("Offset", offsetAnimation);
            
            batch.End();

            batch.Completed += (s, e) =>
            {
                _isAnimating = false;
                ShowWindow(_windowHandle, SW_HIDE);
                _visual.Offset = new Vector3(0, 0, 0); // Reset for next show
                Logger.Debug("Hide animation completed, window hidden");
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
            ShowWindow(_windowHandle, SW_HIDE);
            _visual.Offset = new Vector3(0, 0, 0);
            _isAnimating = false;
        }
    }

    /// <summary>
    /// Toggle visibility with slide animations
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
    /// Force restore focus to saved foreground window
    /// </summary>
    public async Task<bool> RestoreFocusAsync()
    {
        return await _focusManager.RestoreForegroundWindowAsync();
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
        try
        {
            Logger.Info("WindowVisibilityManager cleanup started");
            
            // Stop any running animations
            _visual?.StopAnimation("Offset");
            _visual?.StopAnimation("Opacity");
            _isAnimating = false;
            
            // Reset visual properties
            if (_visual != null)
            {
                _visual.Offset = new Vector3(0, 0, 0);
                _visual.Opacity = 1.0f;
            }
            
            // Reset modifiers
            ResetAllModifiers();
            
            // Clear saved window
            _focusManager.ClearSavedWindow();
            
            // Dispose resources
            _backspaceHandler?.Dispose();
            _trayIcon?.Dispose();
            
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
}