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
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Mouse event constants
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    // UI Automation control type IDs for text inputs
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_ComboBoxControlTypeId = 50003; // Added ComboBox support
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_PaneControlTypeId = 50033;
    private const int UIA_CustomControlTypeId = 50025; // Often used in web/electron

    // UI Automation property IDs
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_ClassNamePropertyId = 30012;

    // UI Automation pattern IDs
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;

    // Cooldown to prevent keyboard from showing immediately after hide
    private const int HIDE_COOLDOWN_MS = 1000;
    
    // Time window after mouse click to consider focus change as click-initiated
    // Increased to 2000ms to handle slow UI responses (Electron/Browsers)
    private const int CLICK_TIMEOUT_MS = 2000;

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

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
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

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

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
    private IntPtr _mouseHookHandle;
    private WinEventDelegate _hookDelegate;
    private LowLevelMouseProc _mouseHookDelegate;
    private IUIAutomation _automation;
    private bool _isEnabled;
    private DateTime _lastHideTime;
    private DateTime _lastMouseClickTime;
    private POINT _lastClickPosition;
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
        _lastMouseClickTime = DateTime.MinValue;
        _lastClickPosition = new POINT { x = -1, y = -1 };
        
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

            // Install mouse hook to track clicks
            _mouseHookDelegate = new LowLevelMouseProc(MouseHookCallback);
            _mouseHookHandle = SetWindowsHookEx(
                WH_MOUSE_LL, 
                _mouseHookDelegate, 
                GetModuleHandle(null), 
                0);

            if (_mouseHookHandle != IntPtr.Zero)
            {
                Logger.Info("Mouse hook installed successfully");
            }
            else
            {
                Logger.Error("Failed to install mouse hook");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error installing event hooks", ex);
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

        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
            Logger.Info("Mouse hook removed");
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
        {
            try
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr clickedWindow = WindowFromPoint(hookStruct.pt);

                // Ignore clicks on keyboard window
                if (clickedWindow != _keyboardWindowHandle)
                {
                    _lastMouseClickTime = DateTime.Now;
                    _lastClickPosition = hookStruct.pt;
                    Logger.Debug($"Mouse click detected at ({hookStruct.pt.x}, {hookStruct.pt.y}), window: 0x{clickedWindow:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error processing mouse click", ex);
            }
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
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

        // Check if there was a recent mouse click
        double timeSinceClick = (DateTime.Now - _lastMouseClickTime).TotalMilliseconds;
        
        // If click was too long ago, ignore it. 
        if (timeSinceClick > CLICK_TIMEOUT_MS)
        {
            Logger.Debug($"Focus event ignored - no recent click (last click {timeSinceClick:F0}ms ago)");
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

            Logger.Debug($"Processing focus event: hwnd=0x{hwnd:X}, click position: ({_lastClickPosition.x}, {_lastClickPosition.y})");

            // Check if focused element is a text input
            if (IsTextInputElement(hwnd))
            {
                // We rely on the time check we did above. 
                // If it's a text input and the user clicked recently, we show the keyboard.
                // We do NOT strictly check IsFocusClickInitiated() anymore because mismatched elements 
                // in web apps caused failures.
                
                Logger.Info($"Text input focused in window 0x{hwnd:X} after recent click ({timeSinceClick:F0}ms). Showing keyboard.");
                OnShowKeyboardRequested();
            }
            else
            {
                Logger.Debug("Focus event ignored - not a text input element");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error processing focus event", ex);
        }
    }

    /// <summary>
    /// Heuristic to check if click and focus locations match.
    /// Kept for debugging/logging, but no longer a strict requirement for showing keyboard.
    /// </summary>
    private bool IsFocusClickInitiated()
    {
        if (_lastClickPosition.x < 0 || _lastClickPosition.y < 0) return false;
        if (_automation == null) return false;

        IUIAutomationElement clickedElement = null;
        IUIAutomationElement focusedElement = null;

        try
        {
            var pt = new tagPOINT { x = _lastClickPosition.x, y = _lastClickPosition.y };
            clickedElement = _automation.ElementFromPoint(pt);
            focusedElement = _automation.GetFocusedElement();

            if (clickedElement == null || focusedElement == null) return true; // Be permissive

            // Direct comparison
            try {
                if (_automation.CompareElements(clickedElement, focusedElement) == 0) return true;
            } catch { }

            // Parent check
            try {
                var focusedParent = focusedElement.GetCachedParent();
                if (focusedParent != null) {
                    int parentResult = _automation.CompareElements(clickedElement, focusedParent);
                    if (Marshal.IsComObject(focusedParent)) Marshal.ReleaseComObject(focusedParent);
                    if (parentResult == 0) return true;
                }
            } catch { }

            return false;
        }
        catch
        {
            return true; // Default to permissive on error
        }
        finally
        {
            if (clickedElement != null && Marshal.IsComObject(clickedElement)) try { Marshal.ReleaseComObject(clickedElement); } catch { }
            if (focusedElement != null && Marshal.IsComObject(focusedElement)) try { Marshal.ReleaseComObject(focusedElement); } catch { }
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
            {
                Logger.Debug("GetFocusedElement returned null");
                return false;
            }

            // Check if element actually has keyboard focus
            if (!focusedElement.CurrentHasKeyboardFocus)
            {
                Logger.Debug("Element does not have keyboard focus");
                return false;
            }

            // Check control type
            int controlType = focusedElement.CurrentControlType;
            string className = focusedElement.CurrentClassName ?? "";
            
            Logger.Debug($"Checking element: Type={controlType}, Class='{className}'");
            
            // Edit controls and ComboBoxes are always valid text inputs
            if (controlType == UIA_EditControlTypeId || controlType == UIA_ComboBoxControlTypeId)
            {
                Logger.Debug($"✓ Text input detected - Control Type: {controlType}");
                return true;
            }

            // Pane controls with Scintilla class (Notepad++, code editors)
            if (controlType == UIA_PaneControlTypeId && 
                className.IndexOf("Scintilla", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Logger.Debug($"✓ Text input detected - Scintilla editor (Pane + Scintilla class)");
                return true;
            }

            // Document controls (web pages) need additional validation
            if (controlType == UIA_DocumentControlTypeId)
            {
                bool hasEditablePattern = false;
                
                // Check Value pattern (editable text fields)
                try
                {
                    var valuePattern = focusedElement.GetCurrentPattern(UIA_ValuePatternId) as IUIAutomationValuePattern;
                    if (valuePattern != null)
                    {
                        // Wrap check in try-catch as some OLE implementations throw exceptions here
                        bool isReadOnly = false;
                        try {
                            isReadOnly = valuePattern.CurrentIsReadOnly;
                        } catch {
                            Logger.Debug("Could not read IsReadOnly, assuming editable");
                        }

                        if (!isReadOnly)
                        {
                            Logger.Debug("✓ Text input detected - Document with editable Value Pattern");
                            hasEditablePattern = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Value pattern check failed: {ex.Message}");
                }

                if (!hasEditablePattern)
                {
                    try
                    {
                        var textPattern = focusedElement.GetCurrentPattern(UIA_TextPatternId);
                        if (textPattern != null)
                        {
                            if (className.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                className.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Logger.Debug("✓ Text input detected - Document with Text Pattern (editable)");
                                hasEditablePattern = true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Text pattern check failed: {ex.Message}");
                    }
                }
                return hasEditablePattern;
            }

            // Check for Value pattern (other editable text fields)
            try
            {
                var valuePattern = focusedElement.GetCurrentPattern(UIA_ValuePatternId) as IUIAutomationValuePattern;
                if (valuePattern != null)
                {
                    // CRITICAL FIX: Wrapped in try-catch to handle 'Specified OLE variant is invalid' exceptions
                    // which caused the logic to fail prematurely for many apps
                    bool isReadOnly = false;
                    try 
                    {
                        isReadOnly = valuePattern.CurrentIsReadOnly;
                    } 
                    catch (Exception valEx) 
                    {
                        Logger.Debug($"ValuePattern property read error: {valEx.Message}. Assuming editable.");
                        isReadOnly = false; // Fail safe: assume editable if we can't read it
                    }
                    
                    if (!isReadOnly)
                    {
                        Logger.Debug($"✓ Text input detected - Control Type {controlType} with editable Value Pattern");
                        return true;
                    }
                    else
                    {
                        Logger.Debug($"Element has Value Pattern but is read-only");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Value pattern check (general) failed: {ex.Message}");
            }

            // Last resort: Check for generic Custom/Group controls that are focusable (Common in Web/Electron)
            if (controlType == UIA_CustomControlTypeId || controlType == 50026 /* Group */)
            {
                if (focusedElement.CurrentIsKeyboardFocusable && focusedElement.CurrentIsEnabled)
                {
                     // If we are here, we passed the ValuePattern check (or it failed), but the element accepts focus.
                     // In modern web apps, this is often enough to assume input.
                     Logger.Debug("✓ Text input assumed - Custom/Group control with keyboard focus");
                     return true;
                }
            }

            Logger.Debug($"✗ Element is not a text input - Type={controlType}, Class='{className}'");
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
                try { Marshal.ReleaseComObject(focusedElement); } catch { }
            }
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