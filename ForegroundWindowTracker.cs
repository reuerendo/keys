using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

namespace VirtualKeyboard;

/// <summary>
/// Tracks foreground window changes in real-time using Windows Event Hooks
/// Provides instant access to the last valid foreground window
/// </summary>
public class ForegroundWindowTracker : IDisposable
{
    #region Win32 API

    // Event hook constants
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Window style constants
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const uint GW_OWNER = 4;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;

    // Delegate for the event hook callback
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
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

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    private const uint GA_ROOT = 2;

    #endregion

    #region Ignored Windows Lists

    // System window classes to ignore (NEVER track these)
    private static readonly HashSet<string> AlwaysIgnoredClasses = new HashSet<string>
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Input.InputSite.WindowClass",
        "ForegroundStaging",
    };

    // Classes that require additional checks (title, process, etc.)
    private static readonly HashSet<string> ConditionallyIgnoredClasses = new HashSet<string>
    {
        "ApplicationFrameWindow",
    };

    private static readonly HashSet<string> IgnoredProcesses = new HashSet<string>
    {
        "ShellExperienceHost.exe",
        "SearchHost.exe",
        "StartMenuExperienceHost.exe",
        "TextInputHost.exe",
        "SystemSettings.exe",
        "LockApp.exe",
    };

    #endregion

    #region Fields

    private readonly IntPtr _keyboardWindowHandle;
    private readonly WinEventDelegate _hookDelegate;
    private IntPtr _hookHandle = IntPtr.Zero;
    private IntPtr _lastValidWindow = IntPtr.Zero;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;

    #endregion

    #region Events

    // Event for foreground window changes
    public event EventHandler<IntPtr> ForegroundWindowChanged;

    #endregion

    public ForegroundWindowTracker(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        
        _hookDelegate = new WinEventDelegate(WinEventProc);
        
        _hookHandle = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookDelegate,
            0,
            0,
            WINEVENT_OUTOFCONTEXT);

        if (_hookHandle == IntPtr.Zero)
        {
            Logger.Error("Failed to install foreground window event hook");
        }
        else
        {
            Logger.Info("ForegroundWindowTracker: Event hook installed successfully");
        }
    }

    /// <summary>
    /// Event hook callback - called whenever foreground window changes
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
        if (idObject != 0 || idChild != 0)
            return;

        if (!IsValidWindow(hwnd))
            return;

        if (ShouldIgnoreWindow(hwnd))
        {
            Logger.Debug($"Ignoring foreground change to: {GetWindowInfo(hwnd)}");
            return;
        }

        lock (_lockObject)
        {
            _lastValidWindow = hwnd;
            Logger.Info($"✓ Tracked valid foreground window: {GetWindowInfo(hwnd)}");
        }
        
        // Fire event for Chrome accessibility stimulation
        try
        {
            ForegroundWindowChanged?.Invoke(this, hwnd);
        }
        catch (Exception ex)
        {
            Logger.Error("Error in ForegroundWindowChanged event handler", ex);
        }
    }

    /// <summary>
    /// Get the last valid foreground window (before tray/keyboard/system windows)
    /// </summary>
    public IntPtr GetLastValidWindow()
    {
        lock (_lockObject)
        {
            if (_lastValidWindow != IntPtr.Zero && 
                IsWindow(_lastValidWindow) && 
                IsWindowVisible(_lastValidWindow))
            {
                return _lastValidWindow;
            }

            Logger.Warning("Last valid window is no longer valid");
            _lastValidWindow = IntPtr.Zero;
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Check if we have a valid tracked window
    /// </summary>
    public bool HasValidWindow()
    {
        lock (_lockObject)
        {
            return _lastValidWindow != IntPtr.Zero && 
                   IsWindow(_lastValidWindow) && 
                   IsWindowVisible(_lastValidWindow);
        }
    }

    /// <summary>
    /// Clear the tracked window (call only on app exit)
    /// </summary>
    public void ClearTrackedWindow()
    {
        lock (_lockObject)
        {
            _lastValidWindow = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Check if window handle is valid
    /// </summary>
    private bool IsValidWindow(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && IsWindow(hWnd);
    }

    /// <summary>
    /// Check if window should be ignored
    /// </summary>
    private bool ShouldIgnoreWindow(IntPtr hWnd)
    {
        if (!IsValidWindow(hWnd))
            return true;

        if (hWnd == _keyboardWindowHandle)
            return true;

        if (!IsWindowVisible(hWnd))
            return true;

        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        string classStr = className.ToString();

        if (AlwaysIgnoredClasses.Contains(classStr))
            return true;

        StringBuilder title = new StringBuilder(256);
        GetWindowText(hWnd, title, title.Capacity);
        string titleStr = title.ToString();

        uint processId;
        GetWindowThreadProcessId(hWnd, out processId);
        string processName = GetProcessName(processId);

        if (ConditionallyIgnoredClasses.Contains(classStr))
        {
            if (classStr == "ApplicationFrameWindow" && !string.IsNullOrWhiteSpace(titleStr))
            {
                Logger.Debug($"Accepting UWP app: Title='{titleStr}', Process='{processName}'");
                return false;
            }
            
            Logger.Debug($"Ignoring UWP system window without title: Process='{processName}'");
            return true;
        }

        if (processName == "Explorer.EXE" && string.IsNullOrWhiteSpace(titleStr))
        {
            Logger.Debug($"Ignoring Explorer.EXE system window without title: Class='{classStr}'");
            return true;
        }

        if (IgnoredProcesses.Contains(processName))
            return true;

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return true;

        IntPtr owner = GetWindow(hWnd, GW_OWNER);
        if (owner != IntPtr.Zero)
            return true;

        if (string.IsNullOrWhiteSpace(titleStr))
        {
            var allowedEmptyTitle = new[] { "WindowsTerminal.exe", "cmd.exe", "powershell.exe", "conhost.exe" };
            bool isAllowed = false;
            foreach (var allowed in allowedEmptyTitle)
            {
                if (processName.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                {
                    isAllowed = true;
                    break;
                }
            }

            if (!isAllowed)
            {
                Logger.Debug($"Ignoring window without title: Class='{classStr}', Process='{processName}'");
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get process name from process ID
    /// </summary>
    private string GetProcessName(uint processId)
    {
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        if (hProcess != IntPtr.Zero)
        {
            StringBuilder procName = new StringBuilder(256);
            if (GetModuleBaseName(hProcess, IntPtr.Zero, procName, (uint)procName.Capacity) > 0)
            {
                string name = procName.ToString();
                CloseHandle(hProcess);
                return name;
            }
            CloseHandle(hProcess);
        }
        return "Unknown";
    }

    /// <summary>
    /// Get detailed information about a window (for logging)
    /// </summary>
    private string GetWindowInfo(IntPtr hWnd)
    {
        if (!IsValidWindow(hWnd))
            return "Invalid window";

        StringBuilder title = new StringBuilder(256);
        GetWindowText(hWnd, title, title.Capacity);

        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);

        uint processId;
        GetWindowThreadProcessId(hWnd, out processId);

        string processName = GetProcessName(processId);

        return $"HWND=0x{hWnd:X}, Title='{title}', Class='{className}', Process='{processName}' (PID={processId})";
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Logger.Info("ForegroundWindowTracker: Event hook removed");
        }

        GC.SuppressFinalize(this);
    }

    ~ForegroundWindowTracker()
    {
        Dispose();
    }
}