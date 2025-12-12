using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;

namespace VirtualKeyboard;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsManager _settingsManager;
    private int _originalScale;
    private bool _originalAutoShow;
    private List<string> _originalLayouts;
    
    private bool _hasScaleChanges = false;
    private bool _hasAutoShowChanges = false;
    private bool _hasLayoutChanges = false;

    private Dictionary<string, CheckBox> _layoutCheckBoxes = new Dictionary<string, CheckBox>();

    public bool RequiresRestart => _hasScaleChanges;
    public bool RequiresAutoShowUpdate => _hasAutoShowChanges;
    public bool RequiresLayoutUpdate => _hasLayoutChanges;

    public SettingsDialog(SettingsManager settingsManager)
    {
        this.InitializeComponent();
        _settingsManager = settingsManager;
        
        // Load current settings
        _originalScale = _settingsManager.GetKeyboardScalePercent();
        _originalAutoShow = _settingsManager.GetAutoShowKeyboard();
        _originalLayouts = new List<string>(_settingsManager.GetEnabledLayouts());
        
        ScaleSlider.Value = _originalScale;
        UpdateScaleText(_originalScale);
        
        AutoShowCheckBox.IsChecked = _originalAutoShow;
        
        InitializeLayoutCheckBoxes();
        
        Logger.Info($"Settings dialog opened. Scale: {_originalScale}%, AutoShow: {_originalAutoShow}, Layouts: {string.Join(", ", _originalLayouts)}");
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
        }
        
        // Check if layouts changed
        var currentLayouts = GetSelectedLayouts();
        _hasLayoutChanges = !currentLayouts.SequenceEqual(_originalLayouts);
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
        bool newAutoShow = AutoShowCheckBox.IsChecked ?? false;
        var newLayouts = GetSelectedLayouts();
        
        // Save scale setting
        if (newScale != _originalScale)
        {
            _settingsManager.SetKeyboardScalePercent(newScale);
            _hasScaleChanges = true;
            Logger.Info($"Scale changed from {_originalScale}% to {newScale}%");
        }
        
        // Save auto-show setting
        if (newAutoShow != _originalAutoShow)
        {
            _settingsManager.SetAutoShowKeyboard(newAutoShow);
            _hasAutoShowChanges = true;
            Logger.Info($"AutoShow changed from {_originalAutoShow} to {newAutoShow}");
        }
        
        // Save layouts setting
        if (!newLayouts.SequenceEqual(_originalLayouts))
        {
            _settingsManager.SetEnabledLayouts(newLayouts);
            _hasLayoutChanges = true;
            Logger.Info($"Layouts changed from [{string.Join(", ", _originalLayouts)}] to [{string.Join(", ", newLayouts)}]");
        }
        
        if (!_hasScaleChanges && !_hasAutoShowChanges && !_hasLayoutChanges)
        {
            Logger.Info("Settings dialog closed. No changes made.");
        }
    }

    private void CancelButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Logger.Info("Settings dialog cancelled");
        _hasScaleChanges = false;
        _hasAutoShowChanges = false;
        _hasLayoutChanges = false;
    }
}