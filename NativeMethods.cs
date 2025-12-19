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
    public const int ROLE_SYSTEM_DOCUMENT = 0x0F;
    public const int ROLE_SYSTEM_CLIENT = 0x0A;
    public const int ROLE_SYSTEM_COMBOBOX = 0x2E;
    public const int ROLE_SYSTEM_PANE = 0x10;
    public const int ROLE_SYSTEM_CARET = 0x07;

    // Standard MSAA States
    public const int STATE_SYSTEM_FOCUSED = 0x00000004;
    public const int STATE_SYSTEM_FOCUSABLE = 0x00100000;
    public const int STATE_SYSTEM_READONLY = 0x00000040;
    public const int STATE_SYSTEM_PROTECTED = 0x20000000;
    public const int STATE_SYSTEM_UNAVAILABLE = 0x00000001;

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

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    public const uint GA_PARENT = 1;
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, StringBuilder lpFilename, int nSize);

    public const uint PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint PROCESS_VM_READ = 0x0010;

    // GetCurrentInputMessageSource API - Windows 8+
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCurrentInputMessageSource(out INPUT_MESSAGE_SOURCE inputMessageSource);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT_MESSAGE_SOURCE
    {
        public INPUT_MESSAGE_DEVICE_TYPE deviceType;
        public INPUT_MESSAGE_ORIGIN_ID originId;
    }

    public enum INPUT_MESSAGE_DEVICE_TYPE
    {
        IMDT_UNAVAILABLE = 0x00000000,
        IMDT_KEYBOARD = 0x00000001,
        IMDT_MOUSE = 0x00000002,
        IMDT_TOUCH = 0x00000004,
        IMDT_PEN = 0x00000008,
        IMDT_TOUCHPAD = 0x00000010,
    }

    public enum INPUT_MESSAGE_ORIGIN_ID
    {
        IMO_UNAVAILABLE = 0x00000000,
        IMO_HARDWARE = 0x00000001,
        IMO_INJECTED = 0x00000002,
        IMO_SYSTEM = 0x00000004,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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