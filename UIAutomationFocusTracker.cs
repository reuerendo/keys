using System;
using System.Runtime.InteropServices;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace VirtualKeyboard;

/// <summary>
/// Tracks focus changes using FlaUI library (wrapper over UI Automation)
/// Now with mouse click detection to distinguish user clicks from programmatic focus changes
/// </summary>
public class UIAutomationFocusTracker : IDisposable
{
    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #endregion

    private UIA3Automation _automation;
    private IDisposable _focusHandler;
    private readonly IntPtr _keyboardWindowHandle;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;
    private bool _isInitialized = false;
    
    // Mouse click detector for filtering programmatic focus changes
    private MouseClickDetector _clickDetector;
    private bool _requireClickForAutoShow;

    public event EventHandler<TextInputFocusEventArgs> TextInputFocused;
    public event EventHandler<FocusEventArgs> NonTextInputFocused;

    public UIAutomationFocusTracker(IntPtr keyboardWindowHandle, bool requireClickForAutoShow = true)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _requireClickForAutoShow = requireClickForAutoShow;
        
        try
        {
            Logger.Info("Initializing UI Automation focus tracker with FlaUI...");
            
            if (_requireClickForAutoShow)
            {
                _clickDetector = new MouseClickDetector();
                Logger.Info("🖱️ Click detection enabled - auto-show only on user clicks");
            }
            else
            {
                Logger.Info("⚠️ Click detection disabled - auto-show on any focus change");
            }
            
            _automation = new UIA3Automation();
            
            if (_automation == null)
            {
                Logger.Error("Failed to create UIA3Automation instance");
                return;
            }

            _focusHandler = _automation.RegisterFocusChangedEvent(element =>
            {
                OnFocusChanged(element);
            });
            
            _isInitialized = true;
            Logger.Info("✅ UI Automation focus tracker initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation focus tracker", ex);
            _isInitialized = false;
        }
    }

    private void OnFocusChanged(AutomationElement sender)
    {
        if (sender == null || !_isInitialized)
            return;

        lock (_lockObject)
        {
            try
            {
                var hwnd = sender.Properties.NativeWindowHandle.ValueOrDefault;
                
                if (hwnd == _keyboardWindowHandle)
                    return;

                var controlType = sender.Properties.ControlType.ValueOrDefault;
                string className = sender.Properties.ClassName.ValueOrDefault ?? "";
                string name = sender.Properties.Name.ValueOrDefault ?? "";
                // Добавляем проверку на IsReadOnly, если свойство поддерживается, но это может быть медленно.
                // Лучше полагаться на типы.
                
                bool isKeyboardFocusable = sender.Properties.IsKeyboardFocusable.ValueOrDefault;
                
                uint processId = 0;
                if (hwnd != IntPtr.Zero)
                    GetWindowThreadProcessId(hwnd, out processId);

                int controlTypeId = (int)controlType;

                // Основное изменение здесь: более строгая проверка
                if (IsTextInputControl(controlType, className, isKeyboardFocusable))
                {
                    bool shouldTrigger = true;
                    
                    if (_requireClickForAutoShow && _clickDetector != null)
                    {
                        shouldTrigger = IsClickInitiatedFocus(sender);
                    }
                    
                    if (shouldTrigger)
                    {
                        // Проверяем еще раз IsPassword только если решили показать, чтобы не тормозить на каждом клике
                        bool isPassword = false;
                        try { isPassword = sender.Properties.IsPassword.ValueOrDefault; } catch {}

                        Logger.Info($"✅ Text input focused (user click) - Type: {controlType}, Class: '{className}', Name: '{name}'");
                        
                        var args = new TextInputFocusEventArgs
                        {
                            WindowHandle = hwnd,
                            ControlType = controlTypeId,
                            ClassName = className,
                            Name = name,
                            IsPassword = isPassword,
                            ProcessId = processId
                        };
                        
                        TextInputFocused?.Invoke(this, args);
                    }
                    else
                    {
                        Logger.Debug($"⏭️ Text input focused (programmatic/out-of-bounds) - IGNORED - Type: {controlType}, Class: '{className}'");
                    }
                }
                else
                {
                    var args = new FocusEventArgs
                    {
                        WindowHandle = hwnd,
                        ControlType = controlTypeId,
                        ClassName = className
                    };
                    
                    NonTextInputFocused?.Invoke(this, args);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error handling focus changed event", ex);
            }
        }
    }

    private bool IsClickInitiatedFocus(AutomationElement element)
    {
        try
        {
            if (!_clickDetector.WasRecentClick())
            {
                Logger.Debug("❌ No recent click detected");
                return false;
            }

            var boundingRectangle = element.Properties.BoundingRectangle.ValueOrDefault;
            if (boundingRectangle.IsEmpty) return true; 

            var bounds = new Rectangle(
                (int)boundingRectangle.X,
                (int)boundingRectangle.Y,
                (int)boundingRectangle.Width,
                (int)boundingRectangle.Height
            );

            bool wasClickInside = _clickDetector.WasRecentClickInBounds(bounds);
            
            if (wasClickInside)
                Logger.Debug($"✅ Click detected INSIDE bounds ({bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height})");
            else
                Logger.Debug($"❌ Click detected OUTSIDE bounds ({bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height})");

            return wasClickInside;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking click-initiated focus", ex);
            return true;
        }
    }

    /// <summary>
    /// Исправленная логика определения текстовых полей
    /// </summary>
    private bool IsTextInputControl(ControlType controlType, string className, bool isKeyboardFocusable)
    {
        if (!isKeyboardFocusable)
            return false;

        // 1. Стандартные поля ввода (TextBox, Edit)
        if (controlType == ControlType.Edit)
            return true;

        string classLower = className?.ToLowerInvariant() ?? "";

        // 2. ControlType.Document (Браузеры vs Word)
        // В браузерах "Document" - это вся страница. Мы НЕ хотим срабатывать на неё.
        // В Word "Document" - это область редактирования. Мы ХОТИМ срабатывать.
        if (controlType == ControlType.Document)
        {
            // Если класс пустой - это почти всегда веб-контент браузера (Firefox/Edge так делают)
            if (string.IsNullOrEmpty(classLower))
                return false;

            // Если класс явно браузерный - игнорируем Document (ждем клика именно в Edit внутри)
            if (classLower.Contains("chrome") || 
                classLower.Contains("mozilla") || 
                classLower.Contains("edge") ||
                classLower.Contains("webkit"))
            {
                return false;
            }

            // Для остальных (например, Word использует класс '_wwg' или 'opusapp') - разрешаем
            return true;
        }

        // 3. Специфичные классы окон, которые ведут себя как текстовые поля, но имеют странные типы
        if (classLower.Contains("edit") ||
            classLower.Contains("richedit") || // WordPad, некоторые редакторы
            classLower.Contains("scintilla") || // Notepad++
            classLower.Contains("cmd") || // Командная строка
            classLower == "consolewindowclass")
        {
            return true;
        }

        return false;
    }

    public void SetClickRequirement(bool requireClick)
    {
        lock (_lockObject)
        {
            if (_requireClickForAutoShow == requireClick) return;
            _requireClickForAutoShow = requireClick;

            if (requireClick && _clickDetector == null)
                _clickDetector = new MouseClickDetector();
            else if (!requireClick && _clickDetector != null)
            {
                _clickDetector.Dispose();
                _clickDetector = null;
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_isInitialized && _focusHandler != null) _focusHandler.Dispose();
            _clickDetector?.Dispose();
            _automation?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing UI Automation focus tracker", ex);
        }
        GC.SuppressFinalize(this);
    }

    ~UIAutomationFocusTracker() => Dispose();
}

// ... EventArgs classes останутся без изменений ...
public class TextInputFocusEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; set; }
    public int ControlType { get; set; }
    public string ClassName { get; set; }
    public string Name { get; set; }
    public bool IsPassword { get; set; }
    public uint ProcessId { get; set; }
}

public class FocusEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; set; }
    public int ControlType { get; set; }
    public string ClassName { get; set; }
}