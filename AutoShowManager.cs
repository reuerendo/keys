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

    // Mouse event constants
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    // UI Automation control type IDs for text inputs
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_ComboBoxControlTypeId = 50003;
    private const int UIA_DocumentControlTypeId = 50030;
    private const int UIA_PaneControlTypeId = 50033;
    private const int UIA_CustomControlTypeId = 50025;
    private const int UIA_GroupControlTypeId = 50026;

    // UI Automation property IDs
    private const int UIA_BoundingRectanglePropertyId = 30001; // New: For strict location check
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_ClassNamePropertyId = 30012;

    // UI Automation pattern IDs
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;

    // Cooldown to prevent keyboard from showing immediately after hide
    private const int HIDE_COOLDOWN_MS = 1000;
    
    // Reduced timeout back to 1000ms. Since we now check LOCATION, we can be more strict with time.
    // If we keep it too long, clicking somewhere else and then tabbing might trigger it false-positively.
    private const int CLICK_TIMEOUT_MS = 1000;

    #region Win32 Imports

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, 
        WinEventDelegate lpfnWinEventProc, uint idProcess, 
        uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

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
    private interface IUIAutomationCondition { }

    [ComImport]
    [Guid("B32A92B5-BC25-4078-9C08-D7EE95C48E03")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCacheRequest { }

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
            _hookDelegate = new WinEventDelegate(WinEventCallback);
            
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS,
                IntPtr.Zero, _hookDelegate,
                0, 0, WINEVENT_OUTOFCONTEXT);

            if (_hookHandle != IntPtr.Zero)
                Logger.Info("Focus event hook installed successfully");
            else
                Logger.Error("Failed to install focus event hook");

            _mouseHookDelegate = new LowLevelMouseProc(MouseHookCallback);
            _mouseHookHandle = SetWindowsHookEx(
                WH_MOUSE_LL, 
                _mouseHookDelegate, 
                GetModuleHandle(null), 
                0);

            if (_mouseHookHandle != IntPtr.Zero)
                Logger.Info("Mouse hook installed successfully");
            else
                Logger.Error("Failed to install mouse hook");
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
        if (!_isEnabled || _isDisposed) return;

        // 1. Cooldown check
        if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
        {
            Logger.Debug("Focus event ignored - within cooldown period");
            return;
        }

        // 2. Already visible check
        if (IsWindowVisible(_keyboardWindowHandle))
        {
            Logger.Debug("Focus event ignored - keyboard already visible");
            return;
        }

        // 3. Time check (Click must be recent)
        double timeSinceClick = (DateTime.Now - _lastMouseClickTime).TotalMilliseconds;
        if (timeSinceClick > CLICK_TIMEOUT_MS)
        {
            Logger.Debug($"Focus event ignored - no recent click (last click {timeSinceClick:F0}ms ago)");
            return;
        }

        // 4. Ignore own window
        if (hwnd == _keyboardWindowHandle) return;

        try
        {
            // 5. Get the actual focused element to perform logic checks
            IUIAutomationElement focusedElement = _automation.GetFocusedElement();
            if (focusedElement == null) return;

            // 6. STRICT LOCATION CHECK:
            // Did the click happen inside the element that now has focus?
            // This prevents "Automatic" activation when focus shifts programmatically.
            bool isClickInside = IsClickInsideElement(focusedElement, _lastClickPosition);
            
            if (!isClickInside)
            {
                // Edge case: Sometimes focused element is a generic "Pane" (like in Chrome), 
                // but we clicked an 'input' inside it.
                // However, for strict "click-to-type", requiring overlap is safer.
                Logger.Debug($"Focus event ignored - Click at ({_lastClickPosition.x},{_lastClickPosition.y}) was NOT inside focused element bounds.");
                Marshal.ReleaseComObject(focusedElement);
                return;
            }

            // 7. Check if it is a Text Input
            if (IsTextInputElement(focusedElement))
            {
                Logger.Info($"Text input focused AND clicked (inside bounds). Latency: {timeSinceClick:F0}ms. Showing keyboard.");
                OnShowKeyboardRequested();
            }
            else
            {
                Logger.Debug("Focus event ignored - Element is not a text input");
            }

            Marshal.ReleaseComObject(focusedElement);
        }
        catch (Exception ex)
        {
            Logger.Error("Error processing focus event", ex);
        }
    }

    /// <summary>
    /// Checks if the last click coordinates are within the bounding rectangle of the focused element.
    /// This ensures we only trigger when the USER interacted with THIS specific element.
    /// </summary>
    private bool IsClickInsideElement(IUIAutomationElement element, POINT clickPt)
    {
        try
        {
            // Property ID 30001 is BoundingRectangle
            object rectObj = element.GetCurrentPropertyValue(UIA_BoundingRectanglePropertyId);
            
            // UIA returns an array of 4 doubles: [Left, Top, Width, Height]
            if (rectObj is double[] rectArray && rectArray.Length == 4)
            {
                double left = rectArray[0];
                double top = rectArray[1];
                double width = rectArray[2];
                double height = rectArray[3];
                
                // Check for invalid rects (off-screen or empty)
                if (width <= 0 || height <= 0) return false;

                bool inside = clickPt.x >= left && clickPt.x <= (left + width) &&
                              clickPt.y >= top && clickPt.y <= (top + height);
                              
                if (inside) 
                {
                    Logger.Debug($"Click verified inside element bounds: [{left},{top}, {width}x{height}]");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to check element bounds: {ex.Message}");
            // If we can't check bounds (e.g. some web elements), should we allow it?
            // User requested STRICT activation. So default to false implies safety.
            // But for compatibility, if we can't read bounds, we might assume true if Time is very short.
            // Let's stick to strict for now to solve the user's primary complaint.
            return false;
        }
        return false;
    }

    /// <summary>
    /// Robust check for text input capability with aggressive error handling
    /// </summary>
    private bool IsTextInputElement(IUIAutomationElement element)
    {
        try
        {
            if (element == null) return false;

            // 1. Basic properties (Safe wrappers)
            int controlType = 0;
            string className = "";

            try { controlType = element.CurrentControlType; } catch { }
            try { className = element.CurrentClassName ?? ""; } catch { }

            Logger.Debug($"Checking text input capability: Type={controlType}, Class='{className}'");

            // 2. Fast allow list
            if (controlType == UIA_EditControlTypeId || controlType == UIA_ComboBoxControlTypeId)
                return true;

            // 3. Document / Pane (Web & Electron)
            if (controlType == UIA_DocumentControlTypeId || controlType == UIA_PaneControlTypeId)
            {
                // Try Value Pattern
                try
                {
                    var valuePattern = element.GetCurrentPattern(UIA_ValuePatternId) as IUIAutomationValuePattern;
                    if (valuePattern != null)
                    {
                        bool isReadOnly = false;
                        try { isReadOnly = valuePattern.CurrentIsReadOnly; }
                        catch { /* If we can't read ReadOnly status, usually it means it's not a standard input, or it's dead */ }
                        
                        if (!isReadOnly)
                        {
                            Logger.Debug("✓ Text input - Editable Value Pattern");
                            return true;
                        }
                    }
                }
                catch (COMException) { /* Ignore "OLE variant invalid" etc */ }
                catch (Exception) { }

                // Try Text Pattern (Last resort for Docs)
                try
                {
                    var textPattern = element.GetCurrentPattern(UIA_TextPatternId);
                    if (textPattern != null)
                    {
                        // Heuristic: Only treat as input if class name suggests it, or it's a Document
                        if (controlType == UIA_DocumentControlTypeId || 
                            className.IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            className.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Logger.Debug("✓ Text input - Text Pattern + Class Match");
                            return true;
                        }
                    }
                }
                catch { }
            }

            // 4. Custom/Group (Web apps often use these)
            // Only allow if they are focusable and we clicked inside them (checked previously)
            if (controlType == UIA_CustomControlTypeId || controlType == UIA_GroupControlTypeId)
            {
                try
                {
                    if (element.CurrentIsKeyboardFocusable && element.CurrentIsEnabled)
                    {
                        Logger.Debug("✓ Text input - Focusable Custom/Group element");
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Critical error in IsTextInputElement: {ex.Message}", ex);
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
            catch { }
        }

        Logger.Info("AutoShowManager disposed");
    }
}