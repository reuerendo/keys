using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace VirtualKeyboard;

/// <summary>
/// Global mouse click detector with extended time window for Chrome/Edge
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
    private DateTime _lastClickTime = DateTime.MinValue;
    private Point _lastClickPosition = Point.Empty;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;

    /// <summary>
    /// Extended time window for Chrome/Edge (they need time to build accessibility tree)
    /// Initial load can take 3-4 seconds, subsequent clicks are faster
    /// </summary>
    public int ClickTimeWindowMs { get; set; } = 4000;  // 4 seconds for initial Chrome/Edge load

    /// <summary>
    /// Event fired when a mouse click is detected
    /// </summary>
    public event EventHandler<Point> ClickDetected;

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
                Logger.Info("✅ Mouse click detector initialized (4000ms window for Chrome/Edge)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize mouse click detector", ex);
        }
    }

    private IntPtr SetHook(LowLevelMouseProc proc)
    {
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            
            if (msg == WM_LBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var clickPoint = new Point(hookStruct.pt.X, hookStruct.pt.Y);
                
                lock (_lockObject)
                {
                    _lastClickTime = DateTime.UtcNow;
                    _lastClickPosition = clickPoint;
                    
                    Logger.Debug($"🖱️ Mouse click at ({hookStruct.pt.X}, {hookStruct.pt.Y})");
                }

                try
                {
                    ClickDetected?.Invoke(this, clickPoint);
                }
                catch (Exception ex)
                {
                    Logger.Error("Error in ClickDetected event handler", ex);
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public bool WasRecentClick()
    {
        lock (_lockObject)
        {
            if (_lastClickTime == DateTime.MinValue)
                return false;

            var timeSinceClick = (DateTime.UtcNow - _lastClickTime).TotalMilliseconds;
            bool isRecent = timeSinceClick <= ClickTimeWindowMs;
            
            if (!isRecent)
            {
                Logger.Debug($"Click was {timeSinceClick:F0}ms ago (window: {ClickTimeWindowMs}ms) - too old");
            }
            
            return isRecent;
        }
    }

    public bool WasRecentClickInBounds(Rectangle bounds)
    {
        lock (_lockObject)
        {
            if (!WasRecentClick())
                return false;

            bool isInBounds = bounds.Contains(_lastClickPosition);
            
            var timeSinceClick = (DateTime.UtcNow - _lastClickTime).TotalMilliseconds;
            
            if (isInBounds)
            {
                Logger.Debug($"✅ Click at ({_lastClickPosition.X}, {_lastClickPosition.Y}) {timeSinceClick:F0}ms ago is inside bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
            }
            else
            {
                Logger.Debug($"❌ Click at ({_lastClickPosition.X}, {_lastClickPosition.Y}) {timeSinceClick:F0}ms ago is OUTSIDE bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
            }

            return isInBounds;
        }
    }

    public (DateTime time, Point position) GetLastClickInfo()
    {
        lock (_lockObject)
        {
            return (_lastClickTime, _lastClickPosition);
        }
    }

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