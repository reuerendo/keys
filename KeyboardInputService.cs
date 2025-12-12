using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Service for sending keyboard input using Win32 API
/// </summary>
public class KeyboardInputService
{
    // Win32 Constants
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    
    // Virtual Key Codes
    private const byte VK_CONTROL = 0x11;
    private const byte VK_DELETE = 0x2E;

    // Win32 Structs (Correctly Aligned for x64)
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // P/Invoke Definitions
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private readonly IntPtr _thisWindowHandle;

    public KeyboardInputService(IntPtr windowHandle)
    {
        _thisWindowHandle = windowHandle;
    }

    /// <summary>
    /// Send a Unicode character
    /// </summary>
    public void SendUnicodeChar(char character)
    {
        INPUT[] inputs = new INPUT[2];
        
        // Key Down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = 0;
        inputs[0].u.ki.wScan = character;
        inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;
        
        // Key Up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = 0;
        inputs[1].u.ki.wScan = character;
        inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        inputs[1].u.ki.time = 0;
        inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;
        
        int structSize = Marshal.SizeOf(typeof(INPUT));
        uint result = SendInput(2, inputs, structSize);
        
        if (result == 0)
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Error($"SendInput (Unicode) failed. Win32 Error: {error}");
        }
        else
        {
            Logger.Info($"Success. Sent Unicode char '{character}' (U+{((int)character):X4})");
        }
    }

    /// <summary>
    /// Send a virtual key code
    /// </summary>
    public void SendVirtualKey(byte vk, bool skipModifiers = false)
    {
        INPUT[] inputs = new INPUT[2];
        
        // Key Down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[0].u.ki.wScan = 0;
        inputs[0].u.ki.dwFlags = 0;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;
        
        // Key Up
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.wScan = 0;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[1].u.ki.time = 0;
        inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;
        
        int structSize = Marshal.SizeOf(typeof(INPUT));
        uint result = SendInput(2, inputs, structSize);
        
        if (result == 0)
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Error($"SendInput (VK) failed. Win32 Error: {error}");
        }
        else
        {
            Logger.Info($"Success. Sent VK code 0x{vk:X}");
        }
    }

    /// <summary>
    /// Send modifier key down
    /// </summary>
    public void SendModifierKeyDown(byte vk)
    {
        INPUT[] inputs = new INPUT[1];
        
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[0].u.ki.wScan = 0;
        inputs[0].u.ki.dwFlags = 0;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;
        
        int structSize = Marshal.SizeOf(typeof(INPUT));
        uint result = SendInput(1, inputs, structSize);
        
        if (result == 0)
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Error($"SendInput (Modifier Down) failed. VK: 0x{vk:X}, Win32 Error: {error}");
        }
        else
        {
            Logger.Info($"Success. Sent Modifier Down 0x{vk:X}");
        }
    }

    /// <summary>
    /// Send modifier key up
    /// </summary>
    public void SendModifierKeyUp(byte vk)
    {
        INPUT[] inputs = new INPUT[1];
        
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[0].u.ki.wScan = 0;
        inputs[0].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[0].u.ki.time = 0;
        inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;
        
        int structSize = Marshal.SizeOf(typeof(INPUT));
        uint result = SendInput(1, inputs, structSize);
        
        if (result == 0)
        {
            int error = Marshal.GetLastWin32Error();
            Logger.Error($"SendInput (Modifier Up) failed. VK: 0x{vk:X}, Win32 Error: {error}");
        }
        else
        {
            Logger.Info($"Success. Sent Modifier Up 0x{vk:X}");
        }
    }

    /// <summary>
    /// Send Ctrl+Key combination
    /// </summary>
    public void SendCtrlKey(char key)
    {
        // Press Ctrl
        SendModifierKeyDown(VK_CONTROL);
        System.Threading.Thread.Sleep(10);
        
        // Press the key
        byte vk = (byte)char.ToUpper(key);
        SendVirtualKey(vk);
        System.Threading.Thread.Sleep(10);
        
        // Release Ctrl
        SendModifierKeyUp(VK_CONTROL);
        
        Logger.Info($"Sent Ctrl+{key}");
    }

    /// <summary>
    /// Send a single virtual key (e.g., Delete)
    /// </summary>
    public void SendKey(byte virtualKey)
    {
        SendVirtualKey(virtualKey);
        Logger.Info($"Sent key: 0x{virtualKey:X}");
    }

    /// <summary>
    /// Get virtual key code for control keys
    /// </summary>
    public byte GetVirtualKeyCode(string key)
    {
        return key switch
        {
            "Esc" => 0x1B,
            "Tab" => 0x09,
            "Caps" => 0x14,
            "Ctrl" => 0x11,
            "Alt" => 0x12,
            "Enter" => 0x0D,
            "Backspace" => 0x08,
            " " => 0x20, // Space
            "↑" => 0x26, // Arrow Up
            "↓" => 0x28, // Arrow Down
            "←" => 0x25, // Arrow Left
            "→" => 0x27, // Arrow Right
            _ => 0
        };
    }

    /// <summary>
    /// Get virtual key code for layout keys (for shortcuts)
    /// </summary>
    public byte GetVirtualKeyCodeForLayoutKey(string key)
    {
        return key switch
        {
            // Letters - map to their uppercase VK codes
            "q" => 0x51, "w" => 0x57, "e" => 0x45, "r" => 0x52,
            "t" => 0x54, "y" => 0x59, "u" => 0x55, "i" => 0x49,
            "o" => 0x4F, "p" => 0x50, "a" => 0x41, "s" => 0x53,
            "d" => 0x44, "f" => 0x46, "g" => 0x47, "h" => 0x48,
            "j" => 0x4A, "k" => 0x4B, "l" => 0x4C, "z" => 0x5A,
            "x" => 0x58, "c" => 0x43, "v" => 0x56, "b" => 0x42,
            "n" => 0x4E, "m" => 0x4D,
            
            // Numbers
            "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33,
            "4" => 0x34, "5" => 0x35, "6" => 0x36, "7" => 0x37,
            "8" => 0x38, "9" => 0x39,
            
            _ => 0
        };
    }

    /// <summary>
    /// Get foreground window handle
    /// </summary>
    public IntPtr GetForegroundWindowHandle()
    {
        return GetForegroundWindow();
    }

    /// <summary>
    /// Get window title by handle
    /// </summary>
    public string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "<null>";
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// Check if keyboard window has focus
    /// </summary>
    public bool IsKeyboardWindowFocused()
    {
        return GetForegroundWindow() == _thisWindowHandle;
    }
}