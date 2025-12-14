using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Numerics;

namespace VirtualKeyboard;

/// <summary>
/// Helper class for adding press animations to keyboard buttons
/// </summary>
public static class ButtonAnimationHelper
{
    private const float PRESS_SCALE = 0.85f;
    private const float NORMAL_SCALE = 1.0f;
    private const int ANIMATION_DURATION_MS = 100;

    /// <summary>
    /// Setup press animation for a button using Composition API
    /// </summary>
    public static void SetupPressAnimation(Button button)
    {
        if (button == null) return;

        button.PointerPressed += Button_PointerPressed;
        button.PointerReleased += Button_PointerReleased;
        button.PointerCanceled += Button_PointerCanceled;
        button.PointerCaptureLost += Button_PointerCaptureLost;
        
        // Ensure visual is initialized when button is loaded
        button.Loaded += (s, e) =>
        {
            if (s is Button btn)
            {
                InitializeVisual(btn);
            }
        };
    }

    /// <summary>
    /// Initialize the visual element for a button
    /// </summary>
    private static void InitializeVisual(Button button)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(button);
            
            // Set center point for scaling
            visual.CenterPoint = new Vector3(
                (float)button.ActualWidth / 2, 
                (float)button.ActualHeight / 2, 
                0);
            
            // Ensure scale is set to normal
            visual.Scale = new Vector3(NORMAL_SCALE, NORMAL_SCALE, 1.0f);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Visual initialization error: {ex.Message}");
        }
    }

    /// <summary>
    /// Setup press animations for all buttons in a container
    /// </summary>
    public static void SetupPressAnimations(FrameworkElement element)
    {
        if (element is Button button)
        {
            SetupPressAnimation(button);
        }
        else if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                {
                    SetupPressAnimations(fe);
                }
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement content)
        {
            SetupPressAnimations(content);
        }
    }

    private static void Button_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateScale(button, PRESS_SCALE);
        }
    }

    private static void Button_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateScale(button, NORMAL_SCALE);
        }
    }

    private static void Button_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateScale(button, NORMAL_SCALE);
        }
    }

    private static void Button_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateScale(button, NORMAL_SCALE);
        }
    }

    /// <summary>
    /// Animate button scale using Composition API
    /// </summary>
    private static void AnimateScale(Button button, float targetScale)
    {
        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(button);
            var compositor = visual.Compositor;

            // КРИТИЧЕСКИ ВАЖНО: установить центр масштабирования для визуального элемента
            visual.CenterPoint = new Vector3(
                (float)button.ActualWidth / 2, 
                (float)button.ActualHeight / 2, 
                0);

            // Create scale animation
            var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.Duration = TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS);
            scaleAnimation.InsertKeyFrame(1.0f, new Vector3(targetScale, targetScale, 1.0f));

            // Use cubic bezier easing for smooth animation
            var easingFunction = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.25f, 0.1f), 
                new Vector2(0.25f, 1.0f));
            
            // ИСПРАВЛЕНО: правильное применение easing функции
            scaleAnimation.SetReferenceParameter("easingFunction", easingFunction);
            
            visual.StartAnimation("Scale", scaleAnimation);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Animation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Animate button with a "bounce" effect (for special keys)
    /// </summary>
    public static void AnimateBounce(Button button)
    {
        if (button == null) return;

        try
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(button);
            var compositor = visual.Compositor;

            // Set center point
            visual.CenterPoint = new Vector3(
                (float)button.ActualWidth / 2, 
                (float)button.ActualHeight / 2, 
                0);

            // Create bounce animation
            var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.Duration = TimeSpan.FromMilliseconds(300);
            
            scaleAnimation.InsertKeyFrame(0.0f, new Vector3(1.0f, 1.0f, 1.0f));
            scaleAnimation.InsertKeyFrame(0.5f, new Vector3(1.15f, 1.15f, 1.0f));
            scaleAnimation.InsertKeyFrame(1.0f, new Vector3(1.0f, 1.0f, 1.0f));

            var easingFunction = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.4f, 0.0f), 
                new Vector2(0.2f, 1.0f));
            
            scaleAnimation.SetReferenceParameter("easingFunction", easingFunction);
            
            visual.StartAnimation("Scale", scaleAnimation);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Bounce animation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Create storyboard-based press animation (alternative approach)
    /// </summary>
    public static Storyboard CreatePressStoryboard(Button button, bool isPressed)
    {
        var storyboard = new Storyboard();
        
        // Ensure button has RenderTransform
        if (button.RenderTransform == null || button.RenderTransform is not ScaleTransform)
        {
            button.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            button.RenderTransform = new ScaleTransform();
        }
        
        // Scale X animation
        var scaleXAnimation = new DoubleAnimation
        {
            To = isPressed ? PRESS_SCALE : NORMAL_SCALE,
            Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        
        Storyboard.SetTarget(scaleXAnimation, button);
        Storyboard.SetTargetProperty(scaleXAnimation, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)");
        
        // Scale Y animation
        var scaleYAnimation = new DoubleAnimation
        {
            To = isPressed ? PRESS_SCALE : NORMAL_SCALE,
            Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        
        Storyboard.SetTarget(scaleYAnimation, button);
        Storyboard.SetTargetProperty(scaleYAnimation, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)");
        
        storyboard.Children.Add(scaleXAnimation);
        storyboard.Children.Add(scaleYAnimation);
        
        return storyboard;
    }

    /// <summary>
    /// Cleanup animation handlers
    /// </summary>
    public static void CleanupAnimations(Button button)
    {
        if (button == null) return;

        button.PointerPressed -= Button_PointerPressed;
        button.PointerReleased -= Button_PointerReleased;
        button.PointerCanceled -= Button_PointerCanceled;
        button.PointerCaptureLost -= Button_PointerCaptureLost;
    }
}