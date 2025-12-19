using System;
using System.Runtime.InteropServices;
using System.Drawing;

namespace VirtualKeyboard;

/// <summary>
/// Tracks pointer input (mouse, touch, pen) and keyboard input.
/// Records the sequence of all user inputs to determine what was the most recent.
/// Does NOT use timeouts - relies on strict input sequencing.
/// </summary>
public class PointerInputTracker : IDisposable
{
    #region P/Invoke

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
    
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    #endregion

    private IntPtr _mouseHookId = IntPtr.Zero;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private LowLevelMouseProc _mouseHookProc;
    private LowLevelKeyboardProc _keyboardHookProc;
    
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;

    // Last input tracking
    private InputType _lastInputType = InputType.None;
    private PointerClickInfo _lastPointerClick = null;
    private ulong _inputSequence = 0; // Global input sequence counter

    public PointerInputTracker()
    {
        // Keep references to prevent GC
        _mouseHookProc = MouseHookCallback;
        _keyboardHookProc = KeyboardHookCallback;
        
        try
        {
            _mouseHookId = SetMouseHook(_mouseHookProc);
            _keyboardHookId = SetKeyboardHook(_keyboardHookProc);
            
            if (_mouseHookId == IntPtr.Zero || _keyboardHookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"Failed to set input hooks. Error code: {error}");
            }
            else
            {
                Logger.Info("✅ PointerInputTracker initialized (mouse + keyboard hooks)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize PointerInputTracker", ex);
        }
    }

    private IntPtr SetMouseHook(LowLevelMouseProc proc)
    {
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    private IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
        using (var curModule = curProcess.MainModule)
        {
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
    }

    /// <summary>
    /// Mouse hook callback - records ALL pointer clicks
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            
            // Track all button clicks
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var clickPoint = new Point(hookStruct.pt.X, hookStruct.pt.Y);
                
                // Get window under cursor
                IntPtr hwnd = WindowFromPoint(hookStruct.pt);
                
                // Determine device type
                var deviceType = DetermineDeviceType(hookStruct);
                
                lock (_lockObject)
                {
                    _inputSequence++;
                    _lastInputType = InputType.PointerClick;
                    _lastPointerClick = new PointerClickInfo
                    {
                        Position = clickPoint,
                        WindowHandle = hwnd,
                        DeviceType = deviceType,
                        Timestamp = DateTime.UtcNow,
                        Sequence = _inputSequence
                    };
                    
                    Logger.Debug($"🖱️ Pointer click #{_inputSequence}: ({clickPoint.X}, {clickPoint.Y}) HWND={hwnd:X} Device={deviceType}");
                }
            }
        }

        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Keyboard hook callback - tracks keyboard input
    /// </summary>
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                
                lock (_lockObject)
                {
                    _inputSequence++;
                    _lastInputType = InputType.Keyboard;
                    
                    Logger.Debug($"⌨️ Keyboard input #{_inputSequence}: VK={hookStruct.vkCode}");
                }
            }
        }

        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Determine input device type from hook structure
    /// </summary>
    private InputDeviceType DetermineDeviceType(MSLLHOOKSTRUCT hookStruct)
    {
        const uint LLMHF_INJECTED = 0x00000001;
        bool isInjected = (hookStruct.flags & LLMHF_INJECTED) != 0;
        
        // Get input source from Windows API
        try
        {
            bool success = NativeMethods.GetCurrentInputMessageSource(out NativeMethods.INPUT_MESSAGE_SOURCE source);
            
            if (success)
            {
                // Check device type first (more reliable than origin)
                switch (source.deviceType)
                {
                    case NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_MOUSE:
                        Logger.Debug($"   🖱️ Device detected: MOUSE (origin={source.originId})");
                        return InputDeviceType.Mouse;
                    case NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCH:
                        Logger.Debug($"   👆 Device detected: TOUCH (origin={source.originId})");
                        return InputDeviceType.Touch;
                    case NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_PEN:
                        Logger.Debug($"   ✏️ Device detected: PEN (origin={source.originId})");
                        return InputDeviceType.Pen;
                    case NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD:
                        Logger.Debug($"   📱 Device detected: TOUCHPAD (origin={source.originId})");
                        return InputDeviceType.Touchpad;
                }
                
                // If device type is unavailable but origin is hardware, assume mouse
                if (source.originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_HARDWARE)
                {
                    Logger.Debug($"   🖱️ Device type unavailable but origin=HARDWARE, assuming MOUSE");
                    return InputDeviceType.Mouse;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"   ⚠️ GetCurrentInputMessageSource failed: {ex.Message}");
        }
        
        // Critical fallback logic:
        // If we're in mouse hook and flag is NOT injected, it's definitely pointer input
        // Even if API failed, we should trust the hook
        if (!isInjected)
        {
            Logger.Debug($"   🖱️ API unavailable but NOT injected - assuming MOUSE (safe fallback)");
            return InputDeviceType.Mouse;
        }
        
        Logger.Debug($"   ❓ Could not determine device type - marked as UNKNOWN");
        return InputDeviceType.Unknown;
    }

    /// <summary>
    /// Get the last recorded pointer click (no timeout - just the last one)
    /// </summary>
    public PointerClickInfo GetLastPointerClick()
    {
        lock (_lockObject)
        {
            return _lastPointerClick;
        }
    }

    /// <summary>
    /// Check if the last user input was a pointer click (not keyboard)
    /// This is used for IMO_UNAVAILABLE validation
    /// </summary>
    public bool IsLastInputPointerClick()
    {
        lock (_lockObject)
        {
            return _lastInputType == InputType.PointerClick;
        }
    }

    /// <summary>
    /// Get current input sequence number
    /// </summary>
    public ulong GetCurrentSequence()
    {
        lock (_lockObject)
        {
            return _inputSequence;
        }
    }

    /// <summary>
    /// Reset tracking (useful for testing)
    /// </summary>
    public void Reset()
    {
        lock (_lockObject)
        {
            _lastInputType = InputType.None;
            _lastPointerClick = null;
            _inputSequence = 0;
            Logger.Debug("PointerInputTracker reset");
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            if (_mouseHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = IntPtr.Zero;
            }
            
            if (_keyboardHookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = IntPtr.Zero;
            }
            
            Logger.Info("PointerInputTracker disposed");
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing PointerInputTracker", ex);
        }

        GC.SuppressFinalize(this);
    }

    ~PointerInputTracker()
    {
        Dispose();
    }
}

/// <summary>
/// Information about a pointer click
/// </summary>
public class PointerClickInfo
{
    public Point Position { get; set; }
    public IntPtr WindowHandle { get; set; }
    public InputDeviceType DeviceType { get; set; }
    public DateTime Timestamp { get; set; }
    public ulong Sequence { get; set; }
    
    /// <summary>
    /// Check if this is pointer input (mouse/touch/pen/touchpad)
    /// Returns true even for Unknown if captured via mouse hook (safe assumption)
    /// </summary>
    public bool IsPointerInput => 
        DeviceType == InputDeviceType.Mouse || 
        DeviceType == InputDeviceType.Touch || 
        DeviceType == InputDeviceType.Pen ||
        DeviceType == InputDeviceType.Touchpad ||
        DeviceType == InputDeviceType.Unknown; // Mouse hook captures pointer events, so Unknown = likely mouse
}

public enum InputDeviceType
{
    Unknown,
    Mouse,
    Touch,
    Pen,
    Touchpad,
    Keyboard
}

internal enum InputType
{
    None,
    PointerClick,
    Keyboard
}