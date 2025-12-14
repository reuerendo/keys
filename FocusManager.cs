using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;

namespace VirtualKeyboard;

/// <summary>
/// Manages focus preservation with detailed diagnostics
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

    private const uint GW_OWNER = 4;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint GA_ROOT = 2;
    private const uint GA_ROOTOWNER = 3;

    #endregion

    private readonly IntPtr _keyboardWindowHandle;
    private IntPtr _savedForegroundWindow = IntPtr.Zero;
    private readonly object _lockObject = new object();

    public FocusManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
    }

    /// <summary>
    /// Get detailed information about a window
    /// </summary>
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

    /// <summary>
    /// Save the current foreground window before showing keyboard
    /// WITH DETAILED DIAGNOSTICS
    /// </summary>
    public void SaveForegroundWindow()
    {
        lock (_lockObject)
        {
            IntPtr foreground = GetForegroundWindow();
            
            Logger.Info("=== FOCUS SAVE DIAGNOSTICS ===");
            Logger.Info($"Current foreground: {GetWindowInfo(foreground)}");
            
            // Check if it's our own window
            if (foreground == _keyboardWindowHandle)
            {
                Logger.Warning("Current foreground is keyboard itself!");
                Logger.Info($"Keyboard window: {GetWindowInfo(_keyboardWindowHandle)}");
                
                // Try to find the real target window - maybe it's the root/owner?
                IntPtr rootOwner = GetAncestor(foreground, GA_ROOTOWNER);
                if (rootOwner != IntPtr.Zero && rootOwner != foreground)
                {
                    Logger.Info($"Root owner window: {GetWindowInfo(rootOwner)}");
                }
                
                _savedForegroundWindow = IntPtr.Zero;
                Logger.Warning("Not saving - keyboard has focus");
                return;
            }

            // Check if foreground is valid and visible
            if (!IsWindow(foreground) || !IsWindowVisible(foreground))
            {
                Logger.Warning($"Foreground window is not valid or not visible");
                _savedForegroundWindow = IntPtr.Zero;
                return;
            }

            // Get the root window (in case foreground is a child window)
            IntPtr rootWindow = GetAncestor(foreground, GA_ROOT);
            if (rootWindow != IntPtr.Zero && rootWindow != foreground)
            {
                Logger.Info($"Root window: {GetWindowInfo(rootWindow)}");
                Logger.Info("Using root window instead of foreground");
                _savedForegroundWindow = rootWindow;
            }
            else
            {
                _savedForegroundWindow = foreground;
            }
            
            Logger.Info($"✓ SAVED: {GetWindowInfo(_savedForegroundWindow)}");
            Logger.Info("=== END DIAGNOSTICS ===");
        }
    }

    /// <summary>
    /// Restore focus to the previously saved foreground window
    /// </summary>
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

        // Try multiple methods to restore focus
        bool success = false;

        // Method 1: Direct SetForegroundWindow
        success = TrySetForegroundWindow(targetWindow);
        
        if (!success)
        {
            // Method 2: Using AttachThreadInput
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

    /// <summary>
    /// Synchronous version of RestoreForegroundWindow for immediate use
    /// </summary>
    public bool RestoreForegroundWindow()
    {
        return RestoreForegroundWindowAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Get the currently saved foreground window
    /// </summary>
    public IntPtr GetSavedForegroundWindow()
    {
        lock (_lockObject)
        {
            return _savedForegroundWindow;
        }
    }

    /// <summary>
    /// Check if we have a valid saved window
    /// </summary>
    public bool HasValidSavedWindow()
    {
        lock (_lockObject)
        {
            return _savedForegroundWindow != IntPtr.Zero && 
                   IsWindow(_savedForegroundWindow) && 
                   IsWindowVisible(_savedForegroundWindow);
        }
    }

    /// <summary>
    /// Clear the saved foreground window
    /// </summary>
    public void ClearSavedWindow()
    {
        lock (_lockObject)
        {
            _savedForegroundWindow = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Try to set foreground window directly
    /// </summary>
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

    /// <summary>
    /// Try to set foreground window using thread input attachment
    /// This method can bypass some Windows restrictions
    /// </summary>
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

            // Attach our thread input to the target window's thread
            bool attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            
            if (!attached)
            {
                Logger.Debug("Failed to attach thread input");
                return false;
            }

            try
            {
                // Try to bring window to top first
                BringWindowToTop(hWnd);
                
                // Then set as foreground
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
                // Always detach thread input
                AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in TrySetForegroundWindowWithThreadAttach", ex);
            return false;
        }
    }

    /// <summary>
    /// Check if the keyboard window currently has focus
    /// </summary>
    public bool IsKeyboardFocused()
    {
        return GetForegroundWindow() == _keyboardWindowHandle;
    }

    /// <summary>
    /// Restore focus with delay (for use after UI operations)
    /// </summary>
    public async Task RestoreForegroundWindowWithDelayAsync(int delayMs = 50)
    {
        await Task.Delay(delayMs);
        await RestoreForegroundWindowAsync();
    }
}