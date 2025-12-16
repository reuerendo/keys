using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Manages keyboard layouts and visual updates
/// </summary>
public class LayoutManager
{
    private readonly KeyboardLayout _englishLayout;
    private readonly KeyboardLayout _russianLayout;
    private readonly KeyboardLayout _polishLayout;
    private readonly KeyboardLayout _symbolLayout;
    
    private List<KeyboardLayout> _availableLayouts;
    private int _currentLayoutIndex;
    private KeyboardLayout _previousLayout;
    private bool _isSymbolMode;

    private Button _langButton;
    private Button _symbolButton;
    private readonly SettingsManager _settingsManager;

    public KeyboardLayout CurrentLayout => _isSymbolMode ? _symbolLayout : _availableLayouts[_currentLayoutIndex];
    public bool IsSymbolMode => _isSymbolMode;

    public LayoutManager(SettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        
        _englishLayout = KeyboardLayout.CreateEnglishLayout();
        _russianLayout = KeyboardLayout.CreateRussianLayout();
        _polishLayout = KeyboardLayout.CreatePolishLayout();
        _symbolLayout = KeyboardLayout.CreateSymbolLayout();
        
        RefreshAvailableLayouts();
        SetDefaultLayout();
    }

    /// <summary>
    /// Set current layout to default layout from settings
    /// </summary>
    public void SetDefaultLayout()
    {
        string defaultLayoutCode = _settingsManager.GetDefaultLayout();
        
        // Find the index of the default layout
        for (int i = 0; i < _availableLayouts.Count; i++)
        {
            if (_availableLayouts[i].Code == defaultLayoutCode)
            {
                _currentLayoutIndex = i;
                Logger.Info($"Default layout set to: {_availableLayouts[i].Name} ({defaultLayoutCode})");
                return;
            }
        }
        
        // If default layout not found in available layouts, use first one
        _currentLayoutIndex = 0;
        Logger.Warning($"Default layout {defaultLayoutCode} not found in available layouts, using {_availableLayouts[0].Name}");
    }

    /// <summary>
    /// Refresh available layouts based on settings
    /// </summary>
    public void RefreshAvailableLayouts()
    {
        var enabledLayouts = _settingsManager.GetEnabledLayouts();
        _availableLayouts = new List<KeyboardLayout>();

        if (enabledLayouts.Contains("EN"))
            _availableLayouts.Add(_englishLayout);
        if (enabledLayouts.Contains("RU"))
            _availableLayouts.Add(_russianLayout);
        if (enabledLayouts.Contains("PL"))
            _availableLayouts.Add(_polishLayout);

        // Ensure at least one layout is available
        if (_availableLayouts.Count == 0)
        {
            _availableLayouts.Add(_englishLayout);
            Logger.Warning("No layouts enabled, defaulting to English");
        }

        // Reset index if current is out of bounds
        if (_currentLayoutIndex >= _availableLayouts.Count)
        {
            _currentLayoutIndex = 0;
        }

        Logger.Info($"Available layouts refreshed: {string.Join(", ", _availableLayouts.Select(l => l.Code))}");
        
        // Reapply default layout after refresh
        SetDefaultLayout();
    }

    /// <summary>
    /// Switch to next available layout
    /// </summary>
    public void SwitchLanguage()
    {
        if (_isSymbolMode)
        {
            return; // Don't switch language in symbol mode
        }

        if (_availableLayouts.Count > 1)
        {
            _currentLayoutIndex = (_currentLayoutIndex + 1) % _availableLayouts.Count;
            Logger.Info($"Switched to layout: {CurrentLayout.Name} ({CurrentLayout.Code})");
        }
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
            _previousLayout = _availableLayouts[_currentLayoutIndex];
        }
        else
        {
            // Restore previous layout when leaving symbol mode
            if (_previousLayout != null && _availableLayouts.Contains(_previousLayout))
            {
                _currentLayoutIndex = _availableLayouts.IndexOf(_previousLayout);
            }
        }
        
        Logger.Info($"Symbol mode: {_isSymbolMode}, Layout: {CurrentLayout.Name}");
    }

    /// <summary>
    /// Check if key exists in current layout
    /// </summary>
    public bool IsLayoutKey(string key)
    {
        return CurrentLayout.Keys.ContainsKey(key);
    }

    /// <summary>
    /// Get key definition from current layout
    /// </summary>
    public KeyboardLayout.KeyDefinition GetKeyDefinition(string key)
    {
        return CurrentLayout.Keys.ContainsKey(key) ? CurrentLayout.Keys[key] : null;
    }

    /// <summary>
    /// Update all key labels on the keyboard
    /// </summary>
    public void UpdateKeyLabels(FrameworkElement rootElement, KeyboardStateManager stateManager)
    {
        UpdateButtonLabelsRecursive(rootElement, stateManager);
        UpdateLangButtonLabel();
        UpdateSymbolButtonLabel();
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
                // Don't update control keys except Lang and &.. buttons (handled separately)
            }
            else if (CurrentLayout.Keys.ContainsKey(tag))
            {
                var keyDef = CurrentLayout.Keys[tag];
                
                // Shift should ONLY affect letters
                bool shouldCapitalize = false;
                
                if (keyDef.IsLetter)
                {
                    // For letters: apply Shift OR Caps Lock
                    shouldCapitalize = (stateManager.IsShiftActive || stateManager.IsCapsLockActive);
                    
                    // If both Shift and Caps Lock are active, they cancel each other out
                    if (stateManager.IsShiftActive && stateManager.IsCapsLockActive)
                    {
                        shouldCapitalize = false;
                    }
                }
                
                // For all other keys (numbers, symbols, etc.): ignore Shift completely
                btn.Content = shouldCapitalize ? keyDef.DisplayShift : keyDef.Display;
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
    /// Update Lang button label with current layout code or icon
    /// </summary>
    private void UpdateLangButtonLabel()
    {
        if (_langButton == null)
        {
            // Lazy initialization will happen on first call
            return;
        }
        
        if (_isSymbolMode)
        {
            // In symbol mode, show icon to exit symbol mode
            var fontIcon = new FontIcon
            {
                Glyph = "\uE8D3",
                FontSize = 20
            };
            _langButton.Content = fontIcon;
        }
        else
        {
            // In letter mode, show current layout code
            _langButton.Content = CurrentLayout.Code;
        }
    }

    /// <summary>
    /// Update &.. button label with icon
    /// </summary>
    private void UpdateSymbolButtonLabel()
    {
        if (_symbolButton == null)
        {
            // Lazy initialization will happen on first call
            return;
        }
        
        if (_isSymbolMode)
        {
            // In symbol mode, show icon to exit symbol mode
            var fontIcon = new FontIcon
            {
                Glyph = "\uE8D3",
                FontSize = 20
            };
            _symbolButton.Content = fontIcon;
        }
        else
        {
            // In letter mode, show symbols icon
            var fontIcon = new FontIcon
            {
                Glyph = "\uED58",
                FontSize = 20
            };
            _symbolButton.Content = fontIcon;
        }
    }

    /// <summary>
    /// Initialize Lang button reference
    /// </summary>
    public void InitializeLangButton(FrameworkElement rootElement)
    {
        FindLangButton(rootElement);
        FindSymbolButton(rootElement);
        UpdateLangButtonLabel();
        UpdateSymbolButtonLabel();
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

    /// <summary>
    /// Find &.. button in UI tree
    /// </summary>
    private void FindSymbolButton(FrameworkElement element)
    {
        if (_symbolButton != null) return;

        if (element is Button btn && btn.Tag as string == "&..")
        {
            _symbolButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindSymbolButton(fe);
                if (_symbolButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindSymbolButton(scrollContent);
        }
    }
}