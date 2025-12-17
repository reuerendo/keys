using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VirtualKeyboard;

/// <summary>
/// Manages settings dialog display and application restart logic
/// </summary>
public class SettingsDialogManager
{
    private readonly Window _window;
    private readonly SettingsManager _settingsManager;
    private readonly LayoutManager _layoutManager;
    private readonly KeyboardStateManager _stateManager;
    private readonly WindowVisibilityManager _visibilityManager;

    public SettingsDialogManager(
        Window window,
        SettingsManager settingsManager,
        LayoutManager layoutManager,
        KeyboardStateManager stateManager,
        WindowVisibilityManager visibilityManager)
    {
        _window = window;
        _settingsManager = settingsManager;
        _layoutManager = layoutManager;
        _stateManager = stateManager;
        _visibilityManager = visibilityManager;
    }

    /// <summary>
    /// Show settings dialog and handle changes
    /// </summary>
    public async void ShowSettingsDialog()
    {
        try
        {
            _visibilityManager?.Show(preserveFocus: false);
            
            var dialog = new SettingsDialog(_settingsManager)
            {
                XamlRoot = _window.Content.XamlRoot
            };
            
            await dialog.ShowAsync();
            
            // Handle settings changes
            HandleSettingsChanges(dialog);
            
            Logger.Info("Settings dialog closed");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to show settings dialog", ex);
        }
    }

    /// <summary>
    /// Handle settings changes after dialog closes
    /// </summary>
    private async void HandleSettingsChanges(SettingsDialog dialog)
    {
        // Update layouts if changed (includes default layout)
        if (dialog.RequiresLayoutUpdate)
        {
            _layoutManager.RefreshAvailableLayouts();
            var rootElement = _window.Content as FrameworkElement;
            _layoutManager.UpdateKeyLabels(rootElement, _stateManager);
            Logger.Info("Keyboard layouts refreshed and default layout applied");
        }
        
        // Update auto-show if changed
        if (dialog.RequiresAutoShowUpdate)
        {
            _visibilityManager.UpdateAutoShowSetting();
            Logger.Info("Auto-show setting updated");
        }
        
        // Handle restart if scale changed
        if (dialog.RequiresRestart)
        {
            await ShowRestartDialog();
        }
    }

    /// <summary>
    /// Show restart confirmation dialog
    /// </summary>
    private async System.Threading.Tasks.Task ShowRestartDialog()
    {
        var restartDialog = new ContentDialog
        {
            Title = "Требуется перезапуск",
            Content = "Для применения изменений размера клавиатуры необходимо перезапустить приложение.\n\nПерезапустить сейчас?",
            PrimaryButtonText = "Перезапустить",
            CloseButtonText = "Позже",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _window.Content.XamlRoot
        };
        
        var result = await restartDialog.ShowAsync();
        
        if (result == ContentDialogResult.Primary)
        {
            RestartApplication();
        }
    }

    /// <summary>
    /// Restart application
    /// </summary>
    private void RestartApplication()
    {
        try
        {
            Logger.Info("Restarting application...");
            
            string executablePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(executablePath))
            {
                System.Diagnostics.Process.Start(executablePath);
                Application.Current.Exit();
            }
            else
            {
                Logger.Error("Could not determine executable path for restart");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to restart application", ex);
        }
    }
}