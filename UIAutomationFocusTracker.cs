using System;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace VirtualKeyboard;

/// <summary>
/// Tracks focus changes using FlaUI library (wrapper over UI Automation)
/// </summary>
public class UIAutomationFocusTracker : IDisposable
{
    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #endregion

    private UIA3Automation _automation;
    private IDisposable _focusHandler;  // FlaUI возвращает IDisposable
    private readonly IntPtr _keyboardWindowHandle;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;
    private bool _isInitialized = false;

    public event EventHandler<TextInputFocusEventArgs> TextInputFocused;
    public event EventHandler<FocusEventArgs> NonTextInputFocused;

    public UIAutomationFocusTracker(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        
        try
        {
            Logger.Info("Initializing UI Automation focus tracker with FlaUI...");
            
            // Create FlaUI UIA3 automation instance
            _automation = new UIA3Automation();
            
            if (_automation == null)
            {
                Logger.Error("Failed to create UIA3Automation instance");
                return;
            }

            Logger.Info("✅ FlaUI UIA3Automation instance created successfully");

            // Register focus changed event with lambda
            _focusHandler = _automation.RegisterFocusChangedEvent(element =>
            {
                OnFocusChanged(element);
            });
            
            _isInitialized = true;
            Logger.Info("✅ UI Automation focus tracker initialized successfully with FlaUI");
        }
        catch (COMException comEx)
        {
            Logger.Error($"COM error initializing FlaUI: 0x{comEx.HResult:X8} - {comEx.Message}", comEx);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation focus tracker", ex);
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Handle focus changed event
    /// </summary>
    private void OnFocusChanged(AutomationElement sender)
    {
        if (sender == null || !_isInitialized)
            return;

        lock (_lockObject)
        {
            try
            {
                // Get window handle
                var hwnd = sender.Properties.NativeWindowHandle.ValueOrDefault;
                
                // Ignore keyboard's own window
                if (hwnd == _keyboardWindowHandle)
                {
                    Logger.Debug("Focus on keyboard itself - ignoring");
                    return;
                }

                // Get properties
                var controlType = sender.Properties.ControlType.ValueOrDefault;
                string className = sender.Properties.ClassName.ValueOrDefault ?? "";
                string name = sender.Properties.Name.ValueOrDefault ?? "";
                bool isPassword = sender.Properties.IsPassword.ValueOrDefault;
                bool isKeyboardFocusable = sender.Properties.IsKeyboardFocusable.ValueOrDefault;
                
                uint processId = 0;
                try
                {
                    processId = (uint)sender.Properties.ProcessId.ValueOrDefault;
                }
                catch
                {
                    if (hwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(hwnd, out processId);
                    }
                }

                int controlTypeId = (int)controlType;
                Logger.Debug($"Focus changed - Type: {controlTypeId}, Class: '{className}', Name: '{name}', Password: {isPassword}, Focusable: {isKeyboardFocusable}, PID: {processId}");

                // Check if this is a text input control
                if (IsTextInputControl(controlType, className, isKeyboardFocusable))
                {
                    Logger.Info($"✅ Text input focused - Type: {controlType}, Class: '{className}', Name: '{name}', Password: {isPassword}");
                    
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
                    Logger.Debug($"Non-text input focused - Type: {controlType}");
                    
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

    /// <summary>
    /// Check if control is a text input control
    /// </summary>
    private bool IsTextInputControl(ControlType controlType, string className, bool isKeyboardFocusable)
    {
        if (!isKeyboardFocusable)
            return false;

        // Check FlaUI control types
        if (controlType == ControlType.Edit || controlType == ControlType.Document)
            return true;

        if (!string.IsNullOrEmpty(className))
        {
            string classLower = className.ToLowerInvariant();
            
            if (classLower.Contains("edit") ||
                classLower.Contains("textbox") ||
                classLower.Contains("richedit") ||
                classLower.Contains("scintilla") ||
                classLower == "chrome_rendererwidgethosthwnd" ||
                classLower == "intermediate d3d window")
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            if (_isInitialized && _focusHandler != null)
            {
                try
                {
                    // Dispose автоматически отписывает обработчик
                    _focusHandler.Dispose();
                    Logger.Info("Focus changed event handler removed");
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to remove focus changed event handler", ex);
                }
            }

            _automation?.Dispose();
            _automation = null;
            _focusHandler = null;
            _isInitialized = false;

            Logger.Info("UI Automation focus tracker disposed");
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing UI Automation focus tracker", ex);
        }

        GC.SuppressFinalize(this);
    }

    ~UIAutomationFocusTracker()
    {
        Dispose();
    }
}

#region Event Args Classes

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

#endregion