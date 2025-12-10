using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace VirtualKeyboard;

/// <summary>
/// Manages long-press popup panels with additional characters
/// </summary>
public class LongPressPopup
{
    private const int LONG_PRESS_DELAY_MS = 500;
    
    private Popup _popup;
    private StackPanel _popupPanel;
    private DispatcherTimer _longPressTimer;
    private Button _currentButton;
    private FrameworkElement _rootElement;
    
    public event EventHandler<string> CharacterSelected;
    public bool IsPopupOpen => _popup?.IsOpen ?? false;

    public LongPressPopup(FrameworkElement rootElement)
    {
        _rootElement = rootElement;
        InitializePopup();
        InitializeLongPressTimer();
        
        Logger.Info("LongPressPopup initialized");
    }

    private void InitializePopup()
    {
        _popupPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4)
        };

        _popup = new Popup
        {
            Child = _popupPanel,
            IsLightDismissEnabled = true
        };

        _popup.Closed += (s, e) =>
        {
            Logger.Debug("Popup closed event fired");
            HidePopup();
        };
        
        Logger.Debug("Popup UI initialized");
    }

    private void InitializeLongPressTimer()
    {
        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LONG_PRESS_DELAY_MS)
        };
        _longPressTimer.Tick += LongPressTimer_Tick;
        
        Logger.Debug($"Long press timer initialized with {LONG_PRESS_DELAY_MS}ms delay");
    }

    public void StartPress(Button button, string layoutName)
    {
        _currentButton = button;
        string keyTag = button.Tag as string;
        
        Logger.Debug($"StartPress called for key: {keyTag}, layout: {layoutName}");
        
        if (string.IsNullOrEmpty(keyTag))
        {
            Logger.Warning("StartPress: keyTag is null or empty");
            return;
        }

        var options = GetLongPressOptions(keyTag, layoutName);
        if (options != null && options.Count > 0)
        {
            Logger.Info($"Starting long-press timer for '{keyTag}' - {options.Count} options available");
            _longPressTimer.Start();
        }
        else
        {
            Logger.Debug($"No long-press options for '{keyTag}' in layout '{layoutName}'");
        }
    }

    public void CancelPress()
    {
        if (_longPressTimer.IsEnabled)
        {
            Logger.Debug("CancelPress: stopping timer");
            _longPressTimer.Stop();
        }
        _currentButton = null;
    }

    public void HidePopup()
    {
        if (_popup.IsOpen)
        {
            Logger.Debug("Hiding popup");
            _popup.IsOpen = false;
            _popupPanel.Children.Clear();
        }
    }

    private void LongPressTimer_Tick(object sender, object e)
    {
        _longPressTimer.Stop();
        
        Logger.Info("Long press timer TICK - showing popup");
        
        if (_currentButton == null)
        {
            Logger.Warning("Timer tick but _currentButton is null");
            return;
        }

        string keyTag = _currentButton.Tag as string;
        string layoutName = GetCurrentLayoutName();
        
        Logger.Debug($"Timer tick: keyTag={keyTag}, layout={layoutName}");
        
        ShowPopup(_currentButton, keyTag, layoutName);
    }

    private void ShowPopup(Button sourceButton, string keyTag, string layoutName)
    {
        var options = GetLongPressOptions(keyTag, layoutName);
        if (options == null || options.Count == 0)
        {
            Logger.Warning($"ShowPopup: no options for '{keyTag}'");
            return;
        }

        Logger.Info($"Showing popup for '{keyTag}' with {options.Count} options");

        _popupPanel.Children.Clear();

        foreach (var option in options)
        {
            var btn = new Button
            {
                Content = option.Display,
                Tag = option.Value,
                Width = 48,
                Height = 48,
                FontSize = 18
            };

            btn.Click += PopupButton_Click;
            _popupPanel.Children.Add(btn);
            
            Logger.Debug($"Added popup button: {option.Display}");
        }

        // КРИТИЧЕСКИ ВАЖНО: Установить XamlRoot для popup
        if (_rootElement?.XamlRoot != null)
        {
            _popup.XamlRoot = _rootElement.XamlRoot;
            Logger.Debug("XamlRoot set for popup");
        }
        else
        {
            Logger.Error("XamlRoot is null - popup will not show!");
            return;
        }

        try
        {
            var transform = sourceButton.TransformToVisual(_rootElement);
            var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            _popup.HorizontalOffset = point.X;
            _popup.VerticalOffset = point.Y - 60;

            _popup.IsOpen = true;
            
            Logger.Info($"Popup opened at position X={point.X}, Y={point.Y - 60}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to position popup: {ex.Message}");
        }
    }

    private void PopupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string value)
        {
            Logger.Info($"Popup button clicked: {value}");
            CharacterSelected?.Invoke(this, value);
            HidePopup();
        }
    }

    private string _currentLayoutName = "English";

    public void SetCurrentLayout(string layoutName)
    {
        _currentLayoutName = layoutName;
        Logger.Info($"Long-press layout changed to: {layoutName}");
    }

    private string GetCurrentLayoutName()
    {
        return _currentLayoutName;
    }

    private List<LongPressOption> GetLongPressOptions(string keyTag, string layoutName)
    {
        var optionsMap = new Dictionary<string, Dictionary<string, List<LongPressOption>>>
        {
            ["English"] = new Dictionary<string, List<LongPressOption>>
            {
                ["."] = new List<LongPressOption>
                {
                    new LongPressOption("…", "…"),
                    new LongPressOption("·", "·")
                },
                ["a"] = new List<LongPressOption>
                {
                    new LongPressOption("à", "à"),
                    new LongPressOption("á", "á"),
                    new LongPressOption("â", "â"),
                    new LongPressOption("ä", "ä"),
                    new LongPressOption("å", "å"),
                    new LongPressOption("ã", "ã")
                },
                ["e"] = new List<LongPressOption>
                {
                    new LongPressOption("è", "è"),
                    new LongPressOption("é", "é"),
                    new LongPressOption("ê", "ê"),
                    new LongPressOption("ë", "ë")
                },
                ["i"] = new List<LongPressOption>
                {
                    new LongPressOption("ì", "ì"),
                    new LongPressOption("í", "í"),
                    new LongPressOption("î", "î"),
                    new LongPressOption("ï", "ï")
                },
                ["o"] = new List<LongPressOption>
                {
                    new LongPressOption("ò", "ò"),
                    new LongPressOption("ó", "ó"),
                    new LongPressOption("ô", "ô"),
                    new LongPressOption("ö", "ö"),
                    new LongPressOption("õ", "õ")
                },
                ["u"] = new List<LongPressOption>
                {
                    new LongPressOption("ù", "ù"),
                    new LongPressOption("ú", "ú"),
                    new LongPressOption("û", "û"),
                    new LongPressOption("ü", "ü")
                },
                ["n"] = new List<LongPressOption>
                {
                    new LongPressOption("ñ", "ñ")
                },
                ["c"] = new List<LongPressOption>
                {
                    new LongPressOption("ç", "ç")
                },
                ["-"] = new List<LongPressOption>
                {
                    new LongPressOption("–", "–"),
                    new LongPressOption("—", "—")
                }
            },
            
            ["Russian"] = new Dictionary<string, List<LongPressOption>>
            {
                ["."] = new List<LongPressOption>
                {
                    new LongPressOption("…", "…"),
                    new LongPressOption("·", "·")
                },
                ["q"] = new List<LongPressOption>
                {
                    new LongPressOption("ё", "ё")
                },
                ["-"] = new List<LongPressOption>
                {
                    new LongPressOption("–", "–"),
                    new LongPressOption("—", "—")
                }
            },
            
            ["Symbols"] = new Dictionary<string, List<LongPressOption>>
            {
                ["."] = new List<LongPressOption>
                {
                    new LongPressOption("…", "…"),
                    new LongPressOption("·", "·"),
                    new LongPressOption("•", "•")
                },
                ["-"] = new List<LongPressOption>
                {
                    new LongPressOption("–", "–"),
                    new LongPressOption("—", "—"),
                    new LongPressOption("−", "−")
                }
            }
        };

        if (optionsMap.ContainsKey(layoutName) && 
            optionsMap[layoutName].ContainsKey(keyTag))
        {
            var result = optionsMap[layoutName][keyTag];
            Logger.Debug($"Found {result.Count} long-press options for '{keyTag}' in '{layoutName}'");
            return result;
        }

        Logger.Debug($"No long-press options for '{keyTag}' in '{layoutName}'");
        return null;
    }

    public class LongPressOption
    {
        public string Display { get; set; }
        public string Value { get; set; }

        public LongPressOption(string display, string value)
        {
            Display = display;
            Value = value;
        }
    }
}