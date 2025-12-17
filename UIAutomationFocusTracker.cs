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

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    #endregion

    private UIA3Automation _automation;
    private IDisposable _focusHandler;
    private IntPtr _mouseHook;
    private LowLevelMouseProc _mouseHookDelegate;
    private DateTime _lastMouseClick = DateTime.MinValue;
    private POINT _lastClickPosition;
    private readonly IntPtr _keyboardWindowHandle;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;
    private bool _isInitialized = false;

    // Timeout: показывать клавиатуру только если фокус изменился быстро после клика
    private readonly TimeSpan _clickTimeout = TimeSpan.FromMilliseconds(200);

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

            // Install global mouse hook to track clicks
            _mouseHookDelegate = MouseHookCallback;
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookDelegate, 
                    GetModuleHandle(curModule.ModuleName), 0);
            }

            if (_mouseHook != IntPtr.Zero)
            {
                Logger.Info("✅ Mouse click tracking enabled");
            }
            else
            {
                Logger.Warning("Failed to install mouse hook - auto-show will trigger on any focus change");
            }
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
    /// Mouse hook callback - tracks mouse clicks with coordinates
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
            {
                // Get click coordinates
                MSLLHOOKSTRUCT hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                _lastClickPosition = hookStruct.pt;
                _lastMouseClick = DateTime.UtcNow;
                
                Logger.Debug($"Mouse click at ({_lastClickPosition.X}, {_lastClickPosition.Y}) at {_lastMouseClick:HH:mm:ss.fff}");
            }
        }
        
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
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
                    // Check if focus changed due to user click
                    var timeSinceLastClick = DateTime.UtcNow - _lastMouseClick;
                    bool wasRecentClick = timeSinceLastClick <= _clickTimeout;

                    if (!wasRecentClick)
                    {
                        Logger.Debug($"⏭️ Skipping auto-show: Focus changed programmatically (no recent click, {timeSinceLastClick.TotalMilliseconds:F0}ms since last click)");
                        return;
                    }

                    // Check if click was inside element boundaries
                    try
                    {
                        var boundingRect = sender.Properties.BoundingRectangle.ValueOrDefault;
                        bool clickInsideBounds = 
                            _lastClickPosition.X >= boundingRect.Left &&
                            _lastClickPosition.X <= boundingRect.Right &&
                            _lastClickPosition.Y >= boundingRect.Top &&
                            _lastClickPosition.Y <= boundingRect.Bottom;

                        if (!clickInsideBounds)
                        {
                            Logger.Debug($"⏭️ Skipping auto-show: Click at ({_lastClickPosition.X},{_lastClickPosition.Y}) was outside element bounds ({boundingRect.Left},{boundingRect.Top})-({boundingRect.Right},{boundingRect.Bottom})");
                            return;
                        }

                        Logger.Debug($"🖱️ Click inside element bounds - triggering auto-show");
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not get element bounds: {ex.Message} - allowing auto-show");
                    }

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

            // Remove mouse hook
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
                Logger.Info("Mouse hook removed");
            }

            _automation?.Dispose();
            _automation = null;
            _focusHandler = null;
            _mouseHookDelegate = null;
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