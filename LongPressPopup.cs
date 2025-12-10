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
    private const int LONG_PRESS_DELAY_MS = 300;
    private const double POPUP_MARGIN = 8; // Margin from window edges
    
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

        // Set XamlRoot for popup
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
            // Calculate popup dimensions
            _popupPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double popupWidth = _popupPanel.DesiredSize.Width;
            double popupHeight = _popupPanel.DesiredSize.Height;
            
            // Get button position relative to root element
            var transform = sourceButton.TransformToVisual(_rootElement);
            var buttonPosition = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            
            // Get root element (window) dimensions
            double windowWidth = _rootElement.ActualWidth;
            double windowHeight = _rootElement.ActualHeight;
            
            Logger.Debug($"Window size: {windowWidth}x{windowHeight}, Button pos: {buttonPosition.X},{buttonPosition.Y}, Popup size: {popupWidth}x{popupHeight}");
            
            // Calculate horizontal position
            double horizontalOffset = buttonPosition.X;
            
            // Check if popup goes beyond right edge
            if (horizontalOffset + popupWidth > windowWidth - POPUP_MARGIN)
            {
                // Align to right edge of window
                horizontalOffset = windowWidth - popupWidth - POPUP_MARGIN;
                Logger.Debug($"Popup adjusted to right edge: {horizontalOffset}");
            }
            
            // Check if popup goes beyond left edge
            if (horizontalOffset < POPUP_MARGIN)
            {
                horizontalOffset = POPUP_MARGIN;
                Logger.Debug($"Popup adjusted to left edge: {horizontalOffset}");
            }
            
            // Calculate vertical position - try to show above button first
            double verticalOffset = buttonPosition.Y - popupHeight - 8; // 8px gap above button
            
            // Check if popup goes beyond top edge
            if (verticalOffset < POPUP_MARGIN)
            {
                // Show below button instead
                verticalOffset = buttonPosition.Y + sourceButton.ActualHeight + 8; // 8px gap below button
                Logger.Debug($"Popup positioned below button: {verticalOffset}");
                
                // Check if it goes beyond bottom edge
                if (verticalOffset + popupHeight > windowHeight - POPUP_MARGIN)
                {
                    // Position at bottom edge
                    verticalOffset = windowHeight - popupHeight - POPUP_MARGIN;
                    Logger.Debug($"Popup adjusted to bottom edge: {verticalOffset}");
                }
            }
            
            _popup.HorizontalOffset = horizontalOffset;
            _popup.VerticalOffset = verticalOffset;

            _popup.IsOpen = true;
            
            Logger.Info($"Popup opened at position X={horizontalOffset}, Y={verticalOffset}");
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
                ["1"] = new List<LongPressOption>
                {
                    new LongPressOption("¹", "¹"),
                    new LongPressOption("₁", "₁"),
					new LongPressOption("½", "½"),
					new LongPressOption("⅓", "⅓"),
					new LongPressOption("¼", "¼"),
					new LongPressOption("⅟", "⅟")
                },
				["2"] = new List<LongPressOption>
                {
                    new LongPressOption("²", "²"),
                    new LongPressOption("₂", "₂"),
					new LongPressOption("⅔", "⅔")
                },
				["3"] = new List<LongPressOption>
                {
                    new LongPressOption("³", "³"),
                    new LongPressOption("₃", "₃"),
					new LongPressOption("¾", "¾")
                },
				["4"] = new List<LongPressOption>
                {
                    new LongPressOption("⁴", "⁴"),
                    new LongPressOption("₄", "₄")
                },
				["5"] = new List<LongPressOption>
                {
                    new LongPressOption("⁵", "⁵"),
                    new LongPressOption("₅", "₅")
                },
				["6"] = new List<LongPressOption>
                {
                    new LongPressOption("⁶", "⁶"),
                    new LongPressOption("₆", "₆")
                },
				["7"] = new List<LongPressOption>
                {
                    new LongPressOption("⁷", "⁷"),
                    new LongPressOption("₇", "₇")
                },
				["8"] = new List<LongPressOption>
                {
                    new LongPressOption("⁸", "⁸"),
                    new LongPressOption("₈", "₈")
                },
				["9"] = new List<LongPressOption>
                {
                    new LongPressOption("⁹", "⁹"),
                    new LongPressOption("₉", "₉")
                },
				["0"] = new List<LongPressOption>
                {
                    new LongPressOption("⁰", "⁰"),
                    new LongPressOption("₀", "₀"),
					new LongPressOption("°", "°")
                },
                ["-"] = new List<LongPressOption>
                {
                    new LongPressOption("–", "–"),
                    new LongPressOption("—", "—")
                },
				["+"] = new List<LongPressOption>
                {
                    new LongPressOption("±", "±"),
                    new LongPressOption("∓", "∓")
                },
				["="] = new List<LongPressOption>
                {
                    new LongPressOption("≡", "≡"),
                    new LongPressOption("≠", "≠"),
					new LongPressOption("≈", "≈"),
					new LongPressOption("≉", "≉"),
					new LongPressOption("¬", "¬")
                },
				["/"] = new List<LongPressOption>
                {
                    new LongPressOption("÷", "÷"),
					new LongPressOption("⁄", "⁄")
                },
				["*"] = new List<LongPressOption>
                {
                    new LongPressOption("×", "×")
                },
                ["e"] = new List<LongPressOption>
                {
                    new LongPressOption("ę", "ę")
                },
                ["o"] = new List<LongPressOption>
                {
                    new LongPressOption("ó", "ó")
                },
                ["a"] = new List<LongPressOption>
                {
                    new LongPressOption("ą", "ą"),
                    new LongPressOption("@", "@"),
					new LongPressOption("ª", "ª")
                },
                ["s"] = new List<LongPressOption>
                {
                    new LongPressOption("ś", "ś")
                },
                ["l"] = new List<LongPressOption>
                {
                    new LongPressOption("ł", "ł")
                },
                ["z"] = new List<LongPressOption>
                {
                    new LongPressOption("ż", "ż"),
                    new LongPressOption("ź", "ź")
                },
                ["c"] = new List<LongPressOption>
                {
                    new LongPressOption("ć", "ć")
                },
                ["n"] = new List<LongPressOption>
                {
                    new LongPressOption("ń", "ń")
                },
				["."] = new List<LongPressOption>
                {
                    new LongPressOption("…", "…"),
                    new LongPressOption("·", "·")
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
                }
            },
            
            ["Russian"] = new Dictionary<string, List<LongPressOption>>
            {
                ["1"] = new List<LongPressOption>
                {
                    new LongPressOption("¹", "¹"),
                    new LongPressOption("₁", "₁"),
					new LongPressOption("½", "½"),
					new LongPressOption("⅓", "⅓"),
					new LongPressOption("¼", "¼"),
					new LongPressOption("⅟", "⅟")
                },
				["2"] = new List<LongPressOption>
                {
                    new LongPressOption("²", "²"),
                    new LongPressOption("₂", "₂"),
					new LongPressOption("⅔", "⅔")
                },
				["3"] = new List<LongPressOption>
                {
                    new LongPressOption("³", "³"),
                    new LongPressOption("₃", "₃"),
					new LongPressOption("¾", "¾")
                },
				["4"] = new List<LongPressOption>
                {
                    new LongPressOption("⁴", "⁴"),
                    new LongPressOption("₄", "₄")
                },
				["5"] = new List<LongPressOption>
                {
                    new LongPressOption("⁵", "⁵"),
                    new LongPressOption("₅", "₅")
                },
				["6"] = new List<LongPressOption>
                {
                    new LongPressOption("⁶", "⁶"),
                    new LongPressOption("₆", "₆")
                },
				["7"] = new List<LongPressOption>
                {
                    new LongPressOption("⁷", "⁷"),
                    new LongPressOption("₇", "₇")
                },
				["8"] = new List<LongPressOption>
                {
                    new LongPressOption("⁸", "⁸"),
                    new LongPressOption("₈", "₈")
                },
				["9"] = new List<LongPressOption>
                {
                    new LongPressOption("⁹", "⁹"),
                    new LongPressOption("₉", "₉")
                },
				["0"] = new List<LongPressOption>
                {
                    new LongPressOption("⁰", "⁰"),
                    new LongPressOption("₀", "₀"),
					new LongPressOption("°", "°")
                },
                ["-"] = new List<LongPressOption>
                {
                    new LongPressOption("–", "–"),
                    new LongPressOption("—", "—")
                },
				["+"] = new List<LongPressOption>
                {
                    new LongPressOption("±", "±"),
                    new LongPressOption("∓", "∓")
                },
				["="] = new List<LongPressOption>
                {
                    new LongPressOption("≡", "≡"),
                    new LongPressOption("≠", "≠"),
					new LongPressOption("≈", "≈"),
					new LongPressOption("≉", "≉"),
					new LongPressOption("¬", "¬")
                },
				["/"] = new List<LongPressOption>
                {
                    new LongPressOption("÷", "÷"),
					new LongPressOption("⁄", "⁄")
                },
				["*"] = new List<LongPressOption>
                {
                    new LongPressOption("×", "×")
                },
				[","] = new List<LongPressOption>
                {
                    new LongPressOption(";", ";")
                },
				["."] = new List<LongPressOption>
                {
                    new LongPressOption(":", ":"),
					new LongPressOption("…", "…"),
                    new LongPressOption("·", "·")
                },
                ["t"] = new List<LongPressOption>
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
                    new LongPressOption("∓", "∓")
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