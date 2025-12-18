using System;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility;

namespace VirtualKeyboard;

/// <summary>
/// Native Win32 API and MSAA definitions
/// </summary>
internal static class NativeMethods
{
    public const uint EVENT_OBJECT_FOCUS = 0x8005;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    public const int OBJID_CLIENT = -4;

    // Standard MSAA Roles
    public const int ROLE_SYSTEM_TEXT = 0x2A;
    public const int ROLE_SYSTEM_DOCUMENT = 0x0F; // For Word/Browsers
    public const int ROLE_SYSTEM_CLIENT = 0x0A;   // Generic client area (sometimes used for custom edits)

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
}