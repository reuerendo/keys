using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;

namespace VirtualKeyboard;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsManager _settingsManager;
    private int _originalScale;
    private List<string> _originalLayouts;
    private string _originalDefaultLayout;
    private bool _originalAutoShow;
    
    private bool _hasScaleChanges = false;
    private bool _hasLayoutChanges = false;
    private bool _hasDefaultLayoutChanges = false;
    private bool _hasAutoShowChanges = false;

    private Dictionary<string, CheckBox> _layoutCheckBoxes = new Dictionary<string, CheckBox>();

    public bool RequiresRestart => _hasScaleChanges;
    public bool RequiresLayoutUpdate => _hasLayoutChanges || _hasDefaultLayoutChanges;
    public bool RequiresAutoShowUpdate => _hasAutoShowChanges;

    public SettingsDialog(SettingsManager settingsManager)
    {
        this.InitializeComponent();
        _settingsManager = settingsManager;
        
        // Load current settings
        _originalScale = _settingsManager.GetKeyboardScalePercent();
        _originalLayouts = new List<string>(_settingsManager.GetEnabledLayouts());
        _originalDefaultLayout = _settingsManager.GetDefaultLayout();
        _originalAutoShow = _settingsManager.GetAutoShowOnTextInput();
        
        ScaleSlider.Value = _originalScale;
        UpdateScaleText(_originalScale);
        
        AutoShowToggle.IsOn = _originalAutoShow;
        
        InitializeLayoutCheckBoxes();
        InitializeDefaultLayoutComboBox();
        
        Logger.Info($"Settings dialog opened. Scale: {_originalScale}%, Layouts: {string.Join(", ", _originalLayouts)}, Default: {_originalDefaultLayout}, AutoShow: {_originalAutoShow}");
    }

    /// <summary>
    /// Initialize layout selection checkboxes
    /// </summary>
    private void InitializeLayoutCheckBoxes()
    {
        var layouts = new List<(string code, string name)>
        {
            ("EN", "English (EN)"),
            ("RU", "Русский (RU)"),
            ("PL", "Polski (PL)")
        };

        foreach (var (code, name) in layouts)
        {
            var checkBox = new CheckBox
            {
                Content = name,
                IsChecked = _originalLayouts.Contains(code),
                Tag = code
            };
            
            checkBox.Checked += LayoutCheckBox_Changed;
            checkBox.Unchecked += LayoutCheckBox_Changed;
            
            _layoutCheckBoxes[code] = checkBox;
            LayoutsPanel.Children.Add(checkBox);
        }
    }

    /// <summary>
    /// Initialize default layout ComboBox
    /// </summary>
    private void InitializeDefaultLayoutComboBox()
    {
        UpdateDefaultLayoutComboBox();
        
        // Select current default layout
        for (int i = 0; i < DefaultLayoutComboBox.Items.Count; i++)
        {
            if (DefaultLayoutComboBox.Items[i] is ComboBoxItem item && item.Tag as string == _originalDefaultLayout)
            {
                DefaultLayoutComboBox.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Update default layout ComboBox with enabled layouts
    /// </summary>
    private void UpdateDefaultLayoutComboBox()
    {
        var selectedTag = (DefaultLayoutComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        
        DefaultLayoutComboBox.Items.Clear();
        
        var layoutNames = new Dictionary<string, string>
        {
            { "EN", "English (EN)" },
            { "RU", "Русский (RU)" },
            { "PL", "Polski (PL)" }
        };
        
        var enabledLayouts = GetSelectedLayouts();
        int indexToSelect = 0;
        
        for (int i = 0; i < enabledLayouts.Count; i++)
        {
            string code = enabledLayouts[i];
            var item = new ComboBoxItem
            {
                Content = layoutNames[code],
                Tag = code
            };
            DefaultLayoutComboBox.Items.Add(item);
            
            // Remember index if this was previously selected
            if (code == selectedTag)
            {
                indexToSelect = i;
            }
        }
        
        // Select first item if ComboBox has items
        if (DefaultLayoutComboBox.Items.Count > 0)
        {
            DefaultLayoutComboBox.SelectedIndex = indexToSelect;
        }
    }

    /// <summary>
    /// Handle layout checkbox state change
    /// </summary>
    private void LayoutCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        // Ensure at least one layout is selected
        int checkedCount = _layoutCheckBoxes.Values.Count(cb => cb.IsChecked == true);
        
        if (checkedCount == 0)
        {
            // Prevent unchecking the last checkbox
            if (sender is CheckBox lastCheckBox)
            {
                lastCheckBox.IsChecked = true;
            }
            return;
        }
        
        // Check if layouts changed
        var currentLayouts = GetSelectedLayouts();
        _hasLayoutChanges = !currentLayouts.SequenceEqual(_originalLayouts);
        
        // Update default layout ComboBox
        UpdateDefaultLayoutComboBox();
    }

    /// <summary>
    /// Handle default layout ComboBox selection change
    /// </summary>
    private void DefaultLayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DefaultLayoutComboBox.SelectedItem is ComboBoxItem item && item.Tag is string selectedLayout)
        {
            _hasDefaultLayoutChanges = (selectedLayout != _originalDefaultLayout);
        }
    }

    /// <summary>
    /// Handle auto-show toggle changed
    /// </summary>
    private void AutoShowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        bool newValue = AutoShowToggle.IsOn;
        _hasAutoShowChanges = (newValue != _originalAutoShow);
        
        Logger.Debug($"Auto-show toggle changed: {newValue} (original: {_originalAutoShow}, changed: {_hasAutoShowChanges})");
    }

    /// <summary>
    /// Get list of selected layout codes
    /// </summary>
    private List<string> GetSelectedLayouts()
    {
        return _layoutCheckBoxes
            .Where(kvp => kvp.Value.IsChecked == true)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    private void ScaleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int newValue = (int)e.NewValue;
        UpdateScaleText(newValue);
        
        // Check if value changed from original
        if (newValue != _originalScale)
        {
            _hasScaleChanges = true;
        }
        else
        {
            _hasScaleChanges = false;
        }
    }

    private void UpdateScaleText(int value)
    {
        if (ScaleValueText != null)
        {
            ScaleValueText.Text = $"{value}%";
        }
    }

    private void SaveButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        int newScale = (int)ScaleSlider.Value;
        var newLayouts = GetSelectedLayouts();
        string newDefaultLayout = (DefaultLayoutComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? newLayouts[0];
        bool newAutoShow = AutoShowToggle.IsOn;
        
        // Save scale setting
        if (newScale != _originalScale)
        {
            _settingsManager.SetKeyboardScalePercent(newScale);
            _hasScaleChanges = true;
            Logger.Info($"Scale changed from {_originalScale}% to {newScale}%");
        }
        
        // Save layouts setting (must be done before default layout)
        if (!newLayouts.SequenceEqual(_originalLayouts))
        {
            _settingsManager.SetEnabledLayouts(newLayouts);
            _hasLayoutChanges = true;
            Logger.Info($"Layouts changed from [{string.Join(", ", _originalLayouts)}] to [{string.Join(", ", newLayouts)}]");
        }
        
        // Save default layout setting
        if (newDefaultLayout != _originalDefaultLayout)
        {
            _settingsManager.SetDefaultLayout(newDefaultLayout);
            _hasDefaultLayoutChanges = true;
            Logger.Info($"Default layout changed from {_originalDefaultLayout} to {newDefaultLayout}");
        }
        
        // Save auto-show setting
        if (newAutoShow != _originalAutoShow)
        {
            _settingsManager.SetAutoShowOnTextInput(newAutoShow);
            _hasAutoShowChanges = true;
            Logger.Info($"Auto-show changed from {_originalAutoShow} to {newAutoShow}");
        }
        
        if (!_hasScaleChanges && !_hasLayoutChanges && !_hasDefaultLayoutChanges && !_hasAutoShowChanges)
        {
            Logger.Info("Settings dialog closed. No changes made.");
        }
    }

    private void CancelButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Logger.Info("Settings dialog cancelled");
        _hasScaleChanges = false;
        _hasLayoutChanges = false;
        _hasDefaultLayoutChanges = false;
        _hasAutoShowChanges = false;
    }
}