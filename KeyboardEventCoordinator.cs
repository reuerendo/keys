using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace VirtualKeyboard;

/// <summary>
/// Coordinates keyboard button events, long press handling, and key sending
/// NO focus restoration - keyboard never steals focus
/// </summary>
public class KeyboardEventCoordinator
{
    private readonly KeyboardInputService _inputService;
    private readonly KeyboardStateManager _stateManager;
    private readonly LayoutManager _layoutManager;
    private readonly LongPressPopup _longPressPopup;
    
    private bool _isLongPressHandled = false;

    public KeyboardEventCoordinator(
        KeyboardInputService inputService,
        KeyboardStateManager stateManager,
        LayoutManager layoutManager,
        LongPressPopup longPressPopup)
    {
        _inputService = inputService;
        _stateManager = stateManager;
        _layoutManager = layoutManager;
        _longPressPopup = longPressPopup;
        
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
        Logger.Info($"┌─── KEY CLICK: '{keyCode}' ───");
        
        if (_isLongPressHandled)
        {
            _isLongPressHandled = false;
            Logger.Debug("│ Skipped: long press was handled");
            Logger.Info($"└─────────────────────────────────");
            return;
        }

        _longPressPopup?.HidePopup();

        switch (keyCode)
        {
            case "Shift":
                Logger.Debug("│ Action: Toggle Shift");
                _stateManager.ToggleShift();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                break;
                
            case "Caps":
                Logger.Debug("│ Action: Toggle Caps Lock");
                _stateManager.ToggleCapsLock();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                break;
                
            case "Ctrl":
                Logger.Debug("│ Action: Toggle Ctrl");
                _stateManager.ToggleCtrl();
                break;
                
            case "Alt":
                Logger.Debug("│ Action: Toggle Alt");
                _stateManager.ToggleAlt();
                break;
                
            case "Lang":
                Logger.Debug("│ Action: Switch Language");
                _layoutManager.SwitchLanguage();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            case "&..":
                Logger.Debug("│ Action: Toggle Symbol Mode");
                _layoutManager.ToggleSymbolMode();
                _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                break;
                
            case "Backspace":
                Logger.Debug("│ Action: Backspace (handled by repeat handler)");
                break;
                
            default:
                Logger.Debug($"│ Action: Send key '{keyCode}'");
                SendKey(keyCode);
                
                // Reset Shift after typing a letter (one-shot Shift)
                if (_stateManager.IsShiftActive && _layoutManager.IsLayoutKey(keyCode))
                {
                    Logger.Debug("│ Resetting Shift (one-shot)");
                    _stateManager.ToggleShift();
                    _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                }
                
                // Reset Ctrl/Alt after use
                _stateManager.ResetCtrlIfActive();
                _stateManager.ResetAltIfActive();
                break;
        }
        
        Logger.Info($"└─────────────────────────────────");
    }

    private void KeyButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isLongPressHandled = false;
        
        if (sender is Button btn)
        {
            Logger.Debug($"PointerPressed: {btn.Tag}");
            _longPressPopup?.StartPress(btn, _layoutManager.CurrentLayout.Name);
        }
    }

    private void KeyButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerReleased: {(sender as Button)?.Tag}");
        
        if (_longPressPopup != null)
        {
            _isLongPressHandled = false;
        }
        
        _longPressPopup?.CancelPress();
    }

    private void KeyButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCanceled: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    private void KeyButton_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        Logger.Debug($"PointerCaptureLost: {(sender as Button)?.Tag}");
        _longPressPopup?.CancelPress();
    }

    private void LongPressPopup_CharacterSelected(object sender, string character)
    {
        Logger.Info($"═══════════════════════════════════════════════════════");
        Logger.Info($"LONG-PRESS: Selected '{character}'");
        _isLongPressHandled = true;
        
        // ✅ NO focus restoration needed - keyboard never stole focus
        // Just send the character directly
        foreach (char c in character)
        {
            _inputService.SendUnicodeChar(c);
        }
        
        Logger.Info($"═══════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Send key to active application
    /// ✅ NO focus restoration - keyboard never steals focus
    /// </summary>
    private void SendKey(string key)
    {
        IntPtr currentForeground = _inputService.GetForegroundWindowHandle();
        string currentTitle = _inputService.GetWindowTitle(currentForeground);

        Logger.Info($"│ Target: 0x{currentForeground:X} ({currentTitle})");
        Logger.Info($"│ Modifiers: Ctrl={_stateManager.IsCtrlActive}, Alt={_stateManager.IsAltActive}, Shift={_stateManager.IsShiftActive}, Caps={_stateManager.IsCapsLockActive}");

        // ✅ Verify keyboard hasn't stolen focus (should never happen with WS_EX_NOACTIVATE)
        if (_inputService.IsKeyboardWindowFocused())
        {
            Logger.Error("│ ❌ CRITICAL: Keyboard has focus! WS_EX_NOACTIVATE failed!");
            Logger.Error("│    Keys will NOT be sent to target app.");
            Logger.Info($"└─────────────────────────────────");
            return;
        }

        // Handle control keys (arrows, etc)
        byte controlVk = _inputService.GetVirtualKeyCode(key);
        if (controlVk != 0)
        {
            Logger.Debug($"│ Sending VK: 0x{controlVk:X}");
            _inputService.SendVirtualKey(controlVk);
            return;
        }

        // Handle layout keys (letters, numbers)
        var keyDef = _layoutManager.GetKeyDefinition(key);
        if (keyDef != null)
        {
            // Handle shortcuts (Ctrl+X, Alt+X)
            if (_stateManager.IsCtrlActive || _stateManager.IsAltActive)
            {
                byte vk = _inputService.GetVirtualKeyCodeForLayoutKey(key);
                if (vk != 0)
                {
                    Logger.Debug($"│ Sending shortcut VK: 0x{vk:X}");
                    _inputService.SendVirtualKey(vk, skipModifiers: true);
                }
                else
                {
                    Logger.Warning($"│ ⚠ No VK code for '{key}' - shortcuts may not work");
                }
            }
            else
            {
                // Handle normal character input with Shift/Caps
                bool shouldCapitalize = false;
                
                if (keyDef.IsLetter)
                {
                    shouldCapitalize = (_stateManager.IsShiftActive || _stateManager.IsCapsLockActive);
                    
                    // XOR logic: Shift + Caps = lowercase
                    if (_stateManager.IsShiftActive && _stateManager.IsCapsLockActive)
                    {
                        shouldCapitalize = false;
                    }
                }
                
                string charToSend = shouldCapitalize ? keyDef.ValueShift : keyDef.Value;
                
                Logger.Debug($"│ Char: '{charToSend}' (isLetter={keyDef.IsLetter}, capitalize={shouldCapitalize})");
                
                foreach (char c in charToSend)
                {
                    _inputService.SendUnicodeChar(c);
                }
            }
        }
        else
        {
            // Fallback: send as-is
            if (key.Length == 1 && !char.IsControl(key[0]))
            {
                Logger.Debug($"│ Sending as Unicode: '{key}'");
                _inputService.SendUnicodeChar(key[0]);
            }
        }
    }
}