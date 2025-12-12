using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public List<string> EnabledLayouts { get; set; } = new List<string> { "EN", "RU" }; // Default layouts
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
                
                // Ensure EnabledLayouts is not null and has at least one layout
                if (_settings.EnabledLayouts == null || _settings.EnabledLayouts.Count == 0)
                {
                    _settings.EnabledLayouts = new List<string> { "EN", "RU" };
                }
                
                Logger.Info($"Settings loaded. Scale: {_settings.KeyboardScale:P0}, AutoShow: {_settings.AutoShowKeyboard}, Layouts: {string.Join(", ", _settings.EnabledLayouts)}");
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
            
            Logger.Info($"Settings saved. Scale: {_settings.KeyboardScale:P0}, AutoShow: {_settings.AutoShowKeyboard}, Layouts: {string.Join(", ", _settings.EnabledLayouts)}");
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

    /// <summary>
    /// Get enabled keyboard layouts
    /// </summary>
    public List<string> GetEnabledLayouts()
    {
        if (_settings.EnabledLayouts == null || _settings.EnabledLayouts.Count == 0)
        {
            return new List<string> { "EN", "RU" };
        }
        return new List<string>(_settings.EnabledLayouts);
    }

    /// <summary>
    /// Set enabled keyboard layouts
    /// </summary>
    public void SetEnabledLayouts(List<string> layouts)
    {
        // Ensure at least one layout is enabled
        if (layouts == null || layouts.Count == 0)
        {
            layouts = new List<string> { "EN" };
        }

        _settings.EnabledLayouts = new List<string>(layouts);
        SaveSettings();
    }

    /// <summary>
    /// Check if a specific layout is enabled
    /// </summary>
    public bool IsLayoutEnabled(string layoutCode)
    {
        return _settings.EnabledLayouts != null && _settings.EnabledLayouts.Contains(layoutCode);
    }
}