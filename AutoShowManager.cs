using System;
using System.Runtime.InteropServices;

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

    // UI Automation pattern IDs
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;

    // Cooldown to prevent keyboard from showing immediately after hide
    private const int HIDE_COOLDOWN_MS = 1000;

    #region Win32 Imports

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

    #endregion

    #region UI Automation COM Interfaces

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement found);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updatedElement);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, out object retVal);
        [PreserveSig] int GetCurrentPropertyValueEx(int propertyId, bool ignoreDefaultValue, out object retVal);
        [PreserveSig] int GetCachedPropertyValue(int propertyId, out object retVal);
        [PreserveSig] int GetCachedPropertyValueEx(int propertyId, bool ignoreDefaultValue, out object retVal);
        [PreserveSig] int GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        [PreserveSig] int GetCurrentPattern(int patternId, out IntPtr patternObject);
        [PreserveSig] int GetCachedPattern(int patternId, out IntPtr patternObject);
        [PreserveSig] int GetCachedParent(out IUIAutomationElement parent);
        [PreserveSig] int GetCachedChildren(out IntPtr children);
        [PreserveSig] int get_CurrentProcessId(out int retVal);
        [PreserveSig] int get_CurrentControlType(out int retVal);
        [PreserveSig] int get_CurrentLocalizedControlType(out string retVal);
        [PreserveSig] int get_CurrentName(out string retVal);
        [PreserveSig] int get_CurrentAcceleratorKey(out string retVal);
        [PreserveSig] int get_CurrentAccessKey(out string retVal);
        [PreserveSig] int get_CurrentHasKeyboardFocus(out int retVal);
        [PreserveSig] int get_CurrentIsKeyboardFocusable(out int retVal);
        [PreserveSig] int get_CurrentIsEnabled(out int retVal);
        [PreserveSig] int get_CurrentAutomationId(out string retVal);
        [PreserveSig] int get_CurrentClassName(out string retVal);
    }

    [ComImport]
    [Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        [PreserveSig] int SetValue(string val);
        [PreserveSig] int get_CurrentValue(out string retVal);
        [PreserveSig] int get_CurrentIsReadOnly(out int retVal);
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);
        [PreserveSig] int CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        [PreserveSig] int GetRootElement(out IUIAutomationElement root);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        [PreserveSig] int ElementFromPoint(System.Drawing.Point pt, out IUIAutomationElement element);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement element);
    }

    [ComImport]
    [Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")]
    [ClassInterface(ClassInterfaceType.None)]
    private class CUIAutomation8 { }

    #endregion

    private readonly IntPtr _keyboardWindowHandle;
    private IntPtr _hookHandle;
    private WinEventDelegate _hookDelegate;
    private IUIAutomation _automation;
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
            _automation = (IUIAutomation)new CUIAutomation8();
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
            IUIAutomationElement focusedElement = null;
            int hr = _automation.GetFocusedElement(out focusedElement);
            
            if (hr != 0 || focusedElement == null)
                return false;

            try
            {
                // Check control type
                int controlType = 0;
                hr = focusedElement.get_CurrentControlType(out controlType);
                
                if (hr == 0 && (controlType == UIA_EditControlTypeId || 
                                controlType == UIA_DocumentControlTypeId))
                {
                    Logger.Debug($"Text input detected - Control Type: {controlType}");
                    return true;
                }

                // Check for Value pattern (editable text fields)
                IntPtr valuePatternPtr = IntPtr.Zero;
                hr = focusedElement.GetCurrentPattern(UIA_ValuePatternId, out valuePatternPtr);
                
                if (hr == 0 && valuePatternPtr != IntPtr.Zero)
                {
                    try
                    {
                        var valuePattern = Marshal.GetObjectForIUnknown(valuePatternPtr) as IUIAutomationValuePattern;
                        if (valuePattern != null)
                        {
                            int isReadOnly = 0;
                            hr = valuePattern.get_CurrentIsReadOnly(out isReadOnly);
                            
                            if (hr == 0 && isReadOnly == 0)
                            {
                                Logger.Debug("Text input detected - Value Pattern (editable)");
                                return true;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(valuePatternPtr);
                    }
                }

                // Check for Text pattern (rich text controls)
                IntPtr textPatternPtr = IntPtr.Zero;
                hr = focusedElement.GetCurrentPattern(UIA_TextPatternId, out textPatternPtr);
                
                if (hr == 0 && textPatternPtr != IntPtr.Zero)
                {
                    Marshal.Release(textPatternPtr);
                    Logger.Debug("Text input detected - Text Pattern");
                    return true;
                }

                // Additional check: common text input class names
                if (IsCommonTextInputClassName(hwnd))
                {
                    Logger.Debug("Text input detected - Common class name");
                    return true;
                }

                return false;
            }
            finally
            {
                if (focusedElement != null && Marshal.IsComObject(focusedElement))
                {
                    Marshal.ReleaseComObject(focusedElement);
                }
            }
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