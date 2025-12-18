using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace VirtualKeyboard;

/// <summary>
/// Global mouse click detector using low-level mouse hook
/// Tracks mouse clicks to distinguish user clicks from programmatic focus changes
/// </summary>
public class MouseClickDetector : IDisposable
{
    #region P/Invoke

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    #endregion

    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelMouseProc _hookProc; // CRITICAL: Keep strong reference to prevent GC
    private DateTime _lastClickTime = DateTime.MinValue;
    private Point _lastClickPosition = Point.Empty;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;

    /// <summary>
    /// Time window in milliseconds to consider focus change as click-initiated
    /// FIXED: Increased from 150ms to 300ms for Chrome/Electron apps
    /// </summary>
    public int ClickTimeWindowMs { get; set; } = 300;

    /// <summary>
    /// Event fired when a mouse click is detected
    /// </summary>
    public event EventHandler<Point> ClickDetected;

    public MouseClickDetector()
    {
        try
        {
            // CRITICAL: Keep reference to prevent GC
            _hookProc = HookCallback;
            
            _hookId = SetHook(_hookProc);
            
            if (_hookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"Failed to set mouse hook. Error code: {error}");
            }
            else
            {
                // Keep delegate alive
                GC.KeepAlive(_hookProc);
                Logger.Info("✅ Mouse click detector initialized successfully");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize mouse click detector", ex);
        }
    }

    /// <summary>
    /// Set up low-level mouse hook
    /// </summary>
    private IntPtr SetHook(LowLevelMouseProc proc)
    {
        try
        {
            using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                if (curModule == null)
                {
                    Logger.Error("Cannot get current module for mouse hook");
                    return IntPtr.Zero;
                }

                return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error setting up mouse hook: {ex.Message}", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Mouse hook callback - records click events
    /// CRITICAL: Must not throw exceptions - wraps everything in try-catch
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (_isDisposed)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                
                // Track left button clicks only
                if (msg == WM_LBUTTONDOWN)
                {
                    try
                    {
                        var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        var clickPoint = new Point(hookStruct.pt.X, hookStruct.pt.Y);
                        
                        lock (_lockObject)
                        {
                            _lastClickTime = DateTime.UtcNow;
                            _lastClickPosition = clickPoint;
                        }
                        
                        Logger.Debug($"🖱️ Mouse click detected at ({hookStruct.pt.X}, {hookStruct.pt.Y})");

                        // Fire event - wrapped in try-catch to prevent handler exceptions from crashing
                        try
                        {
                            ClickDetected?.Invoke(this, clickPoint);
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("CRITICAL: Exception in ClickDetected event handler", ex);
                            // Continue - don't let handler exception crash the hook
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error processing mouse click: {ex.Message}", ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // CRITICAL: Never let exceptions escape from hook callback
            Logger.Error($"CRITICAL: Unhandled exception in mouse hook callback: {ex.Message}", ex);
        }

        // Always call next hook
        try
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Check if a recent mouse click occurred within the time window
    /// </summary>
    public bool WasRecentClick()
    {
        try
        {
            lock (_lockObject)
            {
                if (_lastClickTime == DateTime.MinValue)
                    return false;

                var timeSinceClick = (DateTime.UtcNow - _lastClickTime).TotalMilliseconds;
                return timeSinceClick <= ClickTimeWindowMs;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error in WasRecentClick: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Check if a recent click occurred and was within the specified bounds
    /// </summary>
    /// <param name="bounds">Element bounds in screen coordinates</param>
    /// <returns>True if click was recent and inside bounds</returns>
    public bool WasRecentClickInBounds(Rectangle bounds)
    {
        try
        {
            lock (_lockObject)
            {
                if (!WasRecentClick())
                    return false;

                bool isInBounds = bounds.Contains(_lastClickPosition);
                
                if (isInBounds)
                {
                    Logger.Debug($"✅ Click at ({_lastClickPosition.X}, {_lastClickPosition.Y}) is inside element bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
                }
                else
                {
                    Logger.Debug($"❌ Click at ({_lastClickPosition.X}, {_lastClickPosition.Y}) is OUTSIDE element bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
                }

                return isInBounds;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Error in WasRecentClickInBounds: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Get information about the last click
    /// </summary>
    public (DateTime time, Point position) GetLastClickInfo()
    {
        lock (_lockObject)
        {
            return (_lastClickTime, _lastClickPosition);
        }
    }

    /// <summary>
    /// Reset click tracking (useful for testing or manual control)
    /// </summary>
    public void Reset()
    {
        lock (_lockObject)
        {
            _lastClickTime = DateTime.MinValue;
            _lastClickPosition = Point.Empty;
            Logger.Debug("Click detector reset");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            if (_hookId != IntPtr.Zero)
            {
                bool success = UnhookWindowsHookEx(_hookId);
                
                if (success)
                {
                    Logger.Info("Mouse hook removed successfully");
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    Logger.Warning($"Failed to remove mouse hook. Error code: {error}");
                }
                
                _hookId = IntPtr.Zero;
            }

            // Keep delegate alive until disposal is complete
            GC.KeepAlive(_hookProc);
        }
        catch (Exception ex)
        {
            Logger.Error("Error removing mouse hook", ex);
        }

        GC.SuppressFinalize(this);
    }

    ~MouseClickDetector()
    {
        Dispose();
    }
}