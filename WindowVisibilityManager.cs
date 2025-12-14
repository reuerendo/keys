using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with focus preservation and smooth slide animations
/// Uses SetWindowPos instead of ShowWindow to avoid system animations
/// </summary>
public class WindowVisibilityManager
{
    // SetWindowPos flags
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_HIDEWINDOW = 0x0080;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    
    private const int ANIMATION_DURATION_MS = 250;
    private const float SLIDE_DISTANCE = 50f;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X, int Y,
        int cx, int cy,
        uint uFlags);

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
        DisableSystemAnimations();
    }

    /// <summary>
    /// Disable Windows DWM system animations for this window
    /// </summary>
    private void DisableSystemAnimations()
    {
        try
        {
            bool success = DwmHelper.DisableTransitions(_windowHandle);
            
            if (success)
            {
                Logger.Info("System window transitions disabled via DWM");
            }
            else
            {
                Logger.Warning("Could not disable DWM transitions (may not work on Windows 11)");
            }

            // Verify the setting
            bool disabled = DwmHelper.AreTransitionsDisabled(_windowHandle);
            Logger.Info($"DWM transitions disabled state: {disabled}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to disable system animations", ex);
        }
    }

    /// <summary>
    /// Initialize Composition API for custom animations
    /// </summary>
    private void InitializeComposition()
    {
        try
        {
            _visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_rootElement);
            _compositor = _visual.Compositor;
            
            // Force opacity to 1.0 - no fade effects
            _visual.Opacity = 1.0f;
            
            // Set initial offset to 0
            _visual.Offset = new Vector3(0, 0, 0);
            
            Logger.Info("Composition API initialized (no fade, slide only)");
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
    /// Show the window with slide animation using SetWindowPos (avoids system animations)
    /// </summary>
    public async void Show(bool preserveFocus = true)
    {
        if (_isAnimating)
        {
            Logger.Debug("Animation in progress, skipping");
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
            
            // Set initial offset BEFORE showing
            _visual.Offset = new Vector3(0, SLIDE_DISTANCE, 0);
            _visual.Opacity = 1.0f;
            
            // Show window using SetWindowPos (no system animations)
            bool success = SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | 
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);

            if (!success)
            {
                Logger.Warning($"SetWindowPos failed: {Marshal.GetLastWin32Error()}");
            }
            
            Logger.Info($"Window shown via SetWindowPos. Foreground: 0x{GetForegroundWindow():X}");

            // Wait for window to be fully rendered
            await Task.Delay(30);

            // Create and start slide animation
            var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            offsetAnimation.InsertKeyFrame(1.0f, new Vector3(0, 0, 0));
            
            var easingFunction = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.25f, 0.1f),
                new Vector2(0.25f, 1.0f));
            offsetAnimation.SetReferenceParameter("easingFunction", easingFunction);

            var batch = _compositor.CreateScopedBatch(
                Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            
            _visual.StartAnimation("Offset", offsetAnimation);
            batch.End();

            batch.Completed += (s, e) =>
            {
                _isAnimating = false;
                Logger.Debug("Show animation completed");
            };

            // Restore focus
            if (preserveFocus && _focusManager.HasValidSavedWindow())
            {
                await Task.Delay(50);
                
                bool restored = await _focusManager.RestoreForegroundWindowAsync();
                
                if (restored)
                {
                    Logger.Info("Focus preserved successfully");
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
    /// Show window synchronously
    /// </summary>
    public void ShowSync(bool preserveFocus = true)
    {
        Show(preserveFocus);
    }

    /// <summary>
    /// Hide window with slide animation using SetWindowPos
    /// </summary>
    public void Hide()
    {
        if (_isAnimating)
        {
            Logger.Debug("Animation in progress, forcing hide");
            _visual.Offset = new Vector3(0, 0, 0);
            
            SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | 
                SWP_NOACTIVATE | SWP_HIDEWINDOW);
            
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
            
            // Start from position 0
            _visual.Offset = new Vector3(0, 0, 0);
            _visual.Opacity = 1.0f;
            
            // Create slide down animation
            var offsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            offsetAnimation.InsertKeyFrame(1.0f, new Vector3(0, SLIDE_DISTANCE, 0));
            
            var easingFunction = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.75f, 0.0f),
                new Vector2(0.75f, 0.9f));
            offsetAnimation.SetReferenceParameter("easingFunction", easingFunction);

            var batch = _compositor.CreateScopedBatch(
                Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            
            _visual.StartAnimation("Offset", offsetAnimation);
            batch.End();

            batch.Completed += (s, e) =>
            {
                _isAnimating = false;
                
                // Hide window using SetWindowPos
                SetWindowPos(
                    _windowHandle,
                    IntPtr.Zero,
                    0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | 
                    SWP_NOACTIVATE | SWP_HIDEWINDOW);
                
                _visual.Offset = new Vector3(0, 0, 0);
                Logger.Debug("Hide animation completed, window hidden");
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
            
            SetWindowPos(
                _windowHandle,
                IntPtr.Zero,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | 
                SWP_NOACTIVATE | SWP_HIDEWINDOW);
            
            _visual.Offset = new Vector3(0, 0, 0);
            _isAnimating = false;
        }
    }

    /// <summary>
    /// Toggle visibility
    /// </summary>
    public void Toggle()
    {
        if (IsVisible())
        {
            Logger.Info("Toggle: hiding");
            Hide();
        }
        else
        {
            Logger.Info("Toggle: showing with focus preservation");
            Show(preserveFocus: true);
        }
    }

    /// <summary>
    /// Restore focus to saved window
    /// </summary>
    public async Task<bool> RestoreFocusAsync()
    {
        return await _focusManager.RestoreForegroundWindowAsync();
    }

    /// <summary>
    /// Check if keyboard has focus
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
            
            // Stop animations
            _visual?.StopAnimation("Offset");
            _visual?.StopAnimation("Opacity");
            _isAnimating = false;
            
            // Reset visual
            if (_visual != null)
            {
                _visual.Offset = new Vector3(0, 0, 0);
                _visual.Opacity = 1.0f;
            }
            
            // Re-enable DWM transitions (cleanup)
            DwmHelper.EnableTransitions(_windowHandle);
            
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