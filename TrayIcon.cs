using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard;

/// <summary>
/// Manages system tray icon and notifications for the application
/// </summary>
public class TrayIcon : IDisposable
{
    // Win32 Constants
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    
    private const int IDI_APPLICATION = 32512;
    
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    
    // Menu item IDs
    private const int MENU_SHOW = 1000;
    private const int MENU_SETTINGS = 1001;
    private const int MENU_EXIT = 1002;

    // Structures
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
    }

    // P/Invoke
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private IntPtr _hwnd;
    private NOTIFYICONDATA _notifyIconData;
    private bool _isIconAdded;
    private TrayIconMessageWindow _messageWindow;

    public event EventHandler ShowRequested;
    public event EventHandler SettingsRequested;
    public event EventHandler ExitRequested;

    public TrayIcon(IntPtr windowHandle, string tooltip = "Virtual Keyboard")
    {
        _hwnd = windowHandle;
        
        // Create message-only window for receiving tray icon messages
        _messageWindow = new TrayIconMessageWindow(this);
        
        // Load application icon
        IntPtr hIcon = LoadIcon(IntPtr.Zero, IDI_APPLICATION);
        
        // Initialize NOTIFYICONDATA structure
        _notifyIconData = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf(typeof(NOTIFYICONDATA)),
            hWnd = _messageWindow.Handle,
            uID = 1,
            uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = tooltip
        };
        
        Logger.Info("TrayIcon initialized");
    }

    public void Show()
    {
        if (!_isIconAdded)
        {
            bool result = Shell_NotifyIcon(NIM_ADD, ref _notifyIconData);
            _isIconAdded = result;
            
            if (result)
            {
                Logger.Info("Tray icon added successfully");
            }
            else
            {
                Logger.Error("Failed to add tray icon");
            }
        }
    }

    public void Hide()
    {
        if (_isIconAdded)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _notifyIconData);
            _isIconAdded = false;
            Logger.Info("Tray icon removed");
        }
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_isIconAdded)
        {
            _notifyIconData.szTip = tooltip;
            Shell_NotifyIcon(NIM_MODIFY, ref _notifyIconData);
        }
    }

    private void OnTrayIconMessage(int message)
    {
        switch (message)
        {
            case WM_LBUTTONDBLCLK:
                Logger.Info("Tray icon double-clicked");
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;
                
            case WM_RBUTTONUP:
                Logger.Info("Tray icon right-clicked");
                ShowContextMenu();
                break;
        }
    }

    private void ShowContextMenu()
    {
        // Get cursor position
        GetCursorPos(out POINT pt);
        
        // Create popup menu
        IntPtr hMenu = CreatePopupMenu();
        
        if (hMenu != IntPtr.Zero)
        {
            // Add menu items
            AppendMenu(hMenu, 0, MENU_SHOW, "Показать");
            AppendMenu(hMenu, 0, MENU_SETTINGS, "Настройки");
            AppendMenu(hMenu, 0x800, 0, null); // MF_SEPARATOR
            AppendMenu(hMenu, 0, MENU_EXIT, "Выход");
            
            // Set foreground window to make menu work properly
            SetForegroundWindow(_messageWindow.Handle);
            
            // Show menu and get selected item
            uint cmd = TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _messageWindow.Handle, IntPtr.Zero);
            
            // Cleanup
            DestroyMenu(hMenu);
            
            // Handle menu selection
            HandleMenuCommand(cmd);
        }
    }

    private void HandleMenuCommand(uint cmd)
    {
        switch (cmd)
        {
            case MENU_SHOW:
                Logger.Info("Menu: Show selected");
                ShowRequested?.Invoke(this, EventArgs.Empty);
                break;
                
            case MENU_SETTINGS:
                Logger.Info("Menu: Settings selected");
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
                
            case MENU_EXIT:
                Logger.Info("Menu: Exit selected");
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    public void Dispose()
    {
        Hide();
        _messageWindow?.Dispose();
        Logger.Info("TrayIcon disposed");
    }

    // Hidden message-only window to receive tray icon messages
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
        
        [DllImport("kernel32.dll")]
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
            
            ushort atom = RegisterClassEx(ref wndClass);
            
            if (atom == 0)
            {
                Logger.Error($"Failed to register window class. Error: {Marshal.GetLastWin32Error()}");
            }
            
            // Create message-only window (HWND_MESSAGE = -3)
            _hwnd = CreateWindowEx(0, WINDOW_CLASS_NAME, "TrayIconWindow", 0, 0, 0, 0, 0, 
                new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
            
            if (_hwnd == IntPtr.Zero)
            {
                Logger.Error($"Failed to create message window. Error: {Marshal.GetLastWin32Error()}");
            }
            else
            {
                Logger.Info($"Message window created: 0x{_hwnd:X}");
            }
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
}