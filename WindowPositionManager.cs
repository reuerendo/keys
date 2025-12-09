using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard;

/// <summary>
/// Управляет позиционированием окна клавиатуры на экране
/// </summary>
public class WindowPositionManager
{
    // Win32 Constants
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_NOZORDER = 0x0004;
    private const int HWND_TOPMOST = -1;
    
    // Отступ от панели задач в пикселях
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
    /// Позиционирует окно внизу экрана по центру, над панелью задач
    /// </summary>
    public void PositionWindow()
    {
        try
        {
            // Получаем размеры окна
            if (!GetWindowRect(_hwnd, out RECT windowRect))
            {
                Logger.Error("Failed to get window rect");
                return;
            }

            int windowWidth = windowRect.Width;
            int windowHeight = windowRect.Height;

            Logger.Info($"Window size: {windowWidth}x{windowHeight}");

            // Получаем монитор, на котором находится окно
            IntPtr hMonitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            
            MONITORINFO monitorInfo = new MONITORINFO();
            monitorInfo.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                Logger.Error("Failed to get monitor info");
                return;
            }

            // Получаем рабочую область (без панели задач)
            RECT workArea = monitorInfo.rcWork;
            Logger.Info($"Work area: Left={workArea.Left}, Top={workArea.Top}, Right={workArea.Right}, Bottom={workArea.Bottom}");

            // Получаем позицию панели задач
            APPBARDATA taskbarData = new APPBARDATA();
            taskbarData.cbSize = Marshal.SizeOf(typeof(APPBARDATA));
            IntPtr result = SHAppBarMessage(ABM_GETTASKBARPOS, ref taskbarData);
            
            int taskbarHeight = 0;
            bool taskbarAtBottom = true;

            if (result != IntPtr.Zero)
            {
                // Определяем высоту панели задач
                taskbarHeight = taskbarData.rc.Height;
                
                // Проверяем, где находится панель задач
                if (taskbarData.uEdge == 1) // ABE_TOP
                {
                    taskbarAtBottom = false;
                    Logger.Info($"Taskbar at top, height: {taskbarHeight}");
                }
                else if (taskbarData.uEdge == 3) // ABE_BOTTOM
                {
                    taskbarAtBottom = true;
                    Logger.Info($"Taskbar at bottom, height: {taskbarHeight}");
                }
                else if (taskbarData.uEdge == 0 || taskbarData.uEdge == 2) // ABE_LEFT or ABE_RIGHT
                {
                    Logger.Info($"Taskbar at side, using work area");
                    taskbarHeight = 0; // Используем рабочую область
                }
            }
            else
            {
                Logger.Warning("Could not get taskbar position, using default");
                // Предполагаем стандартную высоту панели задач
                taskbarHeight = 48;
            }

            // Получаем DPI для корректного масштабирования
            uint dpi = GetDpiForWindow(_hwnd);
            float scalingFactor = dpi / 96f;
            int scaledOffset = (int)(TASKBAR_OFFSET * scalingFactor);

            // Вычисляем позицию X (центр экрана)
            int screenWidth = workArea.Right - workArea.Left;
            int posX = workArea.Left + (screenWidth - windowWidth) / 2;

            // Вычисляем позицию Y (внизу экрана, над панелью задач)
            int posY;
            if (taskbarAtBottom)
            {
                // Панель задач внизу - позиционируем над ней
                posY = workArea.Bottom - windowHeight - scaledOffset;
            }
            else
            {
                // Панель задач не внизу - используем нижнюю границу рабочей области
                posY = workArea.Bottom - windowHeight - scaledOffset;
            }

            Logger.Info($"Positioning window at X={posX}, Y={posY} (DPI: {dpi}, Scale: {scalingFactor})");

            // Устанавливаем позицию окна
            bool success = SetWindowPos(
                _hwnd,
                new IntPtr(HWND_TOPMOST),
                posX,
                posY,
                0, // Не меняем ширину
                0, // Не меняем высоту
                SWP_NOACTIVATE | SWP_NOZORDER | 0x0001 // SWP_NOSIZE
            );

            if (success)
            {
                Logger.Info("Window positioned successfully");
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
    /// Центрирует окно горизонтально без изменения вертикальной позиции
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
    /// Получает текущую позицию окна
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