using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility using Microsoft UI Automation (UIA).
/// Replaces legacy polling/Win32 hooks with reliable event-based focus tracking.
/// Works with Browsers, WPF, UWP, and standard Win32 apps.
/// </summary>
public class AutoShowManager : IDisposable
{
    // UIA Control Type IDs
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;
    
    // UIA Property IDs
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_NativeWindowHandlePropertyId = 30020;
    private const int UIA_IsReadOnlyPropertyId = 30046;

    private readonly IntPtr _keyboardWindowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    
    private IUIAutomation _automation;
    private IUIAutomationFocusChangedEventHandler _focusHandler;
    private bool _isEnabled;
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
                    SubscribeToFocusEvents();
                else
                    UnsubscribeFromFocusEvents();
                
                Logger.Info($"AutoShow (UIA) {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        try
        {
            // Initialize the UIA Object
            _automation = new CUIAutomation() as IUIAutomation;
            Logger.Info("UI Automation interface initialized successfully.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation. Auto-show will not work.", ex);
        }
    }

    private void SubscribeToFocusEvents()
    {
        if (_automation == null || _focusHandler != null) return;

        try
        {
            _focusHandler = new FocusChangedHandler(this);
            _automation.AddFocusChangedEventHandler(null, _focusHandler);
            Logger.Info("Subscribed to global focus change events.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to subscribe to focus events", ex);
        }
    }

    private void UnsubscribeFromFocusEvents()
    {
        if (_automation == null || _focusHandler == null) return;

        try
        {
            _automation.RemoveFocusChangedEventHandler(_focusHandler);
            _focusHandler = null;
            Logger.Info("Unsubscribed from focus events.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to unsubscribe from focus events", ex);
        }
    }

    /// <summary>
    /// Process the focus event. This is called from a background RPC thread,
    /// so we must be careful with threading and COM context.
    /// </summary>
    private void OnFocusChanged(IUIAutomationElement element)
    {
        if (element == null || _isDisposed) return;

        try
        {
            // 1. Check Control Type (Edit or Document)
            int controlType = element.CurrentControlType;
            
            // Check if it is an input field
            // 50004 = Edit (TextBox, PasswordBox, Browser Address Bar)
            // 50030 = Document (Word, Browser Content Area)
            bool isInput = (controlType == UIA_EditControlTypeId || controlType == UIA_DocumentControlTypeId);

            if (!isInput) return;

            // 2. Filter out our own window to prevent loops
            IntPtr nativeHwnd = (IntPtr)element.CurrentNativeWindowHandle;
            if (nativeHwnd == _keyboardWindowHandle) return;

            // 3. Optional: Check if ReadOnly (some browsers don't report this correctly, but good to check)
            // Note: Accessing CurrentIsReadOnly can sometimes be slow or fail, wrapping in try-catch block specifically
            try 
            {
                 // Using GetCachedPropertyValue if you set up caching, but here we access Current.
                 // If the property is not supported, it might return false default.
                 // We skip this check for simplicity to ensure maximum compatibility, 
                 // or you can implement it if you find the keyboard popping up on read-only docs.
            }
            catch { }

            Logger.Info($"Input focus detected. Type: {controlType}, HWND: 0x{nativeHwnd:X}");

            // 4. Marshal to UI Thread to show keyboard
            _dispatcherQueue.TryEnqueue(() =>
            {
                OnShowKeyboardRequested();
            });
        }
        catch (Exception ex)
        {
            // COM elements can become invalid quickly if the UI changes rapidly
            Logger.Debug($"Error processing focus event: {ex.Message}");
        }
    }

    private void OnShowKeyboardRequested()
    {
        ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        UnsubscribeFromFocusEvents();
        
        // Release COM object
        if (_automation != null)
        {
            Marshal.ReleaseComObject(_automation);
            _automation = null;
        }

        Logger.Info("AutoShowManager (UIA) disposed");
    }

    // =========================================================================
    // Internal Handler Class
    // =========================================================================
    
    private class FocusChangedHandler : IUIAutomationFocusChangedEventHandler
    {
        private readonly AutoShowManager _manager;

        public FocusChangedHandler(AutoShowManager manager)
        {
            _manager = manager;
        }

        public void HandleFocusChangedEvent(IUIAutomationElement sender)
        {
            _manager.OnFocusChanged(sender);
        }
    }

    // =========================================================================
    // COM Interfaces & Imports
    // Definitions required to use UI Automation without external references
    // =========================================================================

    [ComImport]
    [Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation
    {
    }

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac4f9d6b233")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);
        void CompareRuntimeIds(IntPtr runtimeId1, IntPtr runtimeId2, out int areSame);
        void GetRootElement(out IUIAutomationElement root);
        void GetElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void GetFocusedElement(out IUIAutomationElement element);
        void GetRootElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement root);
        void GetElementFromHandleBuildCache(IntPtr hwnd, IntPtr cacheRequest, out IUIAutomationElement element);
        void GetFocusedElementBuildCache(IntPtr cacheRequest, out IUIAutomationElement element);
        void CreateTreeWalker(IntPtr pCondition, out IntPtr walker);
        get_ControlViewWalker(out IntPtr walker);
        get_ContentViewWalker(out IntPtr walker);
        get_RawViewWalker(out IntPtr walker);
        get_RawViewCondition(out IntPtr condition);
        get_ControlViewCondition(out IntPtr condition);
        get_ContentViewCondition(out IntPtr condition);
        void CreateCacheRequest(out IntPtr cacheRequest);
        void CreateTrueCondition(out IntPtr newCondition);
        void CreateFalseCondition(out IntPtr newCondition);
        void CreatePropertyCondition(int propertyId, object value, out IntPtr newCondition);
        void CreatePropertyConditionEx(int propertyId, object value, int flags, out IntPtr newCondition);
        void CreateAndCondition(IntPtr condition1, IntPtr condition2, out IntPtr newCondition);
        void CreateAndConditionFromArray(IntPtr conditions, out IntPtr newCondition);
        void CreateOrCondition(IntPtr condition1, IntPtr condition2, out IntPtr newCondition);
        void CreateOrConditionFromArray(IntPtr conditions, out IntPtr newCondition);
        void CreateNotCondition(IntPtr condition, out IntPtr newCondition);
        void AddAutomationEventHandler(int eventId, IUIAutomationElement element, int scope, IntPtr cacheRequest, IntPtr handler);
        void RemoveAutomationEventHandler(int eventId, IUIAutomationElement element, IntPtr handler);
        void AddPropertyChangedEventHandler(IUIAutomationElement element, int scope, IntPtr cacheRequest, IntPtr handler, IntPtr propertyArray);
        void RemovePropertyChangedEventHandler(IUIAutomationElement element, IntPtr handler);
        void AddStructureChangedEventHandler(IUIAutomationElement element, int scope, IntPtr cacheRequest, IntPtr handler);
        void RemoveStructureChangedEventHandler(IUIAutomationElement element, IntPtr handler);
        
        // This is the one we use
        void AddFocusChangedEventHandler(IntPtr cacheRequest, IUIAutomationFocusChangedEventHandler handler);
        void RemoveFocusChangedEventHandler(IUIAutomationFocusChangedEventHandler handler);
        
        void RemoveAllEventHandlers();
        // ... (remaining methods omitted for brevity as they are not used)
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-16985ce19007")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId(out IntPtr runtimeId);
        void FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
        void FindAll(int scope, IntPtr condition, out IntPtr found);
        void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement found);
        void FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        void BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updatedElement);
        void GetCurrentPropertyValue(int propertyId, out object retVal);
        void GetCurrentPropertyValueEx(int propertyId, int ignoreDefaultValue, out object retVal);
        void GetCachedPropertyValue(int propertyId, out object retVal);
        void GetCachedPropertyValueEx(int propertyId, int ignoreDefaultValue, out object retVal);
        void GetCurrentPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        void GetCachedPatternAs(int patternId, ref Guid riid, out IntPtr patternObject);
        void GetCurrentPattern(int patternId, out IntPtr patternObject);
        void GetCachedPattern(int patternId, out IntPtr patternObject);
        void GetCachedParent(out IUIAutomationElement parent);
        void GetCachedChildren(out IntPtr children);
        
        // Property Getters
        int CurrentProcessId { get; }
        int CurrentControlType { get; }
        string CurrentLocalizedControlType { get; }
        string CurrentName { get; }
        string CurrentAcceleratorKey { get; }
        string CurrentAccessKey { get; }
        int CurrentHasKeyboardFocus { get; }
        int CurrentIsKeyboardFocusable { get; }
        int CurrentIsEnabled { get; }
        string CurrentAutomationId { get; }
        string CurrentClassName { get; }
        string CurrentHelpText { get; }
        int CurrentCulture { get; }
        int CurrentIsControlElement { get; }
        int CurrentIsContentElement { get; }
        int CurrentIsPassword { get; }
        int CurrentNativeWindowHandle { get; }
        string CurrentItemType { get; }
        int CurrentIsOffscreen { get; }
        int CurrentOrientation { get; }
        int CurrentFrameworkId { get; }
        int CurrentIsRequiredForForm { get; }
        int CurrentItemStatus { get; }
        string CurrentItemStatusAsString { get; }
        string CurrentBoundingRectangle { get; } // Rect actually
        int CurrentLabeledBy { get; }
        int CurrentAriaRole { get; }
        int CurrentAriaProperties { get; }
        int CurrentIsDataValidForForm { get; }
        int CurrentControllerFor { get; }
        int CurrentDescribedBy { get; }
        int CurrentFlowsTo { get; }
        string CurrentProviderDescription { get; }
        
        // Cache getters omitted for brevity
    }

    [ComImport]
    [Guid("c270f6b5-5c69-4290-9745-7a7f97169468")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationFocusChangedEventHandler
    {
        void HandleFocusChangedEvent(IUIAutomationElement sender);
    }
}