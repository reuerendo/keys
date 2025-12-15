using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text;

namespace VirtualKeyboard;

/// <summary>
/// Manages focus restoration using real-time window tracking
/// Much faster and more accurate than Z-order search
/// </summary>
public class FocusManager : IDisposable
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    #endregion

    private readonly IntPtr _keyboardWindowHandle;
    private readonly ForegroundWindowTracker _windowTracker;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;

    public FocusManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        
        // Initialize real-time window tracker
        _windowTracker = new ForegroundWindowTracker(keyboardWindowHandle);
        
        Logger.Info("FocusManager initialized with real-time tracking");
    }

    /// <summary>
    /// Get the target window for focus restoration
    /// Uses real-time tracked window - instant, no search needed!
    /// </summary>
    public IntPtr GetTargetWindow()
    {
        lock (_lockObject)
        {
            IntPtr targetWindow = _windowTracker.GetLastValidWindow();
            
            if (targetWindow != IntPtr.Zero)
            {
                Logger.Info($"✓ Target window from tracker: {GetWindowInfo(targetWindow)}");
                return targetWindow;
            }
            
            Logger.Warning("No valid target window tracked");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Restore focus to the tracked target window
    /// </summary>
    public async Task<bool> RestoreFocusAsync()
    {
        IntPtr targetWindow = GetTargetWindow();
        
        if (targetWindow == IntPtr.Zero)
        {
            Logger.Warning("No target window to restore focus to");
            return false;
        }

        // Verify window is still valid
        if (!IsWindow(targetWindow) || !IsWindowVisible(targetWindow))
        {
            Logger.Warning("Target window is no longer valid or visible");
            return false;
        }

        Logger.Info("=== FOCUS RESTORE ===");
        IntPtr currentBefore = GetForegroundWindow();
        Logger.Info($"BEFORE: Current={GetWindowInfo(currentBefore)}, Target={GetWindowInfo(targetWindow)}");

        // Try multiple methods to restore focus
        bool success = false;

        // Method 1: Direct SetForegroundWindow
        success = TrySetForegroundWindow(targetWindow);
        
        if (!success)
        {
            // Method 2: Using AttachThreadInput
            success = TrySetForegroundWindowWithThreadAttach(targetWindow);
        }

        // Give Windows time to process
        await Task.Delay(10);

        IntPtr currentAfter = GetForegroundWindow();
        Logger.Info($"AFTER: Current={GetWindowInfo(currentAfter)}");
        
        if (success && currentAfter == targetWindow)
        {
            Logger.Info("✓ SUCCESS: Focus restored");
        }
        else if (success)
        {
            Logger.Warning("⚠ PARTIAL: SetForegroundWindow succeeded but foreground is different");
        }
        else
        {
            Logger.Error("✗ FAILED: Could not restore focus");
        }
        
        Logger.Info("=== END RESTORE ===");

        return success && currentAfter == targetWindow;
    }

    /// <summary>
    /// Synchronous version of RestoreFocus
    /// </summary>
    public bool RestoreFocus()
    {
        return RestoreFocusAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Check if we have a valid tracked window
    /// </summary>
    public bool HasValidTrackedWindow()
    {
        return _windowTracker.HasValidWindow();
    }

    /// <summary>
    /// Check if the keyboard window currently has focus
    /// </summary>
    public bool IsKeyboardFocused()
    {
        return GetForegroundWindow() == _keyboardWindowHandle;
    }

    /// <summary>
    /// Clear tracked window (only call on app exit)
    /// </summary>
    public void ClearTrackedWindow()
    {
        _windowTracker.ClearTrackedWindow();
    }

    /// <summary>
    /// Restore focus with delay (for use after UI operations)
    /// </summary>
    public async Task RestoreFocusWithDelayAsync(int delayMs = 50)
    {
        await Task.Delay(delayMs);
        await RestoreFocusAsync();
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
    /// Get detailed information about a window (for logging)
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

        return $"HWND=0x{hWnd:X}, Title='{title}', Class='{className}', PID={processId}";
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        _windowTracker?.Dispose();

        Logger.Info("FocusManager disposed");

        GC.SuppressFinalize(this);
    }

    ~FocusManager()
    {
        Dispose();
    }
}