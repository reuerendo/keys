using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
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

    private IntPtr _targetWindow = IntPtr.Zero;
    private IntPtr _thisWindowHandle;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        Logger.Info("=== MainWindow Constructor Started ===");
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        // Get the AppWindow for advanced windowing features
        var appWindow = this.AppWindow;
        
        // Get DPI for proper scaling
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        Logger.Info($"DPI: {dpi}, Scaling Factor: {scalingFactor}");
        
        // Set window size in physical pixels (accounting for DPI scaling)
        int physicalWidth = (int)(760 * scalingFactor);
        int physicalHeight = (int)(330 * scalingFactor);
        
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        Logger.Info($"Window resized to: {physicalWidth}x{physicalHeight}");
        
        // Get the OverlappedPresenter to configure window behavior
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Make window always on top
            presenter.IsAlwaysOnTop = true;
            
            // Disable resizing
            presenter.IsResizable = false;
            
            // Disable maximize button
            presenter.IsMaximizable = false;
            
            Logger.Info("Window configured: AlwaysOnTop=true, Resizable=false, Maximizable=false");
        }

        // Store target window on activation
        this.Activated += MainWindow_Activated;
        
        Logger.Info($"Log file location: {Logger.GetLogFilePath()}");
        Logger.Info("=== MainWindow Constructor Completed ===");
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Logger.Debug($"Window activation state changed: {args.WindowActivationState}");
        
        // When our window gets deactivated, remember which window is getting focus
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // A small delay to let the other window become foreground
            System.Threading.Tasks.Task.Delay(50).ContinueWith(_ => 
            {
                DispatcherQueue.TryEnqueue(() => 
                {
                    IntPtr foreground = GetForegroundWindow();
                    Logger.Debug($"Window deactivated, new foreground: 0x{foreground.ToString("X")}");
                    
                    if (foreground != _thisWindowHandle && foreground != IntPtr.Zero)
                    {
                        _targetWindow = foreground;
                        string targetWindowTitle = GetWindowTitle(foreground);
                        Logger.Info($"Target window set to: 0x{_targetWindow.ToString("X")} (Title: {targetWindowTitle})");
                    }
                });
            });
        }
    }

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string keyCode)
        {
            Logger.Info($"=== Key Button Clicked: {keyCode} ===");
            SendKeyToTarget(keyCode);
        }
    }

    private void SendKeyToTarget(string key)
    {
        Logger.Info($"SendKeyToTarget called for key: {key}");
        
        // If we don't have a target window yet, try to get it
        if (_targetWindow == IntPtr.Zero)
        {
            Logger.Warning("Target window is null, attempting to get foreground window");
            _targetWindow = GetForegroundWindow();
            
            if (_targetWindow == _thisWindowHandle)
            {
                Logger.Error("Cannot send to ourselves - no valid target window");
                return;
            }
            
            Logger.Info($"Target window set to: 0x{_targetWindow.ToString("X")} (Title: {GetWindowTitle(_targetWindow)})");
        }

        // Log current state
        IntPtr currentForeground = GetForegroundWindow();
        Logger.Debug($"Current foreground before switching: 0x{currentForeground.ToString("X")} (Title: {GetWindowTitle(currentForeground)})");
        Logger.Debug($"Target window: 0x{_targetWindow.ToString("X")} (Title: {GetWindowTitle(_targetWindow)})");

        // Temporarily switch focus to target window
        IntPtr previousWindow = GetForegroundWindow();
        Logger.Debug("Attempting to bring target window to foreground...");
        BringWindowToForeground(_targetWindow);
        
        // Verify focus switch
        IntPtr newForeground = GetForegroundWindow();
        Logger.Debug($"Foreground after switch: 0x{newForeground.ToString("X")} (Title: {GetWindowTitle(newForeground)})");
        
        if (newForeground != _targetWindow)
        {
            Logger.Warning($"Focus switch may have failed! Expected: 0x{_targetWindow.ToString("X")}, Got: 0x{newForeground.ToString("X")}");
        }

        // Send the key
        byte vk = GetVirtualKeyCode(key);
        Logger.Debug($"Virtual key code for '{key}': 0x{vk:X2}");
        
        if (vk != 0)
        {
            INPUT[] inputs = new INPUT[2];
            
            // Key down
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = vk;
            inputs[0].u.ki.wScan = 0;
            inputs[0].u.ki.dwFlags = 0;
            inputs[0].u.ki.time = 0;
            inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;
            
            // Key up
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = vk;
            inputs[1].u.ki.wScan = 0;
            inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs[1].u.ki.time = 0;
            inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;
            
            uint result = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            Logger.Info($"SendInput result: {result} (expected 2)");
            
            if (result != 2)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"SendInput failed! Error code: {error}");
            }
        }
        else
        {
            Logger.Error($"Unknown virtual key code for key: {key}");
        }

        // Don't return focus - let the target window keep it
        // The keyboard stays on top anyway (AlwaysOnTop=true)
        IntPtr finalForeground = GetForegroundWindow();
        Logger.Debug($"Final foreground after send: 0x{finalForeground.ToString("X")} (Title: {GetWindowTitle(finalForeground)})");
        Logger.Info("=== Key Send Operation Completed ===");
    }

    private void BringWindowToForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            Logger.Warning("BringWindowToForeground called with null handle");
            return;
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == hWnd)
        {
            Logger.Debug("Window is already in foreground");
            return;
        }

        uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        uint targetThreadId = GetWindowThreadProcessId(hWnd, out _);
        uint currentThreadId = GetCurrentThreadId();

        Logger.Debug($"Thread IDs - Current: {currentThreadId}, Foreground: {foregroundThreadId}, Target: {targetThreadId}");

        // Attach input threads to allow setting foreground
        if (foregroundThreadId != currentThreadId)
        {
            bool attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            Logger.Debug($"Attached current to foreground thread: {attached}");
        }
        if (targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
        {
            bool attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            Logger.Debug($"Attached current to target thread: {attached}");
        }

        bool setResult = SetForegroundWindow(hWnd);
        Logger.Debug($"SetForegroundWindow result: {setResult}");

        // Detach input threads
        if (foregroundThreadId != currentThreadId)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
        if (targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
        {
            AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    /// <summary>
    /// Get the title of a window by its handle
    /// </summary>
    private string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return "<null>";
            
        var sb = new System.Text.StringBuilder(256);
        int length = GetWindowText(hWnd, sb, sb.Capacity);
        
        if (length > 0)
            return sb.ToString();
        
        return $"<no title, handle: 0x{hWnd.ToString("X")}>";
    }

    private byte GetVirtualKeyCode(string key)
    {
        return key switch
        {
            // Special keys
            "Esc" => 0x1B,
            "Tab" => 0x09,
            "Caps" => 0x14,
            "Shift" => 0x10,
            "Ctrl" => 0x11,
            "Alt" => 0x12,
            "Enter" => 0x0D,
            "Backspace" => 0x08,
            
            // Numbers
            "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34, "5" => 0x35,
            "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39, "0" => 0x30,
            
            // Letters
            "q" => 0x51, "w" => 0x57, "e" => 0x45, "r" => 0x52, "t" => 0x54,
            "y" => 0x59, "u" => 0x55, "i" => 0x49, "o" => 0x4F, "p" => 0x50,
            "a" => 0x41, "s" => 0x53, "d" => 0x44, "f" => 0x46, "g" => 0x47,
            "h" => 0x48, "j" => 0x4A, "k" => 0x4B, "l" => 0x4C,
            "z" => 0x5A, "x" => 0x58, "c" => 0x43, "v" => 0x56, "b" => 0x42,
            "n" => 0x4E, "m" => 0x4D,
            
            // Symbols
            "-" => 0xBD, "+" => 0xBB, "=" => 0xBB,
            "(" => 0x39, ")" => 0x30, "/" => 0xBF, "*" => 0x38,
            ":" => 0xBA, ";" => 0xBA,
            "<" => 0xBC, ">" => 0xBE,
            "!" => 0x31, "?" => 0xBF,
            "\"" => 0xDE, " " => 0x20, "," => 0xBC, "." => 0xBE,
            
            // Arrow keys
            "←" => 0x25, "↓" => 0x28, "→" => 0x27, "↑" => 0x26,
            
            _ => 0
        };
    }
}