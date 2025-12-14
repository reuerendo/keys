using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Manages focus preservation when showing/hiding the keyboard window
/// Ensures that the foreground application maintains focus while keyboard is visible
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

    private const uint GW_OWNER = 4;

    #endregion

    private readonly IntPtr _keyboardWindowHandle;
    private IntPtr _savedForegroundWindow = IntPtr.Zero;
    private readonly object _lockObject = new object();

    public FocusManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
    }

    /// <summary>
    /// Save the current foreground window before showing keyboard
    /// </summary>
    public void SaveForegroundWindow()
    {
        lock (_lockObject)
        {
            IntPtr foreground = GetForegroundWindow();
            
            // Don't save our own window as target
            if (foreground != _keyboardWindowHandle)
            {
                _savedForegroundWindow = foreground;
                Logger.Info($"Saved foreground window: 0x{foreground:X}");
            }
            else
            {
                Logger.Warning("Current foreground is keyboard itself, not saving");
            }
        }
    }

    /// <summary>
    /// Restore focus to the previously saved foreground window
    /// Uses multiple techniques to ensure focus is restored
    /// </summary>
    public async Task<bool> RestoreForegroundWindowAsync()
    {
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
        }

        // Try multiple methods to restore focus
        bool success = false;

        // Method 1: Direct SetForegroundWindow
        success = TrySetForegroundWindow(_savedForegroundWindow);
        
        if (!success)
        {
            // Method 2: Using AttachThreadInput
            success = TrySetForegroundWindowWithThreadAttach(_savedForegroundWindow);
        }

        if (success)
        {
            Logger.Info($"Successfully restored focus to 0x{_savedForegroundWindow:X}");
        }
        else
        {
            Logger.Warning($"Failed to restore focus to 0x{_savedForegroundWindow:X}");
        }

        return success;
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