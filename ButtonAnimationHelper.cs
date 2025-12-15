using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace VirtualKeyboard;

/// <summary>
/// Helper class for adding press animations to keyboard buttons
/// </summary>
public static class ButtonAnimationHelper
{
    private const double PRESS_SCALE = 0.90;
    private const double NORMAL_SCALE = 1.0;
    private const int ANIMATION_DURATION_MS = 80;

    /// <summary>
    /// Setup press animation for a button
    /// </summary>
    public static void SetupPressAnimation(Button button)
    {
        if (button == null) return;

        // Ensure button has a ScaleTransform
        if (button.RenderTransform == null || button.RenderTransform is not ScaleTransform)
        {
            button.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            button.RenderTransform = new ScaleTransform
            {
                ScaleX = NORMAL_SCALE,
                ScaleY = NORMAL_SCALE
            };
        }

        // CRITICAL: Use AddHandler with handledEventsToo = true to ensure we catch events
        // even if they were marked as handled by other controls
        button.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(Button_PointerPressed),
            handledEventsToo: true);
        
        button.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(Button_PointerReleased),
            handledEventsToo: true);
        
        button.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(Button_PointerCanceled),
            handledEventsToo: true);
        
        button.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(Button_PointerCaptureLost),
            handledEventsToo: true);
        
        button.AddHandler(
            UIElement.PointerExitedEvent,
            new PointerEventHandler(Button_PointerExited),
            handledEventsToo: true);
        
        Logger.Debug($"Animation setup for button: {button.Content}");
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
            Logger.Debug($"Button pressed: {button.Content}");
            AnimateScale(button, PRESS_SCALE);
            // Don't mark as handled - let other handlers process it too
        }
    }

    private static void Button_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            Logger.Debug($"Button released: {button.Content}");
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

    private static void Button_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            AnimateScale(button, NORMAL_SCALE);
        }
    }

    /// <summary>
    /// Animate button scale using Storyboard
    /// </summary>
    private static void AnimateScale(Button button, double targetScale)
    {
        try
        {
            if (button.RenderTransform is not ScaleTransform scaleTransform)
            {
                Logger.Warning("Button doesn't have ScaleTransform");
                return;
            }

            var storyboard = new Storyboard();

            // Scale X animation
            var scaleXAnimation = new DoubleAnimation
            {
                To = targetScale,
                Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleXAnimation, scaleTransform);
            Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");

            // Scale Y animation
            var scaleYAnimation = new DoubleAnimation
            {
                To = targetScale,
                Duration = new Duration(TimeSpan.FromMilliseconds(ANIMATION_DURATION_MS)),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scaleYAnimation, scaleTransform);
            Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");

            storyboard.Children.Add(scaleXAnimation);
            storyboard.Children.Add(scaleYAnimation);
            
            storyboard.Begin();
            
            Logger.Debug($"Animation started: scale to {targetScale}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Animation error: {ex.Message}", ex);
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
            if (button.RenderTransform is not ScaleTransform scaleTransform)
            {
                button.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                scaleTransform = new ScaleTransform();
                button.RenderTransform = scaleTransform;
            }

            var storyboard = new Storyboard();

            // Scale X animation with bounce
            var scaleXAnimation = new DoubleAnimationUsingKeyFrames();
            scaleXAnimation.KeyFrames.Add(new EasingDoubleKeyFrame 
            { 
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), 
                Value = 1.0 
            });
            scaleXAnimation.KeyFrames.Add(new EasingDoubleKeyFrame 
            { 
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), 
                Value = 1.2,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            scaleXAnimation.KeyFrames.Add(new EasingDoubleKeyFrame 
            { 
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), 
                Value = 1.0,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            });
            Storyboard.SetTarget(scaleXAnimation, scaleTransform);
            Storyboard.SetTargetProperty(scaleXAnimation, "ScaleX");

            // Scale Y animation with bounce
            var scaleYAnimation = new DoubleAnimationUsingKeyFrames();
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame 
            { 
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), 
                Value = 1.0 
            });
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame 
            { 
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), 
                Value = 1.2,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            });
            scaleYAnimation.KeyFrames.Add(new EasingDoubleKeyFrame 
            { 
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), 
                Value = 1.0,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            });
            Storyboard.SetTarget(scaleYAnimation, scaleTransform);
            Storyboard.SetTargetProperty(scaleYAnimation, "ScaleY");

            storyboard.Children.Add(scaleXAnimation);
            storyboard.Children.Add(scaleYAnimation);
            
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Bounce animation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleanup animation handlers
    /// </summary>
    public static void CleanupAnimations(Button button)
    {
        if (button == null) return;

        button.RemoveHandler(UIElement.PointerPressedEvent, (PointerEventHandler)Button_PointerPressed);
        button.RemoveHandler(UIElement.PointerReleasedEvent, (PointerEventHandler)Button_PointerReleased);
        button.RemoveHandler(UIElement.PointerCanceledEvent, (PointerEventHandler)Button_PointerCanceled);
        button.RemoveHandler(UIElement.PointerCaptureLostEvent, (PointerEventHandler)Button_PointerCaptureLost);
        button.RemoveHandler(UIElement.PointerExitedEvent, (PointerEventHandler)Button_PointerExited);
    }
}