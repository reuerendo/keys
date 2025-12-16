using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace VirtualKeyboard;

/// <summary>
/// Coordinates all keyboard button events, long press handling, and key sending
/// Simplified version - no focus tracking or restoration
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
                // In symbol mode, both Lang and &.. buttons exit symbol mode
                if (_layoutManager.IsSymbolMode)
                {
                    _layoutManager.ToggleSymbolMode();
                    _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                    _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                }
                else
                {
                    // In letter mode, Lang switches language
                    _layoutManager.SwitchLanguage();
                    _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                    _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                }
                break;
                
            case "&..":
                // In symbol mode, both Lang and &.. buttons exit symbol mode
                if (_layoutManager.IsSymbolMode)
                {
                    _layoutManager.ToggleSymbolMode();
                    _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                    _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                }
                else
                {
                    // In letter mode, &.. enters symbol mode
                    _layoutManager.ToggleSymbolMode();
                    _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
                    _longPressPopup?.SetCurrentLayout(_layoutManager.CurrentLayout.Name);
                }
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
        
        // Send character directly - SendInput automatically goes to foreground window
        foreach (char c in character)
        {
            _inputService.SendUnicodeChar(c);
        }
    }

    /// <summary>
    /// Send key to foreground application
    /// </summary>
    private void SendKey(string key)
    {
        IntPtr currentForeground = _inputService.GetForegroundWindowHandle();
        string currentTitle = _inputService.GetWindowTitle(currentForeground);

        Logger.Info($"Sending '{key}' to foreground window: 0x{currentForeground:X} ({currentTitle})");

        // Check for control keys (Esc, Tab, etc.)
        byte controlVk = _inputService.GetVirtualKeyCode(key);
        if (controlVk != 0)
        {
            _inputService.SendVirtualKey(controlVk);
            return;
        }

        // Get key definition from layout
        var keyDef = _layoutManager.GetKeyDefinition(key);
        if (keyDef != null)
        {
            // Handle shortcuts (Ctrl+X, Alt+X)
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
                // Handle regular character input
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
                
                Logger.Debug($"Key '{key}': sending '{charToSend}' (capitalized={shouldCapitalize})");
                
                // Send Unicode character(s)
                foreach (char c in charToSend)
                {
                    _inputService.SendUnicodeChar(c);
                }
            }
        }
        else
        {
            // Fallback: send single character
            if (key.Length == 1 && !char.IsControl(key[0]))
            {
                _inputService.SendUnicodeChar(key[0]);
            }
        }
    }
}