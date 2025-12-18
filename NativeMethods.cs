using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualKeyboard;

/// <summary>
/// Native Win32 API and MSAA definitions.
/// Contains manual definition of IAccessible to avoid assembly dependencies.
/// </summary>
internal static class NativeMethods
{
    public const uint EVENT_OBJECT_FOCUS = 0x8005;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const int OBJID_CLIENT = -4;

    // Standard MSAA Roles
    public const int ROLE_SYSTEM_TEXT = 0x2A;
    public const int ROLE_SYSTEM_DOCUMENT = 0x0F; // For Word/Browsers
    public const int ROLE_SYSTEM_CLIENT = 0x0A;   // Generic client area

    // Standard MSAA States
    public const int STATE_SYSTEM_FOCUSED = 0x00000004;
    public const int STATE_SYSTEM_PROTECTED = 0x20000000; // Password field

    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromEvent(IntPtr hwnd, int dwId, int dwChildId, out IAccessible ppacc, [MarshalAs(UnmanagedType.Struct)] out object pvarChild);

    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromPoint(POINT ptScreen, out IAccessible ppacc, [MarshalAs(UnmanagedType.Struct)] out object pvarChild);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromAccessibleObject(IAccessible pacc);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    // --- Manual IAccessible Definition to avoid Reference Errors ---

    [ComImport()]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IAccessible
    {
        [DispId(-5000)] IAccessible accParent { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
        [DispId(-5001)] int accChildCount { get; }
        [DispId(-5002)] object get_accChild(object varChild);
        [DispId(-5003)] string get_accName(object varChild);
        [DispId(-5004)] string get_accValue(object varChild);
        [DispId(-5005)] string get_accDescription(object varChild);
        [DispId(-5006)] object get_accRole(object varChild);
        [DispId(-5007)] object get_accState(object varChild);
        [DispId(-5008)] string get_accHelp(object varChild);
        [DispId(-5009)] int get_accHelpTopic(out string pszHelpFile, object varChild);
        [DispId(-5010)] string get_accKeyboardShortcut(object varChild);
        [DispId(-5011)] object accFocus { get; }
        [DispId(-5012)] object accSelection { get; }
        [DispId(-5013)] string get_accDefaultAction(object varChild);
        [DispId(-5014)] void accSelect(int flagsSelect, object varChild);
        [DispId(-5015)] void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, object varChild);
        [DispId(-5016)] object accNavigate(int navDir, object varStart);
        [DispId(-5017)] object accHitTest(int xLeft, int yTop);
        [DispId(-5018)] void accDoDefaultAction(object varChild);
        [DispId(-5003)] void set_accName(object varChild, string pszName);
        [DispId(-5004)] void set_accValue(object varChild, string pszValue);
    }
}