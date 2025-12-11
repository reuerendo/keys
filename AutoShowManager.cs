using System;
using System.Runtime.InteropServices;
using System.Threading;
using UIAutomationClient;

namespace VirtualKeyboard;

/// <summary>
/// Monitors focus events and automatically shows keyboard when text input is focused
/// </summary>
public class AutoShowManager : IDisposable
{
    // Win32 event constants
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // UI Automation control type IDs for text inputs
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_TextControlTypeId = 50020;

    // UI Automation pattern IDs
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;

    // Cooldown to prevent keyboard from showing immediately after hide
    private const int HIDE_COOLDOWN_MS = 1000;

    // Win32 imports
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, 
        WinEventDelegate lpfnWinEventProc, uint idProcess, 
        uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private readonly IntPtr _keyboardWindowHandle;
    private IntPtr _hookHandle;
    private WinEventDelegate _hookDelegate;
    private CUIAutomation _automation;
    private bool _isEnabled;
    private DateTime _lastHideTime;
    private bool _isDisposed;

    public event EventHandler ShowKeyboardRequested;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled)
                {
                    StartHook();
                }
                else
                {
                    StopHook();
                }
                Logger.Info($"AutoShowManager IsEnabled set to: {value}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _lastHideTime = DateTime.MinValue;
        
        try
        {
            _automation = new CUIAutomation();
            Logger.Info("UI Automation initialized for AutoShowManager");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation", ex);
        }
    }

    /// <summary>
    /// Notify that keyboard was hidden (starts cooldown)
    /// </summary>
    public void NotifyKeyboardHidden()
    {
        _lastHideTime = DateTime.Now;
        Logger.Debug("Keyboard hide cooldown started");
    }

    private void StartHook()
    {
        if (_hookHandle != IntPtr.Zero || _automation == null)
            return;

        try
        {
            // Keep delegate reference to prevent GC
            _hookDelegate = new WinEventDelegate(WinEventCallback);
            
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
                IntPtr.Zero, _hookDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);

            if (_hookHandle != IntPtr.Zero)
            {
                Logger.Info("Focus event hook installed successfully");
            }
            else
            {
                Logger.Error("Failed to install focus event hook");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error installing focus event hook", ex);
        }
    }

    private void StopHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Logger.Info("Focus event hook removed");
        }
    }

    private void WinEventCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_isEnabled || _isDisposed)
            return;

        // Check cooldown
        if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
        {
            Logger.Debug("Focus event ignored - within cooldown period");
            return;
        }

        try
        {
            // Ignore if focus is on keyboard window
            if (hwnd == _keyboardWindowHandle)
            {
                Logger.Debug("Focus event ignored - keyboard window");
                return;
            }

            // Check if focused element is a text input
            if (IsTextInputElement(hwnd))
            {
                Logger.Info($"Text input focused in window 0x{hwnd:X}");
                OnShowKeyboardRequested();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error processing focus event", ex);
        }
    }

    private bool IsTextInputElement(IntPtr hwnd)
    {
        if (_automation == null || hwnd == IntPtr.Zero)
            return false;

        try
        {
            // Get focused element from UI Automation
            IUIAutomationElement focusedElement = _automation.GetFocusedElement();
            if (focusedElement == null)
                return false;

            // Check control type
            int controlType = focusedElement.CurrentControlType;
            
            if (controlType == UIA_EditControlTypeId ||
                controlType == UIA_DocumentControlTypeId)
            {
                Logger.Debug($"Text input detected - Control Type: {controlType}");
                return true;
            }

            // Check for Value pattern (editable text fields)
            object valuePatternObj = null;
            try
            {
                valuePatternObj = focusedElement.GetCurrentPattern(UIA_ValuePatternId);
                if (valuePatternObj != null)
                {
                    var valuePattern = (IUIAutomationValuePattern)valuePatternObj;
                    bool isReadOnly = valuePattern.CurrentIsReadOnly == 0 ? false : true;
                    
                    if (!isReadOnly)
                    {
                        Logger.Debug("Text input detected - Value Pattern (editable)");
                        return true;
                    }
                }
            }
            catch { }
            finally
            {
                if (valuePatternObj != null && Marshal.IsComObject(valuePatternObj))
                {
                    Marshal.ReleaseComObject(valuePatternObj);
                }
            }

            // Check for Text pattern (rich text controls)
            object textPatternObj = null;
            try
            {
                textPatternObj = focusedElement.GetCurrentPattern(UIA_TextPatternId);
                if (textPatternObj != null)
                {
                    Logger.Debug("Text input detected - Text Pattern");
                    return true;
                }
            }
            catch { }
            finally
            {
                if (textPatternObj != null && Marshal.IsComObject(textPatternObj))
                {
                    Marshal.ReleaseComObject(textPatternObj);
                }
            }

            // Additional check: common text input class names
            if (IsCommonTextInputClassName(hwnd))
            {
                Logger.Debug("Text input detected - Common class name");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking if element is text input", ex);
            return false;
        }
    }

    private bool IsCommonTextInputClassName(IntPtr hwnd)
    {
        try
        {
            var className = new System.Text.StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            string classNameStr = className.ToString();

            // Common text input class names
            string[] textInputClasses = new[]
            {
                "Edit",
                "RichEdit",
                "RichEdit20",
                "RICHEDIT50W",
                "TextBox",
                "Internet Explorer_Server", // Web browser text fields
                "Chrome_RenderWidgetHostHWND", // Chrome text fields
                "MozillaWindowClass" // Firefox text fields
            };

            foreach (var textClass in textInputClasses)
            {
                if (classNameStr.IndexOf(textClass, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking class name", ex);
            return false;
        }
    }

    private void OnShowKeyboardRequested()
    {
        ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        StopHook();

        if (_automation != null)
        {
            try
            {
                Marshal.ReleaseComObject(_automation);
                _automation = null;
            }
            catch (Exception ex)
            {
                Logger.Error("Error releasing UI Automation COM object", ex);
            }
        }

        Logger.Info("AutoShowManager disposed");
    }
}