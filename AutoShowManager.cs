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
    private const int UIA_DataItemControlTypeId = 50007;

    // UI Automation property IDs
    private const int UIA_BoundingRectanglePropertyId = 30001;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_IsEnabledPropertyId = 30010;
    private const int UIA_ClassNamePropertyId = 30012;

    // UI Automation pattern IDs
    private const int UIA_ValuePatternId = 10002;
    private const int UIA_TextPatternId = 10014;

    // Cooldown to prevent keyboard from showing immediately after hide
    private const int HIDE_COOLDOWN_MS = 1000;
    
    // Timeout for click-to-focus correlation
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
    private IUIAutomationElement _clickedElement;
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
        _clickedElement = null;
        
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
                    
                    // КЛЮЧЕВОЕ ИЗМЕНЕНИЕ: получаем элемент под курсором в момент клика
                    ReleaseClickedElement();
                    _clickedElement = GetElementAtPoint(hookStruct.pt);
                    
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

    /// <summary>
    /// Получает UI Automation элемент в указанной точке экрана
    /// </summary>
    private IUIAutomationElement GetElementAtPoint(POINT pt)
    {
        try
        {
            var tagPt = new tagPOINT { x = pt.x, y = pt.y };
            return _automation.ElementFromPoint(tagPt);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Failed to get element at point ({pt.x}, {pt.y}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Освобождает COM-объект кликнутого элемента
    /// </summary>
    private void ReleaseClickedElement()
    {
        if (_clickedElement != null)
        {
            try
            {
                Marshal.ReleaseComObject(_clickedElement);
            }
            catch { }
            _clickedElement = null;
        }
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
            // 5. Get the actual focused element
            IUIAutomationElement focusedElement = _automation.GetFocusedElement();
            if (focusedElement == null) return;

            // 6. НОВАЯ ЛОГИКА: проверяем элемент, на который кликнули
            bool isTextInputClicked = false;
            
            if (_clickedElement != null)
            {
                // Проверяем сам кликнутый элемент
                isTextInputClicked = IsTextInputElement(_clickedElement);
                
                if (!isTextInputClicked)
                {
                    // Проверяем, может быть это wrapper - ищем текстовое поле в дочерних элементах
                    isTextInputClicked = HasTextInputChild(_clickedElement);
                }
                
                if (isTextInputClicked)
                {
                    Logger.Debug("Clicked element is a text input or contains one");
                }
            }

            // 7. Проверяем сфокусированный элемент
            bool isFocusedTextInput = IsTextInputElement(focusedElement);
            
            if (!isFocusedTextInput)
            {
                // Может быть фокус на wrapper - проверяем дочерние элементы
                isFocusedTextInput = HasTextInputChild(focusedElement);
            }

            // 8. Принимаем решение: либо кликнули на текстовое поле, либо фокус на текстовом поле
            // Это позволяет обрабатывать случаи, когда фокус переходит на родительский элемент
            if (isTextInputClicked || isFocusedTextInput)
            {
                Logger.Info($"Text input activated by click. Latency: {timeSinceClick:F0}ms. Showing keyboard.");
                OnShowKeyboardRequested();
            }
            else
            {
                Logger.Debug("Focus event ignored - Neither clicked nor focused element is a text input");
            }

            Marshal.ReleaseComObject(focusedElement);
        }
        catch (Exception ex)
        {
            Logger.Error("Error processing focus event", ex);
        }
    }

    /// <summary>
    /// Проверяет, содержит ли элемент текстовое поле среди дочерних элементов
    /// </summary>
    private bool HasTextInputChild(IUIAutomationElement element)
    {
        try
        {
            var children = element.GetCachedChildren();
            if (children == null) return false;

            int count = children.Length;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var child = children.GetElement(i);
                    if (child != null)
                    {
                        bool isTextInput = IsTextInputElement(child);
                        Marshal.ReleaseComObject(child);
                        
                        if (isTextInput)
                        {
                            Logger.Debug($"Found text input in child element {i}/{count}");
                            return true;
                        }
                    }
                }
                catch { }
            }
            
            Marshal.ReleaseComObject(children);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error checking child elements: {ex.Message}");
        }
        
        return false;
    }

    /// <summary>
    /// Расширенная проверка на текстовое поле с поддержкой большего количества типов
    /// </summary>
    private bool IsTextInputElement(IUIAutomationElement element)
    {
        try
        {
            if (element == null) return false;

            // 1. Basic properties
            int controlType = 0;
            string className = "";
            bool isEnabled = true;

            try { controlType = element.CurrentControlType; } catch { }
            try { className = element.CurrentClassName ?? ""; } catch { }
            try { isEnabled = element.CurrentIsEnabled; } catch { }

            if (!isEnabled) return false;

            Logger.Debug($"Checking element: Type={controlType}, Class='{className}'");

            // 2. Прямые типы текстовых полей
            if (controlType == UIA_EditControlTypeId || controlType == UIA_ComboBoxControlTypeId)
            {
                Logger.Debug("✓ Text input - Edit/ComboBox control");
                return true;
            }

            // 3. Document / Pane (Web & Electron apps)
            if (controlType == UIA_DocumentControlTypeId || controlType == UIA_PaneControlTypeId)
            {
                // Value Pattern check
                if (HasEditableValuePattern(element))
                {
                    Logger.Debug("✓ Text input - Editable Value Pattern");
                    return true;
                }

                // Text Pattern check
                if (HasTextPattern(element))
                {
                    // Эвристика: проверяем имя класса
                    if (IsTextInputByClassName(className))
                    {
                        Logger.Debug("✓ Text input - Text Pattern + Class Match");
                        return true;
                    }
                }
            }

            // 4. Custom/Group/DataItem (современные UI фреймворки)
            if (controlType == UIA_CustomControlTypeId || 
                controlType == UIA_GroupControlTypeId ||
                controlType == UIA_DataItemControlTypeId)
            {
                // Проверяем по имени класса
                if (IsTextInputByClassName(className))
                {
                    Logger.Debug("✓ Text input - Custom control with text input class name");
                    return true;
                }
                
                // Проверяем, есть ли Value Pattern
                if (HasEditableValuePattern(element))
                {
                    Logger.Debug("✓ Text input - Custom control with editable Value Pattern");
                    return true;
                }
                
                // Проверяем фокусируемость для Custom/Group
                if (controlType != UIA_DataItemControlTypeId)
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
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in IsTextInputElement: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Проверяет наличие редактируемого Value Pattern
    /// </summary>
    private bool HasEditableValuePattern(IUIAutomationElement element)
    {
        try
        {
            var valuePattern = element.GetCurrentPattern(UIA_ValuePatternId) as IUIAutomationValuePattern;
            if (valuePattern != null)
            {
                bool isReadOnly = false;
                try { isReadOnly = valuePattern.CurrentIsReadOnly; }
                catch { return false; }
                
                return !isReadOnly;
            }
        }
        catch { }
        
        return false;
    }

    /// <summary>
    /// Проверяет наличие Text Pattern
    /// </summary>
    private bool HasTextPattern(IUIAutomationElement element)
    {
        try
        {
            var textPattern = element.GetCurrentPattern(UIA_TextPatternId);
            return textPattern != null;
        }
        catch { }
        
        return false;
    }

    /// <summary>
    /// Определяет, является ли элемент текстовым полем по имени класса
    /// </summary>
    private bool IsTextInputByClassName(string className)
    {
        if (string.IsNullOrEmpty(className))
            return false;

        string lowerClass = className.ToLowerInvariant();
        
        // Ключевые слова, указывающие на текстовое поле
        string[] textInputKeywords = new[]
        {
            "edit", "text", "input", "search", "box",
            "textbox", "editbox", "searchbox",
            "richedit", "scintilla", // Notepad++, advanced editors
            "address", "url", "urlbar", // Browsers
            "combo", "autocomplete"
        };

        foreach (var keyword in textInputKeywords)
        {
            if (lowerClass.Contains(keyword))
            {
                Logger.Debug($"Class name '{className}' matches text input keyword '{keyword}'");
                return true;
            }
        }

        return false;
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

        ReleaseClickedElement();

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