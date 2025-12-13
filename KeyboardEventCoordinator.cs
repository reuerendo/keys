using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace VirtualKeyboard;

/// <summary>
/// Coordinates all keyboard button events, long press handling, and key sending
/// </summary>
public class KeyboardEventCoordinator
{
    private readonly KeyboardInputService _inputService;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly LongPressPopup _longPressPopup;
    private readonly FocusTracker _focusTracker;
    
    private bool _isLongPressHandled = false;

    public KeyboardEventCoordinator(
        KeyboardInputService inputService,
        KeyboardStateManager stateManager,
        LayoutManager layoutManager,
        LongPressPopup longPressPopup,
        FocusTracker focusTracker)
    {
        _inputService = inputService;
        _stateManager = stateManager;
        _layoutManager = layoutManager;
        _longPressPopup = longPressPopup;
        _focusTracker = focusTracker;
        
        _longPressPopup.CharacterSelected += LongPressPopup_CharacterSelected;
    }

    /// <summary>
    /// Setup long press handlers for all character keys in the UI tree
    /// </summary>
    public void SetupLongPressHandlers(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag is string tag)
        {
            // Skip control keys
            if (tag != "Shift" && tag != "Lang" && tag != "&.." && 
                tag != "Esc" && tag != "Tab" && tag != "Caps" && 
                tag != "Ctrl" && tag != "Alt" && tag != "Enter" && 
                tag != "Backspace" && tag != " ")
            {
                btn.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(KeyButton_PointerPressed), true);
                btn.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(KeyButton_PointerReleased), true);
                btn.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(KeyButton_PointerCanceled), true);
                btn.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(KeyButton_PointerCaptureLost), true);
            }
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    SetupLongPressHandlers(fe);
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            SetupLongPressHandlers(scrollContent);
        }
    }

    /// <summary>
    /// Handle key button click event
    /// </summary>
    public void HandleKeyButtonClick(string keyCode, FrameworkElement rootElement)
    {
        if (_isLongPressHandled)
        {
            _isLongPressHandled = false;
            Logger.Debug("Skipping click - long press was handled");
            return;
        }

        _longPressPopup?.HidePopup();

        switch (keyCode)
        {
            case "Shift":
                _stateManager.ToggleShift();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                break;
                
            case "Caps":
                _stateManager.ToggleCapsLock();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                break;
                
            case "Ctrl":
                _stateManager.ToggleCtrl();
                break;
                
            case "Alt":
                _stateManager.ToggleAlt();
                break;
                
            case "Lang":
                _layoutManager.SwitchLanguage();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            case "&..":
                _layoutManager.ToggleSymbolMode();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            case "Backspace":
                // Handled by BackspaceRepeatHandler
                break;
                
            default:
                SendKey(keyCode);
                
                if (_stateManager.IsShiftActive && _layoutManager.IsLayoutKey(keyCode))
                {
                    _stateManager.ToggleShift();
                    _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                }
                
                _stateManager.ResetCtrlIfActive();
                _stateManager.ResetAltIfActive();
                break;
        }
    }

    private void KeyButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isLongPressHandled = false;
        
        if (sender is Button btn)
        {
            Logger.Debug($"PointerPressed on button: {btn.Tag}");
            _longPressPopup?.StartPress(btn, _layoutManager.CurrentLayout.Name);
        }
    }

    private void KeyButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerReleased on button: {(sender as Button)?.Tag}");
        
        if (_longPressPopup != null)
        {
            _isLongPressHandled = false;
        }
        
        _longPressPopup?.CancelPress();
    }

    private void KeyButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCanceled on button: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    private void KeyButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCaptureLost on button: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    private void LongPressPopup_CharacterSelected(object sender, string character)
    {
        Logger.Info($"Long-press character selected: '{character}'");
        _isLongPressHandled = true;
        
        // ✅ FIX: Restore focus before sending long-press character
        RestoreFocusToTrackedWindow();
        
        foreach (char c in character)
        {
            _inputService.SendUnicodeChar(c);
        }
    }

    /// <summary>
    /// Restore focus to the tracked window before sending input
    /// This prevents keyboard from stealing focus when user clicks buttons
    /// </summary>
    private void RestoreFocusToTrackedWindow()
    {
        if (_focusTracker == null)
            return;

        IntPtr trackedWindow = _focusTracker.GetLastFocusedWindow();
        
        if (trackedWindow == IntPtr.Zero)
        {
            Logger.Debug("No tracked window to restore focus to");
            return;
        }

        bool restored = FocusHelper.RestoreForegroundWindow(trackedWindow);
        
        if (restored)
        {
            Logger.Debug($"Focus restored to 0x{trackedWindow:X} before sending input");
        }
        else
        {
            Logger.Warning($"Failed to restore focus to 0x{trackedWindow:X} before sending input");
        }
    }

    private void SendKey(string key)
    {
        // ✅ FIX: Restore focus to tracked window BEFORE checking current foreground
        // This ensures we send input to the correct window
        RestoreFocusToTrackedWindow();

        // Small delay to let focus restoration settle
        System.Threading.Thread.Sleep(5);

        IntPtr currentForeground = _inputService.GetForegroundWindowHandle();
        string currentTitle = _inputService.GetWindowTitle(currentForeground);

        Logger.Info($"Clicking '{key}'. Target Window: 0x{currentForeground:X} ({currentTitle}). Modifiers: Ctrl={_stateManager.IsCtrlActive}, Alt={_stateManager.IsAltActive}, Shift={_stateManager.IsShiftActive}, Caps={_stateManager.IsCapsLockActive}");

        if (_inputService.IsKeyboardWindowFocused())
        {
            Logger.Warning("CRITICAL: Keyboard has focus! Keys will not be sent to target app. WS_EX_NOACTIVATE failed.");
        }

        byte controlVk = _inputService.GetVirtualKeyCode(key);
        if (controlVk != 0)
        {
            _inputService.SendVirtualKey(controlVk);
            return;
        }

        var keyDef = _layoutManager.GetKeyDefinition(key);
        if (keyDef != null)
        {
            if (_stateManager.IsCtrlActive || _stateManager.IsAltActive)
            {
                byte vk = _inputService.GetVirtualKeyCodeForLayoutKey(key);
                if (vk != 0)
                {
                    _inputService.SendVirtualKey(vk, skipModifiers: true);
                }
                else
                {
                    Logger.Warning($"No VK code found for '{key}' - shortcuts may not work");
                }
            }
            else
            {
                bool shouldCapitalize = false;
                
                if (keyDef.IsLetter)
                {
                    shouldCapitalize = (_stateManager.IsShiftActive || _stateManager.IsCapsLockActive);
                    
                    if (_stateManager.IsShiftActive && _stateManager.IsCapsLockActive)
                    {
                        shouldCapitalize = false;
                    }
                }
                
                string charToSend = shouldCapitalize ? keyDef.ValueShift : keyDef.Value;
                
                Logger.Debug($"Key '{key}': Value={keyDef.Value}, isLetter={keyDef.IsLetter}, shouldCapitalize={shouldCapitalize}, sending='{charToSend}'");
                
                foreach (char c in charToSend)
                {
                    _inputService.SendUnicodeChar(c);
                }
            }
        }
        else
        {
            if (key.Length == 1 && !char.IsControl(key[0]))
            {
                _inputService.SendUnicodeChar(key[0]);
            }
        }
    }
}