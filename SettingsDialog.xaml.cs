using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VirtualKeyboard;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly SettingsManager _settingsManager;
    private int _originalScale;
    private bool _hasChanges = false;

    public bool RequiresRestart => _hasChanges;

    public SettingsDialog(SettingsManager settingsManager)
    {
        this.InitializeComponent();
        _settingsManager = settingsManager;
        
        // Load current settings
        _originalScale = _settingsManager.GetKeyboardScalePercent();
        ScaleSlider.Value = _originalScale;
        UpdateScaleText(_originalScale);
        
        Logger.Info($"Settings dialog opened. Current scale: {_originalScale}%");
    }

    private void ScaleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        int newValue = (int)e.NewValue;
        UpdateScaleText(newValue);
        
        // Check if value changed from original
        if (newValue != _originalScale)
        {
            _hasChanges = true;
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
        
        if (newScale != _originalScale)
        {
            _settingsManager.SetKeyboardScalePercent(newScale);
            Logger.Info($"Settings saved. Scale changed from {_originalScale}% to {newScale}%");
        }
        else
        {
            Logger.Info("Settings dialog closed. No changes made.");
        }
    }

    private void CancelButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Logger.Info("Settings dialog cancelled");
        _hasChanges = false;
    }
}