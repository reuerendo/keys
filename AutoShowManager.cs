using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility using Microsoft UI Automation (UIA).
/// Works with Browsers (Chrome, Edge), Office, WPF, UWP, and standard Win32 apps.
/// </summary>
public class AutoShowManager : IDisposable
{
    // UIA Control Type IDs
    private const int UIA_EditControlTypeId = 50004;
    private const int UIA_DocumentControlTypeId = 50030;
	
	private const int COINIT_APARTMENTTHREADED = 0x2;
	private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    
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

        InitializeAutomation();
    }

	private void InitializeAutomation()
	{
		try
		{
			// Проверяем наличие UIAutomationCore.dll
			string uiaPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "UIAutomationCore.dll");
			if (!File.Exists(uiaPath))
			{
				Logger.Error($"UIAutomationCore.dll not found at: {uiaPath}");
				Logger.Error("UI Automation is not available on this system.");
				return;
			}
			Logger.Info($"UIAutomationCore.dll found at: {uiaPath}");
			
			// Явная инициализация COM для текущего потока (STA)
			int comInitResult = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
			if (comInitResult < 0 && comInitResult != RPC_E_CHANGED_MODE)
			{
				Logger.Warning($"COM initialization warning (HR: 0x{comInitResult:X}). This may be okay if COM is already initialized.");
			}
			
			// Определяем GUID-ы
			Guid iidIUIAutomation = new Guid("30cbe57d-d9d0-452a-ab13-7ac4f9d6b233");
			Guid clsidCUIAutomation = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
			Guid clsidCUIAutomation8 = new Guid("e22ad333-b25f-460c-83d0-0581107395c9"); // Windows 8+
			
			const int CLSCTX_INPROC_SERVER = 0x1;
			const int CLSCTX_LOCAL_SERVER = 0x4;
			const int CLSCTX_REMOTE_SERVER = 0x10;
			const int CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER;
			
			object obj = null;
			int hr = -1;
			
			// Метод 1: CoCreateInstance с стандартным CUIAutomation
			Logger.Info("Attempting Method 1: CoCreateInstance with CUIAutomation...");
			hr = CoCreateInstance(clsidCUIAutomation, IntPtr.Zero, CLSCTX_ALL, iidIUIAutomation, out obj);
			
			if (hr < 0)
			{
				Logger.Warning($"Method 1 failed (HR: 0x{hr:X})");
				
				// Метод 2: CoCreateInstance с CUIAutomation8
				Logger.Info("Attempting Method 2: CoCreateInstance with CUIAutomation8...");
				hr = CoCreateInstance(clsidCUIAutomation8, IntPtr.Zero, CLSCTX_ALL, iidIUIAutomation, out obj);
				
				if (hr < 0)
				{
					Logger.Warning($"Method 2 failed (HR: 0x{hr:X})");
					
					// Метод 3: Type.GetTypeFromCLSID
					Logger.Info("Attempting Method 3: Type.GetTypeFromCLSID...");
					try
					{
						Type automationType = Type.GetTypeFromCLSID(clsidCUIAutomation8);
						if (automationType != null)
						{
							obj = Activator.CreateInstance(automationType);
							if (obj != null)
							{
								hr = 0; // Success
								Logger.Info("Method 3 succeeded using Type.GetTypeFromCLSID");
							}
						}
					}
					catch (Exception ex)
					{
						Logger.Warning($"Method 3 failed: {ex.Message}");
					}
				}
			}

			if (hr >= 0 && obj != null)
			{
				_automation = obj as IUIAutomation;
				
				if (_automation != null)
				{
					Logger.Info($"✓ UI Automation initialized successfully (HRESULT: 0x{hr:X}).");
					
					// Проверяем, что интерфейс действительно работает
					try
					{
						_automation.GetRootElement(out var rootElement);
						if (rootElement != null)
						{
							Logger.Info("✓ UI Automation root element accessible - interface is functional.");
							Marshal.ReleaseComObject(rootElement);
						}
					}
					catch (Exception ex)
					{
						Logger.Warning($"UI Automation interface may not be fully functional: {ex.Message}");
					}
				}
				else
				{
					Logger.Error($"Object created but cast to IUIAutomation failed. HR: 0x{hr:X}");
					Logger.Error($"Object type: {obj?.GetType()?.FullName ?? "null"}");
				}
			}
			else
			{
				Logger.Error($"✗ CRITICAL: Failed to create IUIAutomation via all methods. Last HR: 0x{hr:X}");
				
				// Детальная диагностика
				if (hr == unchecked((int)0x80004002))
				{
					Logger.Error("E_NOINTERFACE (0x80004002) - Interface not supported. Possible causes:");
					Logger.Error("  1. UI Automation components not properly registered");
					Logger.Error("  2. Missing Windows updates or components");
					Logger.Error("  3. Application architecture mismatch (ensure app is x64 on x64 system)");
					Logger.Error("  4. Try running as Administrator");
					Logger.Error("  5. Check Windows Features: UI Automation should be enabled");
				}
				else if (hr == unchecked((int)0x80040154))
				{
					Logger.Error("REGDB_E_CLASSNOTREG (0x80040154) - Class not registered.");
					Logger.Error("  Try running: regsvr32 UIAutomationCore.dll from elevated command prompt");
				}
				else if (hr == unchecked((int)0x80070005))
				{
					Logger.Error("E_ACCESSDENIED (0x80070005) - Access denied. Run as Administrator.");
				}
				
				// Предлагаем workaround
				Logger.Info("AutoShow feature will be disabled. Keyboard can still be shown manually from tray icon.");
			}
		}
		catch (Exception ex)
		{
			Logger.Error("Exception during UI Automation initialization:", ex);
		}
	}

    private void SubscribeToFocusEvents()
    {
        // Добавляем логирование причины выхода
        if (_automation == null) 
        {
            Logger.Warning("Attempted to subscribe to focus events, but Automation is null.");
            return;
        }
        
        if (_focusHandler != null) return;

        try
        {
            _focusHandler = new FocusChangedHandler(this);
            _automation.AddFocusChangedEventHandler(IntPtr.Zero, _focusHandler);
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

    private void OnFocusChanged(IUIAutomationElement element)
    {
        if (element == null || _isDisposed) return;

        try
        {
            int controlType = element.CurrentControlType;
            
            // 50004 = Edit, 50030 = Document
            bool isInput = (controlType == UIA_EditControlTypeId || controlType == UIA_DocumentControlTypeId);

            if (!isInput) return;

            IntPtr nativeHwnd = (IntPtr)element.CurrentNativeWindowHandle;
            if (nativeHwnd == _keyboardWindowHandle) return;

            Logger.Info($"Input focus detected. Type: {controlType}, HWND: 0x{nativeHwnd:X}");

            _dispatcherQueue.TryEnqueue(() =>
            {
                OnShowKeyboardRequested();
            });
        }
        catch (Exception ex)
        {
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
        
        if (_automation != null)
        {
            Marshal.ReleaseComObject(_automation);
            _automation = null;
        }

        Logger.Info("AutoShowManager (UIA) disposed");
    }

    private class FocusChangedHandler : IUIAutomationFocusChangedEventHandler
    {
        private readonly AutoShowManager _manager;
        public FocusChangedHandler(AutoShowManager manager) => _manager = manager;
        public void HandleFocusChangedEvent(IUIAutomationElement sender) => _manager.OnFocusChanged(sender);
    }
	
// =========================================================================
    // Native Methods
    // =========================================================================
	
	[DllImport("ole32.dll")]
	private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CoCreateInstance(
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        IntPtr pUnkOuter,
        int dwClsContext,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    // =========================================================================
    // COM Interfaces
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
        
        // Corrected signatures: explicit void return type
        void get_ControlViewWalker(out IntPtr walker);
        void get_ContentViewWalker(out IntPtr walker);
        void get_RawViewWalker(out IntPtr walker);
        void get_RawViewCondition(out IntPtr condition);
        void get_ControlViewCondition(out IntPtr condition);
        void get_ContentViewCondition(out IntPtr condition);
        
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
        
        // This is the target method we need
        void AddFocusChangedEventHandler(IntPtr cacheRequest, IUIAutomationFocusChangedEventHandler handler);
        void RemoveFocusChangedEventHandler(IUIAutomationFocusChangedEventHandler handler);
        
        void RemoveAllEventHandlers();
        void IntentionallyUnused1();
        void IntentionallyUnused2();
        void IntentionallyUnused3();
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
        
        // Properties
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
        string CurrentBoundingRectangle { get; }
        int CurrentLabeledBy { get; }
        int CurrentAriaRole { get; }
        int CurrentAriaProperties { get; }
        int CurrentIsDataValidForForm { get; }
        int CurrentControllerFor { get; }
        int CurrentDescribedBy { get; }
        int CurrentFlowsTo { get; }
        string CurrentProviderDescription { get; }
    }

    [ComImport]
    [Guid("c270f6b5-5c69-4290-9745-7a7f97169468")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationFocusChangedEventHandler
    {
        void HandleFocusChangedEvent(IUIAutomationElement sender);
    }
}