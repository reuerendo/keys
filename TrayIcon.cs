using System;
using System.Runtime.InteropServices;
using System.IO;

namespace VirtualKeyboard;

/// <summary>
/// Manages system tray icon with context menu and event handling
/// </summary>
public class TrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const int IDI_APPLICATION = 32512;
    private const int IDI_INFORMATION = 32516;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_DEFAULTSIZE = 0x00000040;

    private const int MENU_SHOW = 1000;
    private const int MENU_SETTINGS = 1001;
    private const int MENU_EXIT = 1002;

    private const uint NOTIFYICON_VERSION_4 = 4;

    #region Win32 Structures and Imports

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    #endregion

    private IntPtr _hwnd;
    private NOTIFYICONDATA _notifyIconData;
    private bool _isIconAdded;
    private TrayIconMessageWindow _messageWindow;
    private IntPtr _hIcon;

    public event EventHandler ShowRequested;
    public event EventHandler ToggleVisibilityRequested;
    public event EventHandler SettingsRequested;
    public event EventHandler ExitRequested;

    public TrayIcon(IntPtr windowHandle, string tooltip = "Virtual Keyboard")
    {
        _hwnd = windowHandle;
        _messageWindow = new TrayIconMessageWindow(this);
        _hIcon = LoadTrayIcon();

        _notifyIconData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _messageWindow.Handle,
            uID = 1,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tooltip,
            uVersion = NOTIFYICON_VERSION_4,
            guidItem = Guid.NewGuid()
        };

        Logger.Info($"TrayIcon initialized. Structure size: {_notifyIconData.cbSize}, Icon: 0x{_hIcon:X}, Window: 0x{_messageWindow.Handle:X}");
    }

    /// <summary>
    /// Load tray icon from file or use default system icon
    /// </summary>
    private IntPtr LoadTrayIcon()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string iconPath = Path.Combine(appDir, "tray_icon.ico");

        if (File.Exists(iconPath))
        {
            Logger.Info($"Found custom icon file: {iconPath}");
            IntPtr hIcon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
            if (hIcon != IntPtr.Zero)
                return hIcon;
        }

        IntPtr hInstance = GetModuleHandle(null);
        IntPtr hIcon2 = LoadIcon(hInstance, IDI_APPLICATION);
        if (hIcon2 != IntPtr.Zero)
            return hIcon2;

        return LoadIcon(IntPtr.Zero, IDI_INFORMATION);
    }

    /// <summary>
    /// Show the tray icon
    /// </summary>
    public void Show()
    {
        if (!_isIconAdded)
        {
            bool result = Shell_NotifyIcon(NIM_ADD, ref _notifyIconData);

            if (result)
            {
                _isIconAdded = true;
                Shell_NotifyIcon(NIM_MODIFY, ref _notifyIconData);
            }
        }
    }

    /// <summary>
    /// Hide the tray icon
    /// </summary>
    public void Hide()
    {
        if (_isIconAdded)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _notifyIconData);
            _isIconAdded = false;
        }
    }

    /// <summary>
    /// Update tooltip text
    /// </summary>
    public void UpdateTooltip(string tooltip)
    {
        if (_isIconAdded)
        {
            _notifyIconData.szTip = tooltip;
            Shell_NotifyIcon(NIM_MODIFY, ref _notifyIconData);
        }
    }

    /// <summary>
    /// Handle tray icon mouse messages
    /// </summary>
    private void OnTrayIconMessage(int message)
    {
        switch (message)
        {
            case WM_LBUTTONUP:
                OnLeftClick();
                break;

            case WM_RBUTTONUP:
                OnRightClick();
                break;
        }
    }

    /// <summary>
    /// Handle left click on tray icon - toggle visibility
    /// Focus restoration is handled by WindowVisibilityManager
    /// </summary>
    private void OnLeftClick()
    {
        Logger.Debug("Tray icon left click - toggling visibility");
        ToggleVisibilityRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Handle right click on tray icon - show context menu
    /// Context menu needs focus restoration as it's blocking/synchronous
    /// </summary>
    private void OnRightClick()
    {
        Logger.Debug("Tray icon right click - showing context menu");
        FocusRestorer.CaptureCurrentFocus();
        ShowContextMenu();
        FocusRestorer.RestoreFocus();
    }

    /// <summary>
    /// Show context menu at cursor position
    /// </summary>
    private void ShowContextMenu()
    {
        GetCursorPos(out POINT pt);

        IntPtr hMenu = CreatePopupMenu();

        AppendMenu(hMenu, 0, MENU_SHOW, "Показать");
        AppendMenu(hMenu, 0, MENU_SETTINGS, "Настройки");
        AppendMenu(hMenu, 0x800, 0, null); // Separator
        AppendMenu(hMenu, 0, MENU_EXIT, "Выход");

        SetForegroundWindow(_messageWindow.Handle);

        uint cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _messageWindow.Handle, IntPtr.Zero);

        DestroyMenu(hMenu);

        HandleMenuCommand(cmd);
    }

    /// <summary>
    /// Handle menu command selection
    /// </summary>
    private void HandleMenuCommand(uint cmd)
    {
        switch (cmd)
        {
            case MENU_SHOW:
                Logger.Info("Tray menu: Show requested");
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MENU_SETTINGS:
                Logger.Info("Tray menu: Settings requested");
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MENU_EXIT:
                Logger.Info("Tray menu: Exit requested");
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        Hide();
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
        _messageWindow?.Dispose();
    }

    #region TrayIconMessageWindow

    /// <summary>
    /// Hidden message-only window for receiving tray icon notifications
    /// </summary>
    private class TrayIconMessageWindow : IDisposable
    {
        private const string WINDOW_CLASS_NAME = "VirtualKeyboardTrayIconWindow";

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        private IntPtr _hwnd;
        private WndProc _wndProcDelegate;
        private TrayIcon _parent;

        public IntPtr Handle => _hwnd;

        public TrayIconMessageWindow(TrayIcon parent)
        {
            _parent = parent;
            _wndProcDelegate = WindowProc;

            IntPtr hInstance = GetModuleHandle(null);

            WNDCLASSEX wndClass = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                lpszClassName = WINDOW_CLASS_NAME
            };

            RegisterClassEx(ref wndClass);

            _hwnd = CreateWindowEx(0, WINDOW_CLASS_NAME, "TrayIconWindow", 0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON)
            {
                int message = lParam.ToInt32() & 0xFFFF;
                _parent.OnTrayIconMessage(message);
                return IntPtr.Zero;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
    }

    #endregion
}

#region FocusRestorer

/// <summary>
/// Helper class to capture and restore focus when showing context menus
/// Used ONLY for synchronous/blocking operations like context menus
/// For keyboard show/hide, use WindowVisibilityManager's focus restoration
/// </summary>
public static class FocusRestorer
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static IntPtr _prevFocus = IntPtr.Zero;
    private static IntPtr _prevForeground = IntPtr.Zero;

    /// <summary>
    /// Capture current focus state before showing blocking UI (context menu)
    /// </summary>
    public static void CaptureCurrentFocus()
    {
        try
        {
            _prevForeground = GetForegroundWindow();
            Logger.Debug($"Captured foreground window: 0x{_prevForeground:X}");

            if (_prevForeground == IntPtr.Zero)
            {
                _prevFocus = IntPtr.Zero;
                return;
            }

            uint thisThread = GetCurrentThreadId();
            uint fgThread = GetWindowThreadProcessId(_prevForeground, out _);

            bool attached = false;
            try
            {
                if (thisThread != fgThread)
                {
                    attached = AttachThreadInput(thisThread, fgThread, true);
                }

                if (attached || thisThread == fgThread)
                {
                    _prevFocus = GetFocus();
                    Logger.Debug($"Captured focused control: 0x{_prevFocus:X}");
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(thisThread, fgThread, false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error capturing focus", ex);
            _prevFocus = IntPtr.Zero;
            _prevForeground = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Restore previously captured focus after blocking UI closes
    /// </summary>
    public static void RestoreFocus()
    {
        try
        {
            if (_prevForeground == IntPtr.Zero)
            {
                Logger.Debug("No previous foreground to restore");
                return;
            }

            uint thisThread = GetCurrentThreadId();
            uint fgThread = GetWindowThreadProcessId(_prevForeground, out _);

            bool attached = false;
            try
            {
                if (thisThread != fgThread)
                {
                    attached = AttachThreadInput(thisThread, fgThread, true);
                }

                if ((attached || thisThread == fgThread) && _prevFocus != IntPtr.Zero)
                {
                    SetFocus(_prevFocus);
                    Logger.Debug($"Restored focus to control: 0x{_prevFocus:X}");
                }
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(thisThread, fgThread, false);
                }
            }

            _prevFocus = IntPtr.Zero;
            _prevForeground = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Logger.Error("Error restoring focus", ex);
        }
    }
}

#endregion