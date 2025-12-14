using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Window visibility manager with focus preservation and smooth animations
/// </summary>
public class WindowVisibilityManager
{
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const int ANIMATION_DURATION_MS = 250;

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
    
    private Storyboard _showStoryboard;
    private Storyboard _hideStoryboard;
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
        
        InitializeAnimations();
    }

    /// <summary>
    /// Initialize show/hide animations
    /// </summary>
    private void InitializeAnimations()
    {
        try
        {
            // Show animation - slide up + fade in
            _showStoryboard = new Storyboard();
            
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, _rootElement);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            
            var slideIn = new DoubleAnimation
            {
                From = 50,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slideIn, _rootElement);
            Storyboard.SetTargetProperty(slideIn, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            
            _showStoryboard.Children.Add(fadeIn);
            _showStoryboard.Children.Add(slideIn);
            
            _showStoryboard.Completed += (s, e) =>
            {
                _isAnimating = false;
                Logger.Debug("Show animation completed");
            };
            
            // Hide animation - slide down + fade out
            _hideStoryboard = new Storyboard();
            
            var fadeOut = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS - 50)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fadeOut, _rootElement);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            
            var slideOut = new DoubleAnimation
            {
                To = 30,
                Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS - 50)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(slideOut, _rootElement);
            Storyboard.SetTargetProperty(slideOut, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
            
            _hideStoryboard.Children.Add(fadeOut);
            _hideStoryboard.Children.Add(slideOut);
            
            _hideStoryboard.Completed += (s, e) =>
            {
                _isAnimating = false;
                ShowWindow(_windowHandle, SW_HIDE);
                Logger.Debug("Hide animation completed");
            };
            
            // Setup render transform if not already present
            if (_rootElement.RenderTransform == null || _rootElement.RenderTransform is not CompositeTransform)
            {
                _rootElement.RenderTransform = new CompositeTransform();
            }
            
            Logger.Info("Window animations initialized");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize animations", ex);
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
    /// Show the window with animation and preserve focus
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
            
            // Reset transform for animation
            if (_rootElement.RenderTransform is CompositeTransform transform)
            {
                transform.TranslateY = 50;
            }
            _rootElement.Opacity = 0;
            
            // Show window without activation
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            
            Logger.Info($"Window shown. Current foreground: 0x{GetForegroundWindow():X}");

            // Start show animation
            try
            {
                _showStoryboard.Begin();
            }
            catch (Exception ex)
            {
                Logger.Error($"Animation error: {ex.Message}");
                _rootElement.Opacity = 1;
                if (_rootElement.RenderTransform is CompositeTransform t)
                {
                    t.TranslateY = 0;
                }
                _isAnimating = false;
            }

            // Restore focus after animation starts
            if (preserveFocus && _focusManager.HasValidSavedWindow())
            {
                await Task.Delay(50); // Small delay for window to be fully shown
                
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
            _isAnimating = false;
        }
    }

    /// <summary>
    /// Show window using Composition API for smoother animation (alternative method)
    /// </summary>
    public async void ShowWithComposition(bool preserveFocus = true)
    {
        if (_isAnimating)
        {
            Logger.Debug("Animation already in progress");
            return;
        }

        Logger.Info("Showing window with composition animation");

        try
        {
            _isAnimating = true;

            if (preserveFocus)
            {
                _focusManager.SaveForegroundWindow();
            }

            _positionManager?.PositionWindow(showWindow: false);
            
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_rootElement);
            var compositor = visual.Compositor;

            // Create slide + fade animation
            var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            offsetAnimation.InsertKeyFrame(0.0f, new Vector3(0, 50, 0));
            offsetAnimation.InsertKeyFrame(1.0f, new Vector3(0, 0, 0));
            
            var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
            opacityAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            opacityAnimation.InsertKeyFrame(0.0f, 0.0f);
            opacityAnimation.InsertKeyFrame(1.0f, 1.0f);

            var easingFunction = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.25f, 0.1f),
                new Vector2(0.25f, 1.0f));
            
            offsetAnimation.SetReferenceParameter("easingFunction", easingFunction);
            opacityAnimation.SetReferenceParameter("easingFunction", easingFunction);

            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);

            var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            visual.StartAnimation("Offset", offsetAnimation);
            visual.StartAnimation("Opacity", opacityAnimation);
            batch.End();

            batch.Completed += (s, e) =>
            {
                _isAnimating = false;
                Logger.Debug("Composition animation completed");
            };

            if (preserveFocus && _focusManager.HasValidSavedWindow())
            {
                await Task.Delay(50);
                await _focusManager.RestoreForegroundWindowAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Composition animation failed: {ex.Message}");
            _isAnimating = false;
            
            // Fallback to immediate show
            ShowWindow(_windowHandle, SW_SHOWNOACTIVATE);
            _rootElement.Opacity = 1;
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
    /// Hide window with animation
    /// </summary>
    public void Hide()
    {
        if (_isAnimating)
        {
            Logger.Debug("Animation in progress, forcing hide");
            ShowWindow(_windowHandle, SW_HIDE);
            _isAnimating = false;
            return;
        }

        try
        {
            Logger.Info("Hiding window with animation");
            _isAnimating = true;
            
            // Reset modifiers
            ResetAllModifiers();
            
            // Clear saved foreground
            _focusManager.ClearSavedWindow();
            
            // Start hide animation
            try
            {
                _hideStoryboard.Begin();
            }
            catch (Exception ex)
            {
                Logger.Error($"Hide animation error: {ex.Message}");
                ShowWindow(_windowHandle, SW_HIDE);
                _isAnimating = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to hide window", ex);
            _isAnimating = false;
        }
    }

    /// <summary>
    /// Toggle visibility with animations
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
            _showStoryboard?.Stop();
            _hideStoryboard?.Stop();
            _isAnimating = false;
            
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