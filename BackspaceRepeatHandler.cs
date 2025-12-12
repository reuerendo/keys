using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace VirtualKeyboard;

/// <summary>
/// Handles backspace key repeat functionality with initial delay and fast repeat
/// </summary>
public class BackspaceRepeatHandler : IDisposable
{
    private const int BACKSPACE_INITIAL_DELAY_MS = 500;
    private const int BACKSPACE_REPEAT_INTERVAL_MS = 50;

    private readonly KeyboardInputService _inputService;
    private readonly DispatcherTimer _repeatTimer;
    
    private bool _isBackspacePressed = false;
    private bool _backspaceInitialDelayPassed = false;

    public BackspaceRepeatHandler(KeyboardInputService inputService)
    {
        _inputService = inputService;
        
        _repeatTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(BACKSPACE_REPEAT_INTERVAL_MS)
        };
        _repeatTimer.Tick += RepeatTimer_Tick;
    }

    /// <summary>
    /// Setup backspace handlers for all backspace buttons in the UI tree
    /// </summary>
    public void SetupHandlers(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag is string tag && tag == "Backspace")
        {
            btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(BackspaceButton_PointerPressed), true);
            btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(BackspaceButton_PointerReleased), true);
            btn.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(BackspaceButton_PointerCanceled), true);
            btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(BackspaceButton_PointerCaptureLost), true);
            Logger.Debug("Backspace handlers attached");
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    SetupHandlers(fe);
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            SetupHandlers(scrollContent);
        }
    }

    private void RepeatTimer_Tick(object sender, object e)
    {
        // After initial delay, switch to fast repeat interval
        if (!_backspaceInitialDelayPassed)
        {
            _backspaceInitialDelayPassed = true;
            _repeatTimer.Interval = TimeSpan.FromMilliseconds(BACKSPACE_REPEAT_INTERVAL_MS);
            Logger.Debug($"Switched to fast repeat interval: {BACKSPACE_REPEAT_INTERVAL_MS}ms");
        }
        
        if (_isBackspacePressed)
        {
            byte backspaceVk = _inputService.GetVirtualKeyCode("Backspace");
            _inputService.SendVirtualKey(backspaceVk);
        }
    }

    private void BackspaceButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isBackspacePressed = true;
        _backspaceInitialDelayPassed = false;
        
        // Send first backspace immediately
        byte backspaceVk = _inputService.GetVirtualKeyCode("Backspace");
        _inputService.SendVirtualKey(backspaceVk);
        
        // Start timer with initial delay
        _repeatTimer.Interval = TimeSpan.FromMilliseconds(BACKSPACE_INITIAL_DELAY_MS);
        _repeatTimer.Start();
        
        Logger.Debug($"Backspace pressed - initial delay: {BACKSPACE_INITIAL_DELAY_MS}ms");
    }

    private void BackspaceButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopRepeat();
    }

    private void BackspaceButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        StopRepeat();
    }

    private void BackspaceButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        StopRepeat();
    }

    private void StopRepeat()
    {
        _isBackspacePressed = false;
        _backspaceInitialDelayPassed = false;
        _repeatTimer.Stop();
        
        Logger.Debug("Backspace released - stopping repeat");
    }

    public void Dispose()
    {
        _repeatTimer?.Stop();
    }
}