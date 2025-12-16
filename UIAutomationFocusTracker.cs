using System;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient; 

namespace VirtualKeyboard;

/// <summary>
/// Tracks focus changes using OFFICIAL UI Automation interop (UIAutomationClient)
/// Safe for WinUI 3 unpackaged apps
/// </summary>
public sealed class UIAutomationFocusTracker : IDisposable
{
    // ИСПРАВЛЕНИЕ 2: Используем интерфейс IUIAutomation вместо конкретного класса CUIAutomation8 в поле
    private readonly IUIAutomation _automation;
    private readonly IUIAutomationFocusChangedEventHandler _handler;
    private readonly IntPtr _keyboardHwnd;
    private bool _disposed;

    public event EventHandler<TextInputFocusEventArgs>? TextInputFocused;
    public event EventHandler<FocusEventArgs>? NonTextInputFocused;

    public UIAutomationFocusTracker(IntPtr keyboardWindowHandle)
    {
        _keyboardHwnd = keyboardWindowHandle;

        try
        {
            Logger.Info("Initializing UI Automation focus tracker (official interop)...");

            // ИСПРАВЛЕНИЕ 3: В Interop сборках класс обычно называется CUIAutomation
            _automation = new CUIAutomation();
            _handler = new FocusChangedHandler(this);

            // null cacheRequest = live properties
            _automation.AddFocusChangedEventHandler(null, _handler);

            Logger.Info("✅ UI Automation focus tracker initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("❌ Failed to initialize UI Automation focus tracker", ex);
            throw;
        }
    }

    private void OnFocusChanged(IUIAutomationElement element)
    {
        if (_disposed || element == null)
            return;

        try
        {
            IntPtr hwnd = (IntPtr)element.CurrentNativeWindowHandle;
            if (hwnd == _keyboardHwnd)
                return;

            int controlType = element.CurrentControlType;
            string className = SafeGet(() => element.CurrentClassName);
            string name = SafeGet(() => element.CurrentName);
            bool isPassword = SafeGet(() => element.CurrentIsPassword) == 1;
            bool isKeyboardFocusable = SafeGet(() => element.CurrentIsKeyboardFocusable) == 1;
            uint processId = (uint)SafeGet(() => element.CurrentProcessId);

            Logger.Debug($"UIA Focus: Type={controlType}, Class='{className}'");

            if (IsTextInputControl(controlType, className, isKeyboardFocusable))
            {
                Logger.Info("✅ Text input focused — auto-show keyboard");

                TextInputFocused?.Invoke(this, new TextInputFocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = controlType,
                    ClassName = className,
                    Name = name,
                    IsPassword = isPassword,
                    ProcessId = processId
                });
            }
            else
            {
                NonTextInputFocused?.Invoke(this, new FocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = controlType,
                    ClassName = className
                });
            }
        }
        catch (COMException ex)
        {
            Logger.Debug($"UIA COM error: 0x{ex.HResult:X8}");
        }
        catch (Exception ex)
        {
            Logger.Debug($"UIA error: {ex.Message}");
        }
    }

    private static bool IsTextInputControl(int controlType, string className, bool isKeyboardFocusable)
    {
        if (!isKeyboardFocusable)
            return false;

        // UIA_EditControlTypeId / UIA_DocumentControlTypeId
        if (controlType == UIA_ControlTypeIds.UIA_EditControlTypeId ||
            controlType == UIA_ControlTypeIds.UIA_DocumentControlTypeId)
            return true;

        if (!string.IsNullOrEmpty(className))
        {
            string c = className.ToLowerInvariant();
            if (c.Contains("edit") || c.Contains("textbox") || c.Contains("richedit") ||
                c == "chrome_rendererwidgethosthwnd" || c.Contains("mozilla") || c.Contains("d3d"))
                return true;
        }

        return false;
    }

    private static T SafeGet<T>(Func<T> getter)
    {
        try { return getter(); }
        catch { return default!; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_automation != null && _handler != null)
                _automation.RemoveFocusChangedEventHandler(_handler);
        }
        catch { }
    }

    private sealed class FocusChangedHandler : IUIAutomationFocusChangedEventHandler
    {
        private readonly UIAutomationFocusTracker _parent;

        public FocusChangedHandler(UIAutomationFocusTracker parent)
        {
            _parent = parent;
        }

        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            _parent.OnFocusChanged(sender);
        }
    }
}

// ===== Event args =====

public class FocusEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; set; }
    public int ControlType { get; set; }
    public string ClassName { get; set; } = string.Empty;
}

public class TextInputFocusEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; set; }
    public int ControlType { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsPassword { get; set; }
    public uint ProcessId { get; set; }
}