using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard;

/// <summary>
/// Manages keyboard window positioning on screen
/// </summary>
public class WindowPositionManager
{
    // Win32 Constants
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOZORDER = 0x0004;
    private const int HWND_TOPMOST = -1;
    
    // Offset from taskbar in pixels
    private const int TASKBAR_OFFSET = 10;

    // Structures
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    // P/Invoke
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint ABM_GETTASKBARPOS = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private IntPtr _hwnd;
    private Window _window;

    public WindowPositionManager(Window window, IntPtr hwnd)
    {
        _window = window;
        _hwnd = hwnd;
    }

	/// <summary>
    /// Positions window at bottom-center of screen, above taskbar
    /// </summary>
    public void PositionWindow(bool showWindow = false)
    {
        try
        {
            if (!GetWindowRect(_hwnd, out RECT windowRect))
            {
                Logger.Error("Failed to get window rect");
                return;
            }

            int windowWidth = windowRect.Width;
            int windowHeight = windowRect.Height;

            IntPtr hMonitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            
            MONITORINFO monitorInfo = new MONITORINFO();
            monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                Logger.Error("Failed to get monitor info");
                return;
            }

            RECT workArea = monitorInfo.rcWork;

            APPBARDATA taskbarData = new APPBARDATA();
            taskbarData.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            IntPtr result = SHAppBarMessage(ABM_GETTASKBARPOS, ref taskbarData);
            
            int taskbarHeight = 0;
            bool taskbarAtBottom = true;

            if (result != IntPtr.Zero)
            {
                taskbarHeight = taskbarData.rc.Height;
                
                if (taskbarData.uEdge == 1) // ABE_TOP
                {
                    taskbarAtBottom = false;
                }
                else if (taskbarData.uEdge == 3) // ABE_BOTTOM
                {
                    taskbarAtBottom = true;
                }
                else if (taskbarData.uEdge == 0 || taskbarData.uEdge == 2) // ABE_LEFT or ABE_RIGHT
                {
                    taskbarHeight = 0; //
                }
            }
            else
            {
                taskbarHeight = 48;
            }

            uint dpi = GetDpiForWindow(_hwnd);
            float scalingFactor = dpi / 96f;
            int scaledOffset = (int)(TASKBAR_OFFSET * scalingFactor);

            int screenWidth = workArea.Right - workArea.Left;
            int posX = workArea.Left + (screenWidth - windowWidth) / 2;

            int posY;
            if (taskbarAtBottom)
            {
                posY = workArea.Bottom - windowHeight - scaledOffset;
            }
            else
            {
                // Если таскбар сверху или сбоку, просто прижимаем к низу рабочей области
                posY = workArea.Bottom - windowHeight - scaledOffset;
            }

            Logger.Info($"Positioning window at X={posX}, Y={posY} (DPI: {dpi}, Scale: {scalingFactor})");

            uint uFlags = SWP_NOACTIVATE | 0x0001;

            if (showWindow)
            {
                uFlags |= 0x0040; // SWP_SHOWWINDOW
            }

            bool success = SetWindowPos(
                _hwnd,
                new IntPtr(HWND_TOPMOST),
                posX,
                posY,
                0, // Ширину не меняем
                0, // Высоту не меняем
                uFlags
            );

            if (success)
            {
                Logger.Info($"Window positioned successfully. Flags: 0x{uFlags:X}");
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"Failed to position window. Win32 Error: {error}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in PositionWindow", ex);
        }
    }

    /// <summary>
    /// Centers window horizontally without changing vertical position
    /// </summary>
    public void CenterHorizontally()
    {
        try
        {
            if (!GetWindowRect(_hwnd, out RECT windowRect))
            {
                return;
            }

            IntPtr hMonitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            MONITORINFO monitorInfo = new MONITORINFO();
            monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                return;
            }

            RECT workArea = monitorInfo.rcWork;
            int screenWidth = workArea.Right - workArea.Left;
            int windowWidth = windowRect.Width;
            int posX = workArea.Left + (screenWidth - windowWidth) / 2;

            SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                posX,
                windowRect.Top,
                0,
                0,
                SWP_NOACTIVATE | SWP_NOZORDER | 0x0001 // SWP_NOSIZE
            );
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in CenterHorizontally", ex);
        }
    }

    /// <summary>
    /// Gets current window position
    /// </summary>
    public (int X, int Y, int Width, int Height) GetWindowPosition()
    {
        if (GetWindowRect(_hwnd, out RECT rect))
        {
            return (rect.Left, rect.Top, rect.Width, rect.Height);
        }
        return (0, 0, 0, 0);
    }
}