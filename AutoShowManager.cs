using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard display using TSF (Text Services Framework)
/// Implements the same mechanism as Windows TabTip.exe
/// </summary>
public class AutoShowManager : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private bool _disposed;
    
    private ITfThreadMgr _threadMgr;
    private ITfSource _source;
    private TsfEventSink _eventSink;
    private uint _eventSinkCookie;
    
    // Delay to prevent immediate re-showing
    private DateTime _lastHideTime = DateTime.MinValue;
    private const int HIDE_COOLDOWN_MS = 500;

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
                    StartMonitoring();
                }
                else
                {
                    StopMonitoring();
                }
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        Logger.Info("AutoShowManager created (TSF-based)");
    }

    /// <summary>
    /// Start TSF monitoring
    /// </summary>
    private void StartMonitoring()
    {
        if (_threadMgr != null)
        {
            Logger.Warning("TSF monitoring already active");
            return;
        }

        try
        {
            // Create ITfThreadMgr instance
            Guid threadMgrGuid = typeof(ITfThreadMgr).GUID;
            var clsid = new Guid("529a9e6b-6587-4f23-ab9e-9c7d683e3c50"); // CLSID_TF_ThreadMgr
            
            Logger.Debug($"Creating ITfThreadMgr with CLSID={clsid}, IID={threadMgrGuid}");
            
            var hr = CoCreateInstance(
                ref clsid,
                IntPtr.Zero,
                CLSCTX.CLSCTX_INPROC_SERVER,
                ref threadMgrGuid,
                out object obj);

            if (hr != 0 || obj == null)
            {
                Logger.Error($"Failed to create ITfThreadMgr: HRESULT=0x{hr:X}");
                return;
            }

            _threadMgr = (ITfThreadMgr)obj;
            Logger.Info("ITfThreadMgr created successfully");

            // Activate TSF for this thread
            hr = _threadMgr.Activate(out uint clientId);
            if (hr != 0)
            {
                Logger.Error($"Failed to activate TSF: HRESULT=0x{hr:X}");
                return;
            }
            Logger.Info($"TSF activated with ClientId={clientId}");

            // Query for ITfSource interface
            Guid sourceGuid = typeof(ITfSource).GUID;
            Logger.Debug($"Querying for ITfSource interface: {sourceGuid}");
            
            IntPtr sourcePtr = IntPtr.Zero;
            hr = Marshal.QueryInterface(Marshal.GetIUnknownForObject(_threadMgr), ref sourceGuid, out sourcePtr);
            
            if (hr != 0 || sourcePtr == IntPtr.Zero)
            {
                Logger.Error($"Failed to query ITfSource: HRESULT=0x{hr:X}");
                return;
            }

            _source = (ITfSource)Marshal.GetObjectForIUnknown(sourcePtr);
            Marshal.Release(sourcePtr);
            Logger.Info("ITfSource interface obtained successfully");

            // Create and register event sink
            _eventSink = new TsfEventSink(this);
            Logger.Debug("TsfEventSink instance created");
            
            // Use GUID for ITfThreadMgrEventSink interface
            Guid sinkGuid = new Guid("aa80e80e-2021-11d2-93e0-0060b067b86e");
            Logger.Debug($"Using ITfThreadMgrEventSink GUID={sinkGuid}");
            
            // Get interface pointer for the event sink
            IntPtr sinkPtr = IntPtr.Zero;
            try
            {
                // Use GetComInterfaceForObject for proper COM marshaling
                sinkPtr = Marshal.GetComInterfaceForObject(_eventSink, typeof(ITfThreadMgrEventSink));
                Logger.Debug($"Got COM interface pointer: 0x{sinkPtr:X}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to get COM interface for event sink: {ex.Message}");
                return;
            }
            
            // Register the event sink through ITfSource
            hr = _source.AdviseSink(ref sinkGuid, sinkPtr, out _eventSinkCookie);
            
            if (sinkPtr != IntPtr.Zero)
                Marshal.Release(sinkPtr);
            
            if (hr != 0)
            {
                Logger.Error($"Failed to register event sink: HRESULT=0x{hr:X}");
                return;
            }

            Logger.Info($"TSF event sink registered successfully (Cookie={_eventSinkCookie})");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start TSF monitoring", ex);
        }
    }

    /// <summary>
    /// Stop TSF monitoring
    /// </summary>
    private void StopMonitoring()
    {
        try
        {
            if (_source != null && _eventSinkCookie != 0)
            {
                _source.UnadviseSink(_eventSinkCookie);
                _eventSinkCookie = 0;
                Logger.Info("TSF event sink unregistered");
            }

            if (_source != null)
            {
                Marshal.ReleaseComObject(_source);
                _source = null;
            }

            if (_threadMgr != null)
            {
                _threadMgr.Deactivate();
                Marshal.ReleaseComObject(_threadMgr);
                _threadMgr = null;
                Logger.Info("TSF deactivated");
            }

            _eventSink = null;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to stop TSF monitoring", ex);
        }
    }

    /// <summary>
    /// Called by TsfEventSink when context focus changes
    /// </summary>
    internal void OnContextFocusChanged(ITfDocumentMgr docMgr, ITfContext context)
    {
        if (!_isEnabled || _disposed)
            return;

        try
        {
            // Check cooldown period
            if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
            {
                Logger.Debug("Within cooldown period, ignoring focus change");
                return;
            }

            // If no context or document manager, ignore
            if (context == null || docMgr == null)
            {
                Logger.Debug("No context or document manager");
                return;
            }

            // Get context information
            // Create local copies of GUIDs because ref parameters cannot use static readonly fields
            Guid inputScopeGuid = TF_ATTRIBUTE_GUID.TF_ATTR_INPUT_SCOPE;
            int hr = context.GetAttribute(
                ref inputScopeGuid,
                out ITfProperty property);

            if (hr == 0 && property != null)
            {
                Logger.Info("Text input context detected - showing keyboard");
                ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
                Marshal.ReleaseComObject(property);
            }
            else
            {
                // Even without input scope, check if it's a text context
                // by checking if context supports text operations
                Guid textOwnerGuid = TF_PROPERTY_GUID.TFPROP_TEXTOWNER;
                hr = context.GetProperty(ref textOwnerGuid, out property);
                if (hr == 0 && property != null)
                {
                    Logger.Info("Text owner context detected - showing keyboard");
                    ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
                    Marshal.ReleaseComObject(property);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error in OnContextFocusChanged", ex);
        }
    }

    /// <summary>
    /// Notify manager that keyboard was hidden
    /// </summary>
    public void NotifyKeyboardHidden()
    {
        _lastHideTime = DateTime.Now;
        Logger.Debug("Keyboard hide notification received, cooldown started");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopMonitoring();
        
        Logger.Info("AutoShowManager disposed");
    }

    // P/Invoke
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        CLSCTX dwClsContext,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [Flags]
    private enum CLSCTX : uint
    {
        CLSCTX_INPROC_SERVER = 0x1,
        CLSCTX_INPROC_HANDLER = 0x2,
        CLSCTX_LOCAL_SERVER = 0x4,
        CLSCTX_REMOTE_SERVER = 0x10,
        CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
    }
}

/// <summary>
/// Event sink for TSF thread manager events
/// </summary>
[ComVisible(true)]
[Guid("F9E1A2B3-4C5D-6E7F-8A9B-0C1D2E3F4A5B")] // Unique GUID for this class
[ClassInterface(ClassInterfaceType.None)]
[ComDefaultInterface(typeof(ITfThreadMgrEventSink))]
internal class TsfEventSink : ITfThreadMgrEventSink
{
    private readonly AutoShowManager _manager;

    public TsfEventSink(AutoShowManager manager)
    {
        _manager = manager;
        Logger.Debug("TsfEventSink constructor called");
    }

    // Called when document manager is initialized
    [PreserveSig]
    public int OnInitDocumentMgr(ITfDocumentMgr pdim)
    {
        Logger.Debug("TSF: OnInitDocumentMgr");
        return 0; // S_OK
    }

    // Called when document manager is uninitialized
    [PreserveSig]
    public int OnUninitDocumentMgr(ITfDocumentMgr pdim)
    {
        Logger.Debug("TSF: OnUninitDocumentMgr");
        return 0; // S_OK
    }

    // Called when focus changes between contexts - THIS IS THE KEY METHOD
    [PreserveSig]
    public int OnSetFocus(ITfDocumentMgr pdimFocus, ITfDocumentMgr pdimPrevFocus)
    {
        Logger.Debug("TSF: OnSetFocus called");

        if (pdimFocus == null)
        {
            Logger.Debug("TSF: Focus cleared (no document manager)");
            return 0;
        }

        try
        {
            // Get the top context from the document manager
            int hr = pdimFocus.GetTop(out ITfContext context);
            
            if (hr != 0 || context == null)
            {
                Logger.Debug("TSF: No top context available");
                return 0;
            }

            Logger.Info("TSF: Text input context received focus");
            _manager.OnContextFocusChanged(pdimFocus, context);

            Marshal.ReleaseComObject(context);
        }
        catch (Exception ex)
        {
            Logger.Error("TSF: Error in OnSetFocus", ex);
        }

        return 0; // S_OK
    }

    // Called when keyboard focus is pushed
    [PreserveSig]
    public int OnPushContext(ITfContext pic)
    {
        Logger.Debug("TSF: OnPushContext");
        return 0; // S_OK
    }

    // Called when keyboard focus is popped
    [PreserveSig]
    public int OnPopContext(ITfContext pic)
    {
        Logger.Debug("TSF: OnPopContext");
        return 0; // S_OK
    }
}

#region TSF COM Interfaces

// ITfThreadMgr - Main TSF thread manager interface
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("aa80e801-2021-11d2-93e0-0060b067b86e")]
internal interface ITfThreadMgr
{
    [PreserveSig]
    int Activate(out uint ptid);
    
    [PreserveSig]
    int Deactivate();
    
    [PreserveSig]
    int CreateDocumentMgr(out ITfDocumentMgr ppdim);
    
    [PreserveSig]
    int EnumDocumentMgrs(out IntPtr ppEnum);
    
    [PreserveSig]
    int GetFocus(out ITfDocumentMgr ppdimFocus);
    
    [PreserveSig]
    int SetFocus(ITfDocumentMgr pdimFocus);
    
    [PreserveSig]
    int AssociateFocus(IntPtr hwnd, ITfDocumentMgr pdimNew, out ITfDocumentMgr ppdimPrev);
    
    [PreserveSig]
    int IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool pfThreadFocus);
    
    [PreserveSig]
    int GetFunctionProvider(ref Guid clsid, out IntPtr ppFuncProv);
    
    [PreserveSig]
    int EnumFunctionProviders(out IntPtr ppEnum);
    
    [PreserveSig]
    int GetGlobalCompartment(out IntPtr ppCompMgr);
}

// ITfSource - Used to install and uninstall advise sinks
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7")]
internal interface ITfSource
{
    [PreserveSig]
    int AdviseSink(ref Guid riid, IntPtr punk, out uint pdwCookie);
    
    [PreserveSig]
    int UnadviseSink(uint dwCookie);
}

// ITfThreadMgrEventSink - Receives notifications about thread manager events
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("aa80e80e-2021-11d2-93e0-0060b067b86e")]
internal interface ITfThreadMgrEventSink
{
    [PreserveSig]
    int OnInitDocumentMgr(ITfDocumentMgr pdim);
    
    [PreserveSig]
    int OnUninitDocumentMgr(ITfDocumentMgr pdim);
    
    [PreserveSig]
    int OnSetFocus(ITfDocumentMgr pdimFocus, ITfDocumentMgr pdimPrevFocus);
    
    [PreserveSig]
    int OnPushContext(ITfContext pic);
    
    [PreserveSig]
    int OnPopContext(ITfContext pic);
}

// ITfDocumentMgr - Manages document contexts
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("aa80e7f4-2021-11d2-93e0-0060b067b86e")]
internal interface ITfDocumentMgr
{
    [PreserveSig]
    int CreateContext(
        uint tidOwner,
        uint dwFlags,
        IntPtr punk,
        out ITfContext ppic,
        out uint pecTextStore);
    
    [PreserveSig]
    int Push(ITfContext pic);
    
    [PreserveSig]
    int Pop(uint dwFlags);
    
    [PreserveSig]
    int GetTop(out ITfContext ppic);
    
    [PreserveSig]
    int GetBase(out ITfContext ppic);
    
    [PreserveSig]
    int EnumContexts(out IntPtr ppEnum);
}

// ITfContext - Represents input context
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("aa80e7fd-2021-11d2-93e0-0060b067b86e")]
internal interface ITfContext
{
    [PreserveSig]
    int RequestEditSession(
        uint tid,
        IntPtr pes,
        uint dwFlags,
        out int phrSession);
    
    [PreserveSig]
    int InWriteSession(out bool pfWriteSession);
    
    [PreserveSig]
    int GetSelection(
        uint ulIndex,
        uint ulCount,
        IntPtr pSelection,
        out uint pcFetched);
    
    [PreserveSig]
    int SetSelection(
        uint ulCount,
        IntPtr pSelection);
    
    [PreserveSig]
    int GetStart(out IntPtr ppStart);
    
    [PreserveSig]
    int GetEnd(out IntPtr ppEnd);
    
    [PreserveSig]
    int GetActiveView(out IntPtr ppView);
    
    [PreserveSig]
    int EnumViews(out IntPtr ppEnum);
    
    [PreserveSig]
    int GetStatus(out IntPtr pdcs);
    
    [PreserveSig]
    int GetProperty(ref Guid guidProp, out ITfProperty ppProp);
    
    [PreserveSig]
    int GetAppProperty(ref Guid guidProp, out IntPtr ppProp);
    
    [PreserveSig]
    int TrackProperties(
        IntPtr prgProp,
        uint cProp,
        IntPtr prgAppProp,
        uint cAppProp,
        out IntPtr ppProperty);
    
    [PreserveSig]
    int EnumProperties(out IntPtr ppEnum);
    
    [PreserveSig]
    int GetDocumentMgr(out ITfDocumentMgr ppDm);
    
    [PreserveSig]
    int CreateRangeBackup(
        uint ec,
        IntPtr pRange,
        out IntPtr ppBackup);
    
    [PreserveSig]
    int GetAttribute(ref Guid guidAttribute, out ITfProperty ppProp);
}

// ITfProperty - Represents a property in TSF
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("e2449660-9542-11d2-bf46-00105a2799b5")]
internal interface ITfProperty
{
    [PreserveSig]
    int GetType(out Guid pguid);
    
    [PreserveSig]
    int EnumRanges(
        uint ec,
        out IntPtr ppEnum,
        IntPtr pTargetRange);
    
    [PreserveSig]
    int GetValue(
        uint ec,
        IntPtr pRange,
        out object pvarValue);
    
    [PreserveSig]
    int GetContext(out ITfContext ppContext);
    
    [PreserveSig]
    int FindRange(
        uint ec,
        IntPtr pRange,
        out IntPtr ppRange,
        uint aPos);
    
    [PreserveSig]
    int SetValueStore(
        uint ec,
        IntPtr pRange,
        IntPtr pPropStore);
    
    [PreserveSig]
    int SetValue(
        uint ec,
        IntPtr pRange,
        ref object pvarValue);
    
    [PreserveSig]
    int Clear(
        uint ec,
        IntPtr pRange);
}

// TSF attribute GUIDs
internal static class TF_ATTRIBUTE_GUID
{
    public static readonly Guid TF_ATTR_INPUT_SCOPE = new Guid("632fb373-1891-4e6e-9e98-015dc935c3a8");
    public static readonly Guid TF_ATTR_TARGET_CONVERTED = new Guid("b23b2595-b1dc-4a4e-9c4c-e094aa5b9d5a");
}

// TSF property GUIDs
internal static class TF_PROPERTY_GUID
{
    public static readonly Guid TFPROP_TEXTOWNER = new Guid("f11ee2b1-e907-4eca-bf3f-a5cb93c43840");
    public static readonly Guid TFPROP_COMPOSING = new Guid("e7e1a2cc-3c2e-4876-9ce4-4d6f5b0e7c65");
}

#endregion