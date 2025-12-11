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

    // UI Automation property IDs
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_ClassNamePropertyId = 30012;

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

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    #endregion

    #region UI Automation COM Interfaces

    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        int[] GetRuntimeId();
        IUIAutomationElement FindFirst(TreeScope scope, IUIAutomationCondition condition);
        IUIAutomationElementArray FindAll(TreeScope scope, IUIAutomationCondition condition);
        IUIAutomationElement FindFirstBuildCache(TreeScope scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest);
        IUIAutomationElementArray FindAllBuildCache(TreeScope scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest);
        IUIAutomationElement BuildUpdatedCache(IUIAutomationCacheRequest cacheRequest);
        object GetCurrentPropertyValue(int propertyId);
        object GetCurrentPropertyValueEx(int propertyId, bool ignoreDefaultValue);
        object GetCachedPropertyValue(int propertyId);
        object GetCachedPropertyValueEx(int propertyId, bool ignoreDefaultValue);
        IntPtr GetCurrentPatternAs(int patternId, [In] ref Guid riid);
        IntPtr GetCachedPatternAs(int patternId, [In] ref Guid riid);
        object GetCurrentPattern(int patternId);
        object GetCachedPattern(int patternId);
        IUIAutomationElement GetCachedParent();
        IUIAutomationElementArray GetCachedChildren();
        int CurrentProcessId { get; }
        int CurrentControlType { get; }
        string CurrentLocalizedControlType { get; }
        string CurrentName { get; }
        string CurrentAcceleratorKey { get; }
        string CurrentAccessKey { get; }
        bool CurrentHasKeyboardFocus { get; }
        bool CurrentIsKeyboardFocusable { get; }
        bool CurrentIsEnabled { get; }
        string CurrentAutomationId { get; }
        string CurrentClassName { get; }
    }

    [ComImport]
    [Guid("14314595-B4BC-4055-95F2-58F2E42C9855")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }

    [ComImport]
    [Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition
    {
    }

    [ComImport]
    [Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCacheRequest
    {
    }

    [ComImport]
    [Guid("A94CD8B1-0844-4CD6-9D2D-640537AB39E9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        void SetValue([MarshalAs(UnmanagedType.BStr)] string val);
        string CurrentValue { get; }
        bool CurrentIsReadOnly { get; }
    }

    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2);
        int CompareRuntimeIds(int[] runtimeId1, int[] runtimeId2);
        IUIAutomationElement GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        IUIAutomationElement ElementFromPoint(tagPOINT pt);
        IUIAutomationElement GetFocusedElement();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct tagPOINT
    {
        public int x;
        public int y;
    }

    private enum TreeScope
    {
        TreeScope_Element = 0x1,
        TreeScope_Children = 0x2,
        TreeScope_Descendants = 0x4,
        TreeScope_Parent = 0x8,
        TreeScope_Ancestors = 0x10,
        TreeScope_Subtree = 0x7
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

        // Don't show if keyboard is already visible
        if (IsWindowVisible(_keyboardWindowHandle))
        {
            Logger.Debug("Focus event ignored - keyboard already visible");
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

        IUIAutomationElement focusedElement = null;

        try
        {
            // Get focused element from UI Automation
            focusedElement = _automation.GetFocusedElement();
            
            if (focusedElement == null)
                return false;

            // Check if element actually has keyboard focus
            if (!focusedElement.CurrentHasKeyboardFocus)
            {
                Logger.Debug("Element does not have keyboard focus");
                return false;
            }

            // Check control type
            int controlType = focusedElement.CurrentControlType;
            
            // Edit controls are always valid text inputs
            if (controlType == UIA_EditControlTypeId)
            {
                Logger.Debug($"Text input detected - Control Type: Edit ({controlType})");
                return true;
            }

            // Document controls (web pages) need additional validation
            if (controlType == UIA_DocumentControlTypeId)
            {
                // Check for editable patterns
                bool hasEditablePattern = false;
                
                // Check Value pattern (editable text fields)
                try
                {
                    var valuePattern = focusedElement.GetCurrentPattern(UIA_ValuePatternId) as IUIAutomationValuePattern;
                    if (valuePattern != null && !valuePattern.CurrentIsReadOnly)
                    {
                        Logger.Debug("Text input detected - Document with editable Value Pattern");
                        hasEditablePattern = true;
                    }
                }
                catch { }

                // Check Text pattern for contenteditable elements
                if (!hasEditablePattern)
                {
                    try
                    {
                        var textPattern = focusedElement.GetCurrentPattern(UIA_TextPatternId);
                        if (textPattern != null)
                        {
                            // Additional check: verify it's actually editable
                            // For contenteditable, check if class name contains "edit" or similar
                            string className = focusedElement.CurrentClassName ?? "";
                            if (className.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                className.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Logger.Debug("Text input detected - Document with Text Pattern (editable)");
                                hasEditablePattern = true;
                            }
                        }
                    }
                    catch { }
                }

                return hasEditablePattern;
            }

            // Check for Value pattern (other editable text fields)
            try
            {
                var valuePattern = focusedElement.GetCurrentPattern(UIA_ValuePatternId) as IUIAutomationValuePattern;
                if (valuePattern != null)
                {
                    bool isReadOnly = valuePattern.CurrentIsReadOnly;
                    
                    if (!isReadOnly)
                    {
                        Logger.Debug($"Text input detected - Control Type {controlType} with editable Value Pattern");
                        return true;
                    }
                }
            }
            catch { }

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
        finally
        {
            if (focusedElement != null && Marshal.IsComObject(focusedElement))
            {
                Marshal.ReleaseComObject(focusedElement);
            }
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