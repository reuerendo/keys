using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace VirtualKeyboard;

/// <summary>
/// Global mouse click detector using low-level mouse hook.
/// Records ALL clicks without filtering - actual hardware detection is done by GetCurrentInputMessageSource.
/// Works correctly with Surface touch/pen input where clicks are injected by drivers.
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
    private LowLevelMouseProc _hookProc;
    private DateTime _lastHardwareClickTime = DateTime.MinValue;
    private Point _lastHardwareClickPosition = Point.Empty;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;

    /// <summary>
    /// Time window in milliseconds to consider focus change as click-initiated.
    /// 300ms to account for async focus events on web pages.
    /// </summary>
    public int ClickTimeWindowMs { get; set; } = 300;

    /// <summary>
    /// Event fired when a HARDWARE mouse click is detected
    /// </summary>
    public event EventHandler<Point> HardwareClickDetected;

    public MouseClickDetector()
    {
        // Keep reference to prevent GC
        _hookProc = HookCallback;
        
        try
        {
            _hookId = SetHook(_hookProc);
            
            if (_hookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"Failed to set mouse hook. Error code: {error}");
            }
            else
            {
                Logger.Info("✅ Mouse click detector initialized (records all clicks for Surface touch/pen support)");
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
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    /// <summary>
    /// Mouse hook callback - records ALL clicks without filtering.
    /// Filtering is done in WinEventFocusTracker using GetCurrentInputMessageSource.
    /// </summary>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            
            // Track left button clicks only
            if (msg == WM_LBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var clickPoint = new Point(hookStruct.pt.X, hookStruct.pt.Y);
                
                // Record ALL clicks - no filtering here
                // On Surface devices, touch/pen are converted to mouse via SendInput,
                // so LLMHF_INJECTED flag is set even for real user input.
                // GetCurrentInputMessageSource in WinEventFocusTracker will distinguish properly.
                lock (_lockObject)
                {
                    _lastHardwareClickTime = DateTime.UtcNow;
                    _lastHardwareClickPosition = clickPoint;
                    
                    bool isInjected = (hookStruct.flags & 0x00000001) != 0;
                    Logger.Debug($"🖱️ Click recorded at ({hookStruct.pt.X}, {hookStruct.pt.Y}) - Injected={isInjected}, dwExtraInfo=0x{hookStruct.dwExtraInfo.ToInt64():X}");
                }

                // Fire event for all clicks
                try
                {
                    HardwareClickDetected?.Invoke(this, clickPoint);
                }
                catch (Exception ex)
                {
                    Logger.Error("Error in HardwareClickDetected event handler", ex);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Check if a recent mouse click occurred within the time window.
    /// Does not distinguish hardware vs programmatic - use GetCurrentInputMessageSource for that.
    /// </summary>
    public bool WasRecentHardwareClick()
    {
        lock (_lockObject)
        {
            if (_lastHardwareClickTime == DateTime.MinValue)
                return false;

            var timeSinceClick = (DateTime.UtcNow - _lastHardwareClickTime).TotalMilliseconds;
            bool wasRecent = timeSinceClick <= ClickTimeWindowMs;
            
            if (wasRecent)
            {
                Logger.Debug($"✅ Recent click confirmed ({timeSinceClick:F0}ms ago)");
            }
            
            return wasRecent;
        }
    }

    /// <summary>
    /// Check if a recent click occurred and was within the specified bounds
    /// </summary>
    /// <param name="bounds">Element bounds in screen coordinates</param>
    /// <returns>True if click was recent and inside bounds</returns>
    public bool WasRecentHardwareClickInBounds(Rectangle bounds)
    {
        lock (_lockObject)
        {
            if (!WasRecentHardwareClick())
                return false;

            bool isInBounds = bounds.Contains(_lastHardwareClickPosition);
            
            if (isInBounds)
            {
                Logger.Debug($"✅ Click at ({_lastHardwareClickPosition.X}, {_lastHardwareClickPosition.Y}) is inside element bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
            }
            else
            {
                Logger.Debug($"❌ Click at ({_lastHardwareClickPosition.X}, {_lastHardwareClickPosition.Y}) is OUTSIDE element bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
            }

            return isInBounds;
        }
    }

    /// <summary>
    /// Get information about the last click
    /// </summary>
    public (DateTime time, Point position) GetLastHardwareClickInfo()
    {
        lock (_lockObject)
        {
            return (_lastHardwareClickTime, _lastHardwareClickPosition);
        }
    }

    /// <summary>
    /// Reset click tracking (useful for testing or manual control)
    /// </summary>
    public void Reset()
    {
        lock (_lockObject)
        {
            _lastHardwareClickTime = DateTime.MinValue;
            _lastHardwareClickPosition = Point.Empty;
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