using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard;

/// <summary>
/// Enhanced Chrome/Chromium accessibility support with proactive tree building
/// Responds to WM_GETOBJECT and proactively stimulates accessibility tree creation
/// </summary>
public class ChromeAccessibilityEnabler : IDisposable
{
    private const int WM_GETOBJECT = 0x003D;
    private const int OBJID_CLIENT = -4;
    private const int CHROME_DETECTION_OBJID = 1;
    
    private readonly IntPtr _windowHandle;
    private IntPtr _subclassId = IntPtr.Zero;
    private bool _isDisposed = false;
    
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
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    
    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    private static readonly Guid IID_IAccessible = new Guid("{618736E0-3C3D-11CF-810C-00AA00389B71}");
    
    public ChromeAccessibilityEnabler(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        
        _wndProcDelegate = WndProc;
        _subclassId = new IntPtr(GetHashCode());
        
        bool success = SetWindowSubclass(_windowHandle, _wndProcDelegate, _subclassId, IntPtr.Zero);
        
        if (success)
        {
            Logger.Info("✅ Chrome accessibility enabler installed");
            
            // Proactively stimulate Chrome/Edge to build accessibility tree
            ProactivelyStimulateChromeAccessibility();
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Error($"Failed to install Chrome accessibility enabler. Error: {error}");
        }
    }
    
    /// <summary>
    /// Proactively send WM_GETOBJECT to Chrome render widgets to force accessibility tree creation
    /// This reduces the delay between click and focus event in Chrome/Edge
    /// </summary>
    private void ProactivelyStimulateChromeAccessibility()
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == _windowHandle)
                return;
            
            // Check if foreground window is Chrome/Edge
            uint pid = 0;
            GetWindowThreadProcessId(foreground, out pid);
            
            StringBuilder className = new StringBuilder(256);
            GetClassName(foreground, className, className.Capacity);
            string classStr = className.ToString().ToLowerInvariant();
            
            // Chrome/Edge main window classes
            if (classStr.Contains("chrome_widgetwin"))
            {
                Logger.Info($"🎯 Detected Chrome/Edge window - proactively stimulating accessibility");
                
                // Find Chrome_RenderWidgetHostHWND child window
                IntPtr renderWidget = FindChromeRenderWidget(foreground);
                
                if (renderWidget != IntPtr.Zero)
                {
                    // Send WM_GETOBJECT to render widget to force accessibility tree creation
                    Logger.Info("📡 Sending WM_GETOBJECT to Chrome render widget");
                    SendMessage(renderWidget, WM_GETOBJECT, IntPtr.Zero, new IntPtr(CHROME_DETECTION_OBJID));
                    
                    // Give Chrome a moment to process
                    System.Threading.Thread.Sleep(50);
                    
                    Logger.Info("✅ Chrome accessibility tree should now be ready");
                }
                else
                {
                    Logger.Debug("Chrome render widget not found");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error stimulating Chrome accessibility", ex);
        }
    }
    
    /// <summary>
    /// Find Chrome_RenderWidgetHostHWND child window
    /// </summary>
    private IntPtr FindChromeRenderWidget(IntPtr parentWindow)
    {
        // Common Chrome render widget class names
        string[] renderWidgetClasses = new[]
        {
            "Chrome_RenderWidgetHostHWND",
            "Chrome_RenderWidgetHostHWND1",  // Edge sometimes uses this
            "Intermediate D3D Window"
        };
        
        foreach (var className in renderWidgetClasses)
        {
            IntPtr child = FindWindowEx(parentWindow, IntPtr.Zero, className, null);
            if (child != IntPtr.Zero)
            {
                Logger.Debug($"Found render widget: {className}");
                return child;
            }
        }
        
        // Try recursive search
        return FindChildWindowByClass(parentWindow, "chrome_renderwidget");
    }
    
    /// <summary>
    /// Recursive search for child window by class substring
    /// </summary>
    private IntPtr FindChildWindowByClass(IntPtr parent, string classSubstring)
    {
        IntPtr child = IntPtr.Zero;
        
        do
        {
            child = FindWindowEx(parent, child, null, null);
            if (child != IntPtr.Zero)
            {
                StringBuilder className = new StringBuilder(256);
                GetClassName(child, className, className.Capacity);
                string classStr = className.ToString().ToLowerInvariant();
                
                if (classStr.Contains(classSubstring))
                {
                    return child;
                }
                
                // Recursive search
                IntPtr found = FindChildWindowByClass(child, classSubstring);
                if (found != IntPtr.Zero)
                {
                    return found;
                }
            }
        }
        while (child != IntPtr.Zero);
        
        return IntPtr.Zero;
    }
    
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WM_GETOBJECT)
        {
            int objId = unchecked((int)lParam.ToInt64());
            
            if (objId == CHROME_DETECTION_OBJID)
            {
                Logger.Debug($"🎯 WM_GETOBJECT with Chrome detection ID (1) - responding");
                
                try
                {
                    var accStub = new MinimalAccessibleStub();
                    IntPtr pAcc = Marshal.GetIUnknownForObject(accStub);
                    
                    Guid iidAccessible = IID_IAccessible;
                    IntPtr result = LresultFromObject(ref iidAccessible, wParam, pAcc);
                    
                    Marshal.Release(pAcc);
                    
                    Logger.Info("✅ Responded to Chrome - accessibility enabled");
                    return result;
                }
                catch (Exception ex)
                {
                    Logger.Error("Error responding to Chrome WM_GETOBJECT", ex);
                }
            }
            else if (objId == OBJID_CLIENT)
            {
                Logger.Debug($"WM_GETOBJECT for OBJID_CLIENT - passing to default handler");
            }
        }
        
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
    
    /// <summary>
    /// Call this when foreground window changes to proactively prepare accessibility
    /// </summary>
    public void OnForegroundWindowChanged(IntPtr newForegroundWindow)
    {
        if (_isDisposed || newForegroundWindow == IntPtr.Zero || newForegroundWindow == _windowHandle)
            return;
        
        try
        {
            StringBuilder className = new StringBuilder(256);
            GetClassName(newForegroundWindow, className, className.Capacity);
            string classStr = className.ToString().ToLowerInvariant();
            
            if (classStr.Contains("chrome_widgetwin"))
            {
                Logger.Debug("Foreground changed to Chrome/Edge - stimulating accessibility");
                ProactivelyStimulateChromeAccessibility();
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in OnForegroundWindowChanged: {ex.Message}");
        }
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
/// Minimal IAccessible stub for Chrome detection
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal class MinimalAccessibleStub : NativeMethods.IAccessible
{
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