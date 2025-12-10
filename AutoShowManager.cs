using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard display using Windows Event Hooks + IAccessible (MSAA)
/// Monitors focus changes system-wide and shows keyboard for text input controls
/// </summary>
public class AutoShowManager : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private bool _disposed;
    
    private IntPtr _hookHandle;
    private WinEventDelegate _hookDelegate;
    
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
        Logger.Info("AutoShowManager created (MSAA-based)");
    }

    /// <summary>
    /// Start Windows Event Hook monitoring
    /// </summary>
    private void StartMonitoring()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Logger.Warning("Event hook already active");
            return;
        }

        try
        {
            // Create delegate (no need to pin, .NET keeps delegates alive while hook is active)
            _hookDelegate = new WinEventDelegate(WinEventProc);

            // Set up hook for focus events
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_FOCUS,              // eventMin
                EVENT_OBJECT_FOCUS,              // eventMax
                IntPtr.Zero,                     // hmodWinEventProc
                _hookDelegate,                   // pfnWinEventProc
                0,                               // idProcess (0 = all processes)
                0,                               // idThread (0 = all threads)
                WINEVENT_OUTOFCONTEXT);          // flags

            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"Failed to set Windows event hook (Error: {error})");
                return;
            }

            Logger.Info($"Windows event hook registered successfully (Handle=0x{_hookHandle:X})");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start event hook monitoring", ex);
        }
    }

    /// <summary>
    /// Stop Windows Event Hook monitoring
    /// </summary>
    private void StopMonitoring()
    {
        try
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
                Logger.Info("Windows event hook unregistered");
            }

            _hookDelegate = null;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to stop event hook monitoring", ex);
        }
    }

    /// <summary>
    /// Callback for Windows events
    /// </summary>
    private void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!_isEnabled || _disposed)
            return;

        try
        {
            // Check cooldown period
            if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
            {
                return;
            }

            // Ignore our own window
            if (hwnd == _keyboardWindowHandle)
            {
                return;
            }

            // Ignore invalid handles
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            // Check if focused element is a text input
            if (IsTextInputElement(hwnd, idObject, idChild))
            {
                Logger.Info($"Text input focused (hwnd=0x{hwnd:X}, obj={idObject}, child={idChild}) - showing keyboard");
                ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in WinEventProc: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if element is a text input control using IAccessible (MSAA)
    /// </summary>
    private bool IsTextInputElement(IntPtr hwnd, int idObject, int idChild)
    {
        IntPtr pAccessible = IntPtr.Zero;
        
        try
        {
            // Get IAccessible interface for the element
            object varChild;
            int hr = AccessibleObjectFromEvent(hwnd, idObject, (uint)idChild, out pAccessible, out varChild);
            
            if (hr != 0 || pAccessible == IntPtr.Zero)
            {
                // Fallback: check window class
                return IsEditWindowClass(hwnd);
            }

            // Get IAccessible object
            IAccessible accessible = Marshal.GetObjectForIUnknown(pAccessible) as IAccessible;
            if (accessible == null)
            {
                return IsEditWindowClass(hwnd);
            }

            try
            {
                // Use CHILDID_SELF for checking
                object self = CHILDID_SELF;
                
                // Get role of the accessible object
                object role = accessible.get_accRole(self);
                
                if (role is int roleInt)
                {
                    // Check if role indicates text input
                    // ROLE_SYSTEM_TEXT (42) = editable text
                    // ROLE_SYSTEM_DOCUMENT (15) = document with editable content
                    if (roleInt == ROLE_SYSTEM_TEXT)
                    {
                        Logger.Debug($"MSAA: Text control detected (Role={roleInt})");
                        return true;
                    }

                    // Get state to check if editable
                    try
                    {
                        object state = accessible.get_accState(self);
                        if (state is int stateInt)
                        {
                            // STATE_SYSTEM_FOCUSABLE (0x100000) + check if it's editable
                            bool isFocusable = (stateInt & STATE_SYSTEM_FOCUSABLE) != 0;
                            bool isReadOnly = (stateInt & STATE_SYSTEM_READONLY) != 0;
                            
                            if (isFocusable && !isReadOnly)
                            {
                                // Additional check: is it an edit control?
                                if (roleInt == ROLE_SYSTEM_TEXT || 
                                    roleInt == ROLE_SYSTEM_DOCUMENT ||
                                    IsEditWindowClass(hwnd))
                                {
                                    Logger.Debug($"MSAA: Editable control detected (Role={roleInt}, State=0x{stateInt:X})");
                                    return true;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // If state check fails, fallback to class check
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(accessible);
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in IsTextInputElement: {ex.Message}");
            // Fallback to class name check
            return IsEditWindowClass(hwnd);
        }
        finally
        {
            if (pAccessible != IntPtr.Zero)
            {
                Marshal.Release(pAccessible);
            }
        }
    }

    /// <summary>
    /// Check if window class indicates a text input control
    /// </summary>
    private bool IsEditWindowClass(IntPtr hwnd)
    {
        try
        {
            StringBuilder className = new StringBuilder(256);
            if (GetClassName(hwnd, className, className.Capacity) == 0)
            {
                return false;
            }

            string classStr = className.ToString();

            // Check against known text input classes
            if (classStr.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                classStr.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                classStr.Contains("Edit"))
            {
                Logger.Debug($"Text input class detected: {classStr}");
                return true;
            }

            return false;
        }
        catch
        {
            return false;
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

    #region P/Invoke, COM Interfaces and Constants

    // Delegate for event hook callback
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromEvent(
        IntPtr hwnd,
        int dwObjectID,
        uint dwChildID,
        out IntPtr ppacc,
        out object pvarChild);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // IAccessible interface (minimal definition for MSAA)
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAccessible
    {
        [PreserveSig]
        int get_accParent(out object ppdispParent);
        
        [PreserveSig]
        int get_accChildCount(out int pcountChildren);
        
        [PreserveSig]
        int get_accChild(object varChild, out object ppdispChild);
        
        [PreserveSig]
        int get_accName(object varChild, out string pszName);
        
        [PreserveSig]
        int get_accValue(object varChild, out string pszValue);
        
        [PreserveSig]
        int get_accDescription(object varChild, out string pszDescription);
        
        [PreserveSig]
        int get_accRole(object varChild, out object pvarRole);
        
        [PreserveSig]
        int get_accState(object varChild, out object pvarState);
        
        [PreserveSig]
        int get_accHelp(object varChild, out string pszHelp);
        
        [PreserveSig]
        int get_accHelpTopic(out string pszHelpFile, object varChild, out int pidTopic);
        
        [PreserveSig]
        int get_accKeyboardShortcut(object varChild, out string pszKeyboardShortcut);
        
        [PreserveSig]
        int get_accFocus(out object pvarChild);
        
        [PreserveSig]
        int get_accSelection(out object pvarChildren);
        
        [PreserveSig]
        int get_accDefaultAction(object varChild, out string pszDefaultAction);
        
        [PreserveSig]
        int accSelect(int flagsSelect, object varChild);
        
        [PreserveSig]
        int accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, object varChild);
        
        [PreserveSig]
        int accNavigate(int navDir, object varStart, out object pvarEndUpAt);
        
        [PreserveSig]
        int accHitTest(int xLeft, int yTop, out object pvarChild);
        
        [PreserveSig]
        int accDoDefaultAction(object varChild);
        
        [PreserveSig]
        int put_accName(object varChild, string szName);
        
        [PreserveSig]
        int put_accValue(object varChild, string szValue);
    }

    // Event constants
    private const uint EVENT_OBJECT_FOCUS = 0x8005;

    // Flags
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // MSAA constants
    private const int CHILDID_SELF = 0;
    private const int ROLE_SYSTEM_TEXT = 42;
    private const int ROLE_SYSTEM_DOCUMENT = 15;
    private const int STATE_SYSTEM_FOCUSABLE = 0x100000;
    private const int STATE_SYSTEM_READONLY = 0x40;

    #endregion
}