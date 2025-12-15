using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;

namespace VirtualKeyboard;

/// <summary>
/// Manages focus preservation with smart window detection and caching
/// Tracks last active application window to restore focus correctly
/// </summary>
public class FocusManager
{
    #region Win32 API

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint GW_OWNER = 4;
    private const uint GW_HWNDPREV = 3;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;

    #endregion

    private readonly IntPtr _keyboardWindowHandle;
    private IntPtr _savedForegroundWindow = IntPtr.Zero;
    private IntPtr _lastValidAppWindow = IntPtr.Zero; // Cache for last valid app window
    private readonly object _lockObject = new object();

    private static readonly HashSet<string> IgnoredClasses = new HashSet<string>
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow",
    };

    private static readonly HashSet<string> IgnoredProcesses = new HashSet<string>
    {
        "ShellExperienceHost.exe",
        "SearchHost.exe",
        "StartMenuExperienceHost.exe",
    };

    public FocusManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
    }

    private string GetWindowInfo(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return "Invalid window";

        StringBuilder title = new StringBuilder(256);
        GetWindowText(hWnd, title, title.Capacity);

        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);

        uint processId;
        GetWindowThreadProcessId(hWnd, out processId);

        string processName = "Unknown";
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        if (hProcess != IntPtr.Zero)
        {
            StringBuilder procName = new StringBuilder(256);
            if (GetModuleBaseName(hProcess, IntPtr.Zero, procName, (uint)procName.Capacity) > 0)
            {
                processName = procName.ToString();
            }
            CloseHandle(hProcess);
        }

        bool isVisible = IsWindowVisible(hWnd);
        IntPtr owner = GetWindow(hWnd, GW_OWNER);

        return $"HWND=0x{hWnd:X}, Title='{title}', Class='{className}', Process='{processName}' (PID={processId}), Visible={isVisible}, Owner=0x{owner:X}";
    }

    private bool ShouldIgnoreWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd))
            return true;

        if (hWnd == _keyboardWindowHandle)
            return true;

        StringBuilder className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        string classStr = className.ToString();

        if (IgnoredClasses.Contains(classStr))
        {
            Logger.Debug($"Ignoring window with class '{classStr}'");
            return true;
        }

        uint processId;
        GetWindowThreadProcessId(hWnd, out processId);
        IntPtr hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, processId);
        if (hProcess != IntPtr.Zero)
        {
            StringBuilder procName = new StringBuilder(256);
            if (GetModuleBaseName(hProcess, IntPtr.Zero, procName, (uint)procName.Capacity) > 0)
            {
                string processName = procName.ToString();
                CloseHandle(hProcess);
                
                if (IgnoredProcesses.Contains(processName))
                {
                    Logger.Debug($"Ignoring window from process '{processName}'");
                    return true;
                }
            }
            else
            {
                CloseHandle(hProcess);
            }
        }

        if (!IsWindowVisible(hWnd))
        {
            Logger.Debug($"Ignoring invisible window");
            return true;
        }

        int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
        {
            Logger.Debug($"Ignoring tool window");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Find the best target window using improved Z-order search
    /// Prioritizes windows that were recently foreground
    /// </summary>
    private IntPtr FindBestTargetWindow()
    {
        IntPtr bestWindow = IntPtr.Zero;
        IntPtr foreground = GetForegroundWindow();
        
        Logger.Debug("Searching for best target window...");
        
        // Strategy 1: If we have a cached valid window, try it first
        if (_lastValidAppWindow != IntPtr.Zero && 
            IsWindow(_lastValidAppWindow) && 
            IsWindowVisible(_lastValidAppWindow) &&
            !ShouldIgnoreWindow(_lastValidAppWindow))
        {
            Logger.Info($"Using cached last app window: {GetWindowInfo(_lastValidAppWindow)}");
            return _lastValidAppWindow;
        }
        
        // Strategy 2: Check window before current foreground in Z-order
        if (foreground != IntPtr.Zero)
        {
            IntPtr prevWindow = GetWindow(foreground, GW_HWNDPREV);
            int attempts = 0;
            
            while (prevWindow != IntPtr.Zero && attempts < 10)
            {
                if (!ShouldIgnoreWindow(prevWindow))
                {
                    StringBuilder title = new StringBuilder(256);
                    GetWindowText(prevWindow, title, title.Capacity);
                    
                    if (title.Length > 0)
                    {
                        Logger.Info($"Found previous window in Z-order: {GetWindowInfo(prevWindow)}");
                        return prevWindow;
                    }
                }
                prevWindow = GetWindow(prevWindow, GW_HWNDPREV);
                attempts++;
            }
        }
        
        // Strategy 3: Enumerate all windows and find first valid one with title
        EnumWindows((hWnd, lParam) =>
        {
            if (!ShouldIgnoreWindow(hWnd))
            {
                StringBuilder title = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                
                if (title.Length > 0)
                {
                    bestWindow = hWnd;
                    Logger.Info($"Found candidate window via enumeration: {GetWindowInfo(hWnd)}");
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        // Strategy 4: If still nothing, try any valid window
        if (bestWindow == IntPtr.Zero)
        {
            EnumWindows((hWnd, lParam) =>
            {
                if (!ShouldIgnoreWindow(hWnd))
                {
                    bestWindow = hWnd;
                    Logger.Info($"Found fallback window: {GetWindowInfo(hWnd)}");
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }

        return bestWindow;
    }

    /// <summary>
    /// Update cached last valid application window
    /// Call this periodically or when hiding keyboard
    /// </summary>
    public void UpdateLastValidAppWindow()
    {
        lock (_lockObject)
        {
            IntPtr foreground = GetForegroundWindow();
            
            if (foreground != IntPtr.Zero && 
                foreground != _keyboardWindowHandle &&
                !ShouldIgnoreWindow(foreground))
            {
                _lastValidAppWindow = foreground;
                Logger.Debug($"Updated last valid app window: {GetWindowInfo(_lastValidAppWindow)}");
            }
        }
    }

    /// <summary>
    /// Save the current foreground window before showing keyboard
    /// WITH SMART DETECTION and cached window preference
    /// </summary>
    public void SaveForegroundWindow()
    {
        lock (_lockObject)
        {
            IntPtr foreground = GetForegroundWindow();
            
            Logger.Info("=== FOCUS SAVE DIAGNOSTICS ===");
            Logger.Info($"Current foreground: {GetWindowInfo(foreground)}");
            
            if (_lastValidAppWindow != IntPtr.Zero && 
                IsWindow(_lastValidAppWindow) && 
                IsWindowVisible(_lastValidAppWindow))
            {
                Logger.Info($"Cached last app window: {GetWindowInfo(_lastValidAppWindow)}");
            }
            
            if (ShouldIgnoreWindow(foreground))
            {
                Logger.Warning($"Current foreground is system window");
                
                // First try: use cached window if available
                if (_lastValidAppWindow != IntPtr.Zero && 
                    IsWindow(_lastValidAppWindow) && 
                    IsWindowVisible(_lastValidAppWindow) &&
                    !ShouldIgnoreWindow(_lastValidAppWindow))
                {
                    _savedForegroundWindow = _lastValidAppWindow;
                    Logger.Info($"✓ SAVED (from cache): {GetWindowInfo(_savedForegroundWindow)}");
                }
                else
                {
                    // Second try: search for best target
                    IntPtr targetWindow = FindBestTargetWindow();
                    
                    if (targetWindow != IntPtr.Zero)
                    {
                        _savedForegroundWindow = targetWindow;
                        _lastValidAppWindow = targetWindow; // Update cache
                        Logger.Info($"✓ SAVED (smart detection): {GetWindowInfo(_savedForegroundWindow)}");
                    }
                    else
                    {
                        Logger.Warning("✗ No valid target window found");
                        _savedForegroundWindow = IntPtr.Zero;
                    }
                }
            }
            else
            {
                IntPtr rootWindow = GetAncestor(foreground, GA_ROOT);
                if (rootWindow != IntPtr.Zero && rootWindow != foreground && !ShouldIgnoreWindow(rootWindow))
                {
                    Logger.Info($"Root window: {GetWindowInfo(rootWindow)}");
                    Logger.Info("Using root window instead of foreground");
                    _savedForegroundWindow = rootWindow;
                }
                else
                {
                    _savedForegroundWindow = foreground;
                }
                
                // Update cache with valid window
                _lastValidAppWindow = _savedForegroundWindow;
                
                Logger.Info($"✓ SAVED (direct): {GetWindowInfo(_savedForegroundWindow)}");
            }
            
            Logger.Info("=== END DIAGNOSTICS ===");
        }
    }

    public async Task<bool> RestoreForegroundWindowAsync()
    {
        IntPtr targetWindow;
        
        lock (_lockObject)
        {
            if (_savedForegroundWindow == IntPtr.Zero)
            {
                Logger.Warning("No saved foreground window to restore");
                return false;
            }

            if (!IsWindow(_savedForegroundWindow))
            {
                Logger.Warning("Saved window is no longer valid");
                _savedForegroundWindow = IntPtr.Zero;
                return false;
            }

            if (!IsWindowVisible(_savedForegroundWindow))
            {
                Logger.Warning("Saved window is not visible");
                return false;
            }
            
            targetWindow = _savedForegroundWindow;
        }

        Logger.Info("=== FOCUS RESTORE DIAGNOSTICS ===");
        IntPtr currentBefore = GetForegroundWindow();
        Logger.Info($"BEFORE restore - Current: {GetWindowInfo(currentBefore)}");
        Logger.Info($"BEFORE restore - Target: {GetWindowInfo(targetWindow)}");

        bool success = false;

        success = TrySetForegroundWindow(targetWindow);
        
        if (!success)
        {
            success = TrySetForegroundWindowWithThreadAttach(targetWindow);
        }

        IntPtr currentAfter = GetForegroundWindow();
        Logger.Info($"AFTER restore - Current: {GetWindowInfo(currentAfter)}");
        
        if (success && currentAfter == targetWindow)
        {
            Logger.Info($"✓ SUCCESS: Focus restored to correct window");
        }
        else if (success)
        {
            Logger.Warning($"⚠ PARTIAL: SetForegroundWindow succeeded but foreground is different window");
        }
        else
        {
            Logger.Error($"✗ FAILED: Could not restore focus");
        }
        
        Logger.Info("=== END RESTORE DIAGNOSTICS ===");

        return success && currentAfter == targetWindow;
    }

    public bool RestoreForegroundWindow()
    {
        return RestoreForegroundWindowAsync().GetAwaiter().GetResult();
    }

    public IntPtr GetSavedForegroundWindow()
    {
        lock (_lockObject)
        {
            return _savedForegroundWindow;
        }
    }

    public bool HasValidSavedWindow()
    {
        lock (_lockObject)
        {
            return _savedForegroundWindow != IntPtr.Zero && 
                   IsWindow(_savedForegroundWindow) && 
                   IsWindowVisible(_savedForegroundWindow);
        }
    }

    public void ClearSavedWindow()
    {
        lock (_lockObject)
        {
            _savedForegroundWindow = IntPtr.Zero;
        }
    }

    private bool TrySetForegroundWindow(IntPtr hWnd)
    {
        try
        {
            bool result = SetForegroundWindow(hWnd);
            
            if (result)
            {
                Logger.Debug("SetForegroundWindow succeeded (Method 1)");
                return true;
            }
            
            Logger.Debug("SetForegroundWindow failed (Method 1)");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in TrySetForegroundWindow", ex);
            return false;
        }
    }

    private bool TrySetForegroundWindowWithThreadAttach(IntPtr hWnd)
    {
        try
        {
            uint currentThreadId = GetCurrentThreadId();
            uint targetThreadId = GetWindowThreadProcessId(hWnd, out _);

            if (targetThreadId == 0 || currentThreadId == targetThreadId)
            {
                return false;
            }

            bool attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            
            if (!attached)
            {
                Logger.Debug("Failed to attach thread input");
                return false;
            }

            try
            {
                BringWindowToTop(hWnd);
                bool result = SetForegroundWindow(hWnd);
                
                if (result)
                {
                    Logger.Debug("SetForegroundWindow succeeded (Method 2 - Thread Attach)");
                }
                else
                {
                    Logger.Debug("SetForegroundWindow failed even with thread attach");
                }
                
                return result;
            }
            finally
            {
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in TrySetForegroundWindowWithThreadAttach", ex);
            return false;
        }
    }

    public bool IsKeyboardFocused()
    {
        return GetForegroundWindow() == _keyboardWindowHandle;
    }

    public async Task RestoreForegroundWindowWithDelayAsync(int delayMs = 50)
    {
        await Task.Delay(delayMs);
        await RestoreForegroundWindowAsync();
    }
}