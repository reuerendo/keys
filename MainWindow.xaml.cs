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
    
    // Window Styles
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOPMOST = 0x00000008;

    // --- Win32 Structs (Correctly Aligned for x64) ---

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public InputUnion u; // Offset 8 to account for alignment padding on x64
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
        
        // 1. Получаем handle окна
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        // 2. Настраиваем DPI и размер
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        int physicalWidth = (int)(760 * scalingFactor);
        int physicalHeight = (int)(330 * scalingFactor);
        
        var appWindow = this.AppWindow;
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        
        // Настраиваем Presenter
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // 3. ПРИНУДИТЕЛЬНО УСТАНАВЛИВАЕМ WS_EX_NOACTIVATE
        // Вызываем это здесь и дополнительно при активации, чтобы стиль точно применился
        ApplyNoActivateStyle();

        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
        
        // Подписываемся на активацию, чтобы убедиться, что стиль не слетел
        this.Activated += (s, e) => ApplyNoActivateStyle();
    }

    private void ApplyNoActivateStyle()
    {
        try
        {
            IntPtr exStylePtr = GetWindowLongPtr(_thisWindowHandle, GWL_EXSTYLE);
            long exStyle = exStylePtr.ToInt64();
            
            // Проверяем, установлен ли уже флаг
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
            SendKey(keyCode);
        }
    }

    private void SendKey(string key)
    {
        // Проверка: какое окно сейчас активно?
        IntPtr currentForeground = GetForegroundWindow();
        string currentTitle = GetWindowTitle(currentForeground);

        Logger.Info($"Clicking '{key}'. Target Window: 0x{currentForeground:X} ({currentTitle})");

        if (currentForeground == _thisWindowHandle)
        {
            Logger.Warning("CRITICAL: Keyboard has focus! Keys will not be sent to target app. WS_EX_NOACTIVATE failed.");
            // Попытка сбросить фокус не поможет мгновенно, но залогируем проблему.
        }

        byte vk = GetVirtualKeyCode(key);
        
        if (vk != 0)
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
            
            // ВАЖНО: Размер структуры жестко задан в Marshal.SizeOf
            int structSize = Marshal.SizeOf(typeof(INPUT));
            
            uint result = SendInput(2, inputs, structSize);
            
            if (result == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"SendInput failed with result 0. Win32 Error: {error}. Struct Size sent: {structSize}");
            }
            else
            {
                Logger.Info($"Success. Sent to {currentTitle}");
            }
        }
    }

    private byte GetVirtualKeyCode(string key)
    {
        return key switch
        {
            "Esc" => 0x1B, "Tab" => 0x09, "Caps" => 0x14, "Shift" => 0x10,
            "Ctrl" => 0x11, "Alt" => 0x12, "Enter" => 0x0D, "Backspace" => 0x08,
            "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34, "5" => 0x35,
            "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39, "0" => 0x30,
            "q" => 0x51, "w" => 0x57, "e" => 0x45, "r" => 0x52, "t" => 0x54,
            "y" => 0x59, "u" => 0x55, "i" => 0x49, "o" => 0x4F, "p" => 0x50,
            "a" => 0x41, "s" => 0x53, "d" => 0x44, "f" => 0x46, "g" => 0x47,
            "h" => 0x48, "j" => 0x4A, "k" => 0x4B, "l" => 0x4C,
            "z" => 0x5A, "x" => 0x58, "c" => 0x43, "v" => 0x56, "b" => 0x42,
            "n" => 0x4E, "m" => 0x4D,
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