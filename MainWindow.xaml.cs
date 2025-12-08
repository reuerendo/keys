using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    // --- Win32 Constants ---
    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    
    // Window Styles
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOPMOST = 0x00000008;

    // Shift state
    private bool _isShiftActive = false;
    private Button _shiftButton;

    // --- Win32 Structs (Correctly Aligned for x64) ---

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // --- P/Invoke Definitions ---

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    private IntPtr _thisWindowHandle;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        int physicalWidth = (int)(760 * scalingFactor);
        int physicalHeight = (int)(330 * scalingFactor);
        
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        ApplyNoActivateStyle();

        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
        
        this.Activated += (s, e) => ApplyNoActivateStyle();
    }

    private void FindShiftButton(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag as string == "Shift")
        {
            _shiftButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindShiftButton(fe);
                if (_shiftButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindShiftButton(scrollContent);
        }
    }

    private void ApplyNoActivateStyle()
    {
        try
        {
            IntPtr exStylePtr = GetWindowLongPtr(_thisWindowHandle, GWL_EXSTYLE);
            long exStyle = exStylePtr.ToInt64();
            
            if ((exStyle & WS_EX_NOACTIVATE) == 0)
            {
                exStyle |= WS_EX_NOACTIVATE;
                SetWindowLongPtr(_thisWindowHandle, GWL_EXSTYLE, (IntPtr)exStyle);
                Logger.Info("Applied WS_EX_NOACTIVATE style.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply window style", ex);
        }
    }

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string keyCode)
        {
            if (keyCode == "Shift")
            {
                ToggleShift();
            }
            else
            {
                SendKey(keyCode);
                
                // Deactivate shift after typing a letter
                if (IsLetter(keyCode) && _isShiftActive)
                {
                    ToggleShift();
                }
            }
        }
    }

    private void ToggleShift()
    {
        _isShiftActive = !_isShiftActive;
        
        // Lazy initialization of shift button reference
        if (_shiftButton == null)
        {
            FindShiftButton(this.Content as FrameworkElement);
        }
        
        UpdateKeyLabels();
        UpdateShiftButtonStyle();
        Logger.Info($"Shift toggled: {_isShiftActive}");
    }

    private void UpdateShiftButtonStyle()
    {
        if (_shiftButton != null)
        {
            // Visual feedback for active shift
            _shiftButton.Opacity = _isShiftActive ? 0.7 : 1.0;
        }
    }

    private void UpdateKeyLabels()
    {
        // Update all letter buttons
        UpdateButtonLabelsRecursive(this.Content as FrameworkElement);
    }

    private void UpdateButtonLabelsRecursive(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag is string tag && IsLetter(tag))
        {
            btn.Content = _isShiftActive ? tag.ToUpper() : tag.ToLower();
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    UpdateButtonLabelsRecursive(fe);
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            UpdateButtonLabelsRecursive(scrollContent);
        }
    }

    private bool IsLetter(string key)
    {
        return key.Length == 1 && char.IsLetter(key[0]);
    }

    private void SendKey(string key)
    {
        IntPtr currentForeground = GetForegroundWindow();
        string currentTitle = GetWindowTitle(currentForeground);

        Logger.Info($"Clicking '{key}'. Target Window: 0x{currentForeground:X} ({currentTitle})");

        if (currentForeground == _thisWindowHandle)
        {
            Logger.Warning("CRITICAL: Keyboard has focus! Keys will not be sent to target app. WS_EX_NOACTIVATE failed.");
        }

        // For letters, send as Unicode characters to avoid layout issues
        if (IsLetter(key))
        {
            char charToSend = _isShiftActive ? char.ToUpper(key[0]) : char.ToLower(key[0]);
            SendUnicodeChar(charToSend);
        }
        else
        {
            // For special keys, use virtual key codes
            byte vk = GetVirtualKeyCode(key);
            
            if (vk != 0)
            {
                SendVirtualKey(vk);
            }
        }
    }

    private void SendUnicodeChar(char character)
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
            Logger.Info($"Success. Sent Unicode char '{character}'");
        }
    }

    private void SendVirtualKey(byte vk)
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

    private byte GetVirtualKeyCode(string key)
    {
        return key switch
        {
            "Esc" => 0x1B, "Tab" => 0x09, "Caps" => 0x14,
            "Ctrl" => 0x11, "Alt" => 0x12, "Enter" => 0x0D, "Backspace" => 0x08,
            "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34, "5" => 0x35,
            "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39, "0" => 0x30,
            "-" => 0xBD, "+" => 0xBB, "=" => 0xBB,
            "(" => 0x39, ")" => 0x30, "/" => 0xBF, "*" => 0x38,
            ":" => 0xBA, ";" => 0xBA,
            "<" => 0xBC, ">" => 0xBE,
            "!" => 0x31, "?" => 0xBF,
            "\"" => 0xDE, " " => 0x20, "," => 0xBC, "." => 0xBE,
            "←" => 0x25, "↓" => 0x28, "→" => 0x27, "↑" => 0x26,
            _ => 0
        };
    }
    
    private string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "<null>";
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}