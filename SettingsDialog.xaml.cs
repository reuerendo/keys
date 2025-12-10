using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VirtualKeyboard;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsManager _settingsManager;
    private int _originalScale;
    private bool _originalAutoShow;
    private bool _hasScaleChanges = false;
    private bool _hasAutoShowChanges = false;

    public bool RequiresRestart => _hasScaleChanges;
    public bool RequiresAutoShowUpdate => _hasAutoShowChanges;

    public SettingsDialog(SettingsManager settingsManager)
    {
        this.InitializeComponent();
        _settingsManager = settingsManager;
        
        // Load current settings
        _originalScale = _settingsManager.GetKeyboardScalePercent();
        _originalAutoShow = _settingsManager.GetAutoShowKeyboard();
        
        ScaleSlider.Value = _originalScale;
        UpdateScaleText(_originalScale);
        
        AutoShowCheckBox.IsChecked = _originalAutoShow;
        
        Logger.Info($"Settings dialog opened. Current scale: {_originalScale}%, AutoShow: {_originalAutoShow}");
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
        
        // Save scale setting
        if (newScale != _originalScale)
        {
            _settingsManager.SetKeyboardScalePercent(newScale);
            Logger.Info($"Scale changed from {_originalScale}% to {newScale}%");
        }
        
        // Save auto-show setting
        if (newAutoShow != _originalAutoShow)
        {
            _settingsManager.SetAutoShowKeyboard(newAutoShow);
            _hasAutoShowChanges = true;
            Logger.Info($"AutoShow changed from {_originalAutoShow} to {newAutoShow}");
        }
        
        if (!_hasScaleChanges && !_hasAutoShowChanges)
        {
            Logger.Info("Settings dialog closed. No changes made.");
        }
    }

    private void CancelButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Logger.Info("Settings dialog cancelled");
        _hasScaleChanges = false;
        _hasAutoShowChanges = false;
    }
}