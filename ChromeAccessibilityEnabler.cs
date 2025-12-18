using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard;

/// <summary>
/// Enables Chrome/Chromium accessibility support by responding to WM_GETOBJECT messages.
/// Chrome detects screen readers by sending EVENT_SYSTEM_ALERT with custom object ID 1,
/// then checking if any application responds to WM_GETOBJECT for that ID.
/// </summary>
public class ChromeAccessibilityEnabler : IDisposable
{
    private const int WM_GETOBJECT = 0x003D;
    private const int OBJID_CLIENT = -4;
    
    // Custom object ID used by Chrome to detect assistive technology
    private const int CHROME_DETECTION_OBJID = 1;
    
    private readonly IntPtr _windowHandle;
    private IntPtr _subclassId = IntPtr.Zero;
    private bool _isDisposed = false;
    
    // Delegate for window procedure
    private SUBCLASSPROC _wndProcDelegate;
    
    private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);
    
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);
    
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);
    
    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    
    [DllImport("oleacc.dll")]
    private static extern IntPtr LresultFromObject(ref Guid riid, IntPtr wParam, IntPtr pAcc);
    
    // IAccessible GUID
    private static readonly Guid IID_IAccessible = new Guid("{618736E0-3C3D-11CF-810C-00AA00389B71");
    
    public ChromeAccessibilityEnabler(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        
        // Keep reference to prevent GC
        _wndProcDelegate = WndProc;
        
        // Generate unique ID for subclass
        _subclassId = new IntPtr(GetHashCode());
        
        // Install window subclass to intercept messages
        bool success = SetWindowSubclass(_windowHandle, _wndProcDelegate, _subclassId, IntPtr.Zero);
        
        if (success)
        {
            Logger.Info("✅ Chrome accessibility enabler installed - will respond to WM_GETOBJECT");
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Error($"Failed to install Chrome accessibility enabler. Error: {error}");
        }
    }
    
    /// <summary>
    /// Window procedure to handle WM_GETOBJECT messages
    /// </summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_GETOBJECT)
        {
            int objId = unchecked((int)lParam.ToInt64());
            
            // Chrome's custom detection object ID
            if (objId == CHROME_DETECTION_OBJID)
            {
                Logger.Debug($"🎯 Received WM_GETOBJECT with Chrome detection ID (1) - responding to enable Chrome accessibility");
                
                try
                {
                    // Create minimal IAccessible stub
                    var accStub = new MinimalAccessibleStub();
                    
                    // Marshal to COM and return
                    IntPtr pAcc = Marshal.GetIUnknownForObject(accStub);
                    IntPtr result = LresultFromObject(ref IID_IAccessible, wParam, pAcc);
                    
                    Marshal.Release(pAcc);
                    
                    Logger.Info("✅ Responded to Chrome - accessibility should now be enabled");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Error("Error responding to Chrome WM_GETOBJECT", ex);
                }
            }
            else if (objId == OBJID_CLIENT)
            {
                Logger.Debug($"Received WM_GETOBJECT for OBJID_CLIENT - passing to default handler");
            }
        }
        
        // Pass to default handler
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
    
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        try
        {
            if (_subclassId != IntPtr.Zero)
            {
                bool success = RemoveWindowSubclass(_windowHandle, _wndProcDelegate, _subclassId);
                
                if (success)
                {
                    Logger.Info("Chrome accessibility enabler removed");
                }
                else
                {
                    Logger.Warning("Failed to remove Chrome accessibility enabler subclass");
                }
                
                _subclassId = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error removing Chrome accessibility enabler", ex);
        }
        
        GC.SuppressFinalize(this);
    }
    
    ~ChromeAccessibilityEnabler()
    {
        Dispose();
    }
}

/// <summary>
/// Minimal IAccessible implementation just to satisfy Chrome's detection
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal class MinimalAccessibleStub : NativeMethods.IAccessible
{
    // Only implement what's absolutely necessary for Chrome detection
    
    public NativeMethods.IAccessible accParent => null;
    public int accChildCount => 0;
    
    public object get_accChild(object varChild) => null;
    public string get_accName(object varChild) => "Virtual Keyboard Accessibility Stub";
    public string get_accValue(object varChild) => null;
    public string get_accDescription(object varChild) => "Enables Chrome accessibility detection";
    
    public object get_accRole(object varChild) => NativeMethods.ROLE_SYSTEM_CLIENT;
    
    public object get_accState(object varChild)
    {
        return NativeMethods.STATE_SYSTEM_FOCUSABLE;
    }
    
    public string get_accHelp(object varChild) => null;
    public int get_accHelpTopic(out string pszHelpFile, object varChild)
    {
        pszHelpFile = null;
        return 0;
    }
    
    public string get_accKeyboardShortcut(object varChild) => null;
    public object accFocus => null;
    public object accSelection => null;
    public string get_accDefaultAction(object varChild) => null;
    
    public void accSelect(int flagsSelect, object varChild) { }
    
    public void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, object varChild)
    {
        pxLeft = 0;
        pyTop = 0;
        pcxWidth = 0;
        pcyHeight = 0;
    }
    
    public object accNavigate(int navDir, object varStart) => null;
    public object accHitTest(int xLeft, int yTop) => null;
    public void accDoDefaultAction(object varChild) { }
    public void set_accName(object varChild, string pszName) { }
    public void set_accValue(object varChild, string pszValue) { }
}