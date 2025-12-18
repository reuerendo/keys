using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace VirtualKeyboard;

/// <summary>
/// Global mouse click detector using low-level mouse hook.
/// Uses relaxed filtering: primarily dwExtraInfo, LLMHF_INJECTED is secondary check.
/// </summary>
public class MouseClickDetector : IDisposable
{
    #region P/Invoke

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    
    // Magic value used by SendInput to mark injected events
    private static readonly IntPtr INJECTED_EXTRA_INFO = new IntPtr(0xFF515700);
    
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
    /// </summary>
    public int ClickTimeWindowMs { get; set; } = 150;

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
                Logger.Info("✅ Hardware mouse click detector initialized with relaxed filtering");
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
    /// Mouse hook callback - records ONLY hardware clicks
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
                
                // Check if this is a hardware click using RELAXED logic
                bool isHardwareClick = IsHardwareInput(hookStruct);
                
                if (isHardwareClick)
                {
                    lock (_lockObject)
                    {
                        _lastHardwareClickTime = DateTime.UtcNow;
                        _lastHardwareClickPosition = clickPoint;
                        
                        Logger.Debug($"🖱️ HARDWARE click detected at ({hookStruct.pt.X}, {hookStruct.pt.Y})");
                    }

                    // Fire event for hardware click detection
                    try
                    {
                        HardwareClickDetected?.Invoke(this, clickPoint);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Error in HardwareClickDetected event handler", ex);
                    }
                }
                else
                {
                    Logger.Debug($"🚫 PROGRAMMATIC click detected and IGNORED at ({hookStruct.pt.X}, {hookStruct.pt.Y})");
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Check if click is from hardware device using dwExtraInfo primarily.
    /// RELAXED: LLMHF_INJECTED is now a SECONDARY check, not absolute filter.
    /// </summary>
    private bool IsHardwareInput(MSLLHOOKSTRUCT hookStruct)
    {
        try
        {
            IntPtr extraInfo = hookStruct.dwExtraInfo;
            long extraInfoValue = extraInfo.ToInt64();
            
            // PRIMARY CHECK: Known injection markers in dwExtraInfo
            if (extraInfo == INJECTED_EXTRA_INFO)
            {
                Logger.Debug($"🚫 Known SendInput marker (dwExtraInfo: 0x{extraInfoValue:X})");
                return false;
            }
            
            // Suspicious: very large values often indicate SendInput injection
            if (extraInfoValue > 0x00FFFFFF && extraInfoValue != 0)
            {
                Logger.Debug($"🚫 Suspicious dwExtraInfo: 0x{extraInfoValue:X}");
                return false;
            }
            
            // SECONDARY CHECK: LLMHF_INJECTED flag
            // ИЗМЕНЕНИЕ: Не считаем это абсолютным фильтром!
            const uint LLMHF_INJECTED = 0x00000001;
            bool hasInjectedFlag = (hookStruct.flags & LLMHF_INJECTED) != 0;
            
            if (hasInjectedFlag)
            {
                // Проверяем dwExtraInfo более внимательно
                if (extraInfoValue == 0)
                {
                    // LLMHF_INJECTED + dwExtraInfo=0 часто бывает у настоящих мышей на Windows 11
                    Logger.Debug($"⚠️ LLMHF_INJECTED flag set BUT dwExtraInfo=0 (common on Win11) - ACCEPTING as hardware");
                    return true;
                }
                else
                {
                    Logger.Debug($"🚫 LLMHF_INJECTED flag + non-zero dwExtraInfo (0x{extraInfoValue:X}) - rejecting");
                    return false;
                }
            }
            
            // Если нет явных признаков инъекции - принимаем как hardware
            Logger.Debug($"✅ Hardware input (dwExtraInfo: 0x{extraInfoValue:X}, flags: 0x{hookStruct.flags:X})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking input source", ex);
            // On error, assume programmatic to be safe
            return false;
        }
    }

    /// <summary>
    /// Check if a recent HARDWARE mouse click occurred within the time window
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
                Logger.Debug($"✅ Recent hardware click confirmed ({timeSinceClick:F0}ms ago)");
            }
            
            return wasRecent;
        }
    }

    /// <summary>
    /// Check if a recent hardware click occurred and was within the specified bounds
    /// </summary>
    /// <param name="bounds">Element bounds in screen coordinates</param>
    /// <returns>True if click was recent, hardware, and inside bounds</returns>
    public bool WasRecentHardwareClickInBounds(Rectangle bounds)
    {
        lock (_lockObject)
        {
            if (!WasRecentHardwareClick())
                return false;

            bool isInBounds = bounds.Contains(_lastHardwareClickPosition);
            
            if (isInBounds)
            {
                Logger.Debug($"✅ Hardware click at ({_lastHardwareClickPosition.X}, {_lastHardwareClickPosition.Y}) is inside element bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
            }
            else
            {
                Logger.Debug($"❌ Hardware click at ({_lastHardwareClickPosition.X}, {_lastHardwareClickPosition.Y}) is OUTSIDE element bounds ({bounds.X}, {bounds.Y}, {bounds.Width}x{bounds.Height})");
            }

            return isInBounds;
        }
    }

    /// <summary>
    /// Get information about the last hardware click
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
            Logger.Debug("Hardware click detector reset");
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