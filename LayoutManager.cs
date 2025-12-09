using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VirtualKeyboard;

/// <summary>
/// Manages keyboard layouts and visual updates
/// </summary>
public class LayoutManager
{
    private readonly KeyboardLayout _englishLayout;
    private readonly KeyboardLayout _russianLayout;
    private readonly KeyboardLayout _symbolLayout;
    
    private KeyboardLayout _currentLayout;
    private KeyboardLayout _previousLayout;
    private bool _isSymbolMode;

    private Button _langButton;

    public KeyboardLayout CurrentLayout => _currentLayout;
    public bool IsSymbolMode => _isSymbolMode;

    public LayoutManager()
    {
        _englishLayout = KeyboardLayout.CreateEnglishLayout();
        _russianLayout = KeyboardLayout.CreateRussianLayout();
        _symbolLayout = KeyboardLayout.CreateSymbolLayout();
        _currentLayout = _englishLayout;
    }

    /// <summary>
    /// Switch between English and Russian layouts
    /// </summary>
    public void SwitchLanguage()
    {
        if (_isSymbolMode)
        {
            return; // Don't switch language in symbol mode
        }
        
        _currentLayout = (_currentLayout == _englishLayout) ? _russianLayout : _englishLayout;
        Logger.Info($"Switched to layout: {_currentLayout.Name}");
    }

    /// <summary>
    /// Toggle symbol mode
    /// </summary>
    public void ToggleSymbolMode()
    {
        _isSymbolMode = !_isSymbolMode;
        
        if (_isSymbolMode)
        {
            // Remember current layout before switching to symbols
            _previousLayout = _currentLayout;
            _currentLayout = _symbolLayout;
        }
        else
        {
            // Restore previous layout when leaving symbol mode
            _currentLayout = _previousLayout ?? _englishLayout;
        }
        
        Logger.Info($"Symbol mode: {_isSymbolMode}, Layout: {_currentLayout.Name}");
    }

    /// <summary>
    /// Check if key exists in current layout
    /// </summary>
    public bool IsLayoutKey(string key)
    {
        return _currentLayout.Keys.ContainsKey(key);
    }

    /// <summary>
    /// Get key definition from current layout
    /// </summary>
    public KeyboardLayout.KeyDefinition GetKeyDefinition(string key)
    {
        return _currentLayout.Keys.ContainsKey(key) ? _currentLayout.Keys[key] : null;
    }

    /// <summary>
    /// Update all key labels on the keyboard
    /// </summary>
    public void UpdateKeyLabels(FrameworkElement rootElement, KeyboardStateManager stateManager)
    {
        UpdateButtonLabelsRecursive(rootElement, stateManager);
        UpdateLangButtonLabel();
    }

    /// <summary>
    /// Update button labels recursively
    /// </summary>
    private void UpdateButtonLabelsRecursive(FrameworkElement element, KeyboardStateManager stateManager)
    {
        if (element is Button btn && btn.Tag is string tag)
        {
            // Skip control keys
            if (tag == "Shift" || tag == "Lang" || tag == "&.." || 
                tag == "Esc" || tag == "Tab" || tag == "Caps" || 
                tag == "Ctrl" || tag == "Alt" || tag == "Enter" || 
                tag == "Backspace" || tag == " ")
            {
                // Don't update control keys except Lang button (handled separately)
            }
            else if (_currentLayout.Keys.ContainsKey(tag))
            {
                var keyDef = _currentLayout.Keys[tag];
                // Apply shift OR caps lock for letters
                bool shouldCapitalize = (stateManager.IsShiftActive || stateManager.IsCapsLockActive) && keyDef.IsLetter;
                // For Shift with Caps Lock, they cancel each other out
                if (stateManager.IsShiftActive && stateManager.IsCapsLockActive && keyDef.IsLetter)
                {
                    shouldCapitalize = false;
                }
                // For non-letters, only shift affects display
                bool useShift = stateManager.IsShiftActive && !keyDef.IsLetter;
                
                btn.Content = (shouldCapitalize || useShift) ? keyDef.DisplayShift : keyDef.Display;
            }
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    UpdateButtonLabelsRecursive(fe, stateManager);
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            UpdateButtonLabelsRecursive(scrollContent, stateManager);
        }
    }

    /// <summary>
    /// Update Lang button label
    /// </summary>
    private void UpdateLangButtonLabel()
    {
        if (_langButton == null)
        {
            // Lazy initialization will happen on first call
            return;
        }
        
        _langButton.Content = _isSymbolMode ? "abc" : "Lang";
    }

    /// <summary>
    /// Initialize Lang button reference
    /// </summary>
    public void InitializeLangButton(FrameworkElement rootElement)
    {
        FindLangButton(rootElement);
    }

    /// <summary>
    /// Find Lang button in UI tree
    /// </summary>
    private void FindLangButton(FrameworkElement element)
    {
        if (_langButton != null) return;

        if (element is Button btn && btn.Tag as string == "Lang")
        {
            _langButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindLangButton(fe);
                if (_langButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindLangButton(scrollContent);
        }
    }
}