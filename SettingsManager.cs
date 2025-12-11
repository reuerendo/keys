using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VirtualKeyboard;

/// <summary>
/// JSON serialization context for settings (trim-safe)
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SettingsManager.AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Manages application settings persistence
/// </summary>
public class SettingsManager
{
    private const string SETTINGS_FILENAME = "settings.json";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VirtualKeyboard",
        SETTINGS_FILENAME
    );

    public class AppSettings
    {
        public double KeyboardScale { get; set; } = 1.0; // Default 100%
        public bool AutoShowKeyboard { get; set; } = false; // Auto-show on text input focus
    }

    private AppSettings _settings;

    public AppSettings Settings => _settings;

    public SettingsManager()
    {
        LoadSettings();
    }

    /// <summary>
    /// Load settings from file or create defaults
    /// </summary>
    public void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                _settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                Logger.Info($"Settings loaded. Scale: {_settings.KeyboardScale:P0}, AutoShow: {_settings.AutoShowKeyboard}");
            }
            else
            {
                Logger.Info("No settings file found, using defaults");
                _settings = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings, using defaults", ex);
            _settings = new AppSettings();
        }
    }

    /// <summary>
    /// Save current settings to file
    /// </summary>
    public void SaveSettings()
    {
        try
        {
            string directory = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(_settings, SettingsJsonContext.Default.AppSettings);
            File.WriteAllText(SettingsPath, json);
            
            Logger.Info($"Settings saved. Scale: {_settings.KeyboardScale:P0}, AutoShow: {_settings.AutoShowKeyboard}");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save settings", ex);
        }
    }

    /// <summary>
    /// Update keyboard scale setting
    /// </summary>
    public void SetKeyboardScale(double scale)
    {
        // ИСПРАВЛЕНО: Пределы изменены с 0.5-1.5 на 0.8-1.2
        _settings.KeyboardScale = Math.Clamp(scale, 0.8, 1.2);
        SaveSettings();
    }

    /// <summary>
    /// Get keyboard scale as percentage (80-120)
    /// </summary>
    public int GetKeyboardScalePercent()
    {
        return (int)(_settings.KeyboardScale * 100);
    }

    /// <summary>
    /// Set keyboard scale from percentage (80-120)
    /// </summary>
    public void SetKeyboardScalePercent(int percent)
    {
        SetKeyboardScale(percent / 100.0);
    }

    /// <summary>
    /// Set auto-show keyboard setting
    /// </summary>
    public void SetAutoShowKeyboard(bool enabled)
    {
        _settings.AutoShowKeyboard = enabled;
        SaveSettings();
    }

    /// <summary>
    /// Get auto-show keyboard setting
    /// </summary>
    public bool GetAutoShowKeyboard()
    {
        return _settings.AutoShowKeyboard;
    }
}