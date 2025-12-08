using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;
using System.Text;

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
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern int GetWindowTextLength(IntPtr hWnd);

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
    private TextBlock _logTextBlock;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        // Get window handle
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        
        // Get the AppWindow for advanced windowing features
        var appWindow = this.AppWindow;
        
        // Get DPI for proper scaling
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        
        // Set window size in physical pixels (accounting for DPI scaling)
        int physicalWidth = (int)(760 * scalingFactor);
        int physicalHeight = (int)(430 * scalingFactor); // Increased for log area
        
        appWindow.Resize(new Windows.Graphics.SizeInt32(physicalWidth, physicalHeight));
        
        // Get the OverlappedPresenter to configure window behavior
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Make window always on top
            presenter.IsAlwaysOnTop = true;
            
            // Disable resizing
            presenter.IsResizable = false;
            
            // Disable maximize button
            presenter.IsMaximizable = false;
        }

        // Store target window on activation
        this.Activated += MainWindow_Activated;
        
        Log($"Keyboard window handle: {_thisWindowHandle}");
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        Log($"Window activation state: {args.WindowActivationState}");
        
        // When our window gets activated, we want to remember the previous foreground window
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            IntPtr foreground = GetForegroundWindow();
            Log($"Current foreground window: {foreground}");
            
            if (foreground != _thisWindowHandle && foreground != IntPtr.Zero)
            {
                _targetWindow = foreground;
                Log($"Set target window to: {foreground} ({GetWindowTitle(foreground)})");
            }
        }
    }

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string keyCode)
        {
            Log($"Button clicked: {keyCode}");
            SendKeyToTarget(keyCode);
        }
    }

    private void SendKeyToTarget(string key)
    {
        // If we don't have a target window yet, try to get it
        if (_targetWindow == IntPtr.Zero)
        {
            _targetWindow = GetForegroundWindow();
            Log($"No target window, using foreground: {_targetWindow}");
            
            if (_targetWindow == _thisWindowHandle)
            {
                Log("ERROR: Target is keyboard itself, aborting");
                return;
            }
        }

        Log($"Target window: {_targetWindow} ({GetWindowTitle(_targetWindow)})");

        // Temporarily switch focus to target window
        IntPtr previousWindow = GetForegroundWindow();
        Log($"Previous window before switch: {previousWindow}");
        
        bool focusResult = BringWindowToForeground(_targetWindow);
        Log($"BringWindowToForeground result: {focusResult}");
        
        IntPtr currentForeground = GetForegroundWindow();
        Log($"Foreground after switch: {currentForeground}");

        // Send the key
        byte vk = GetVirtualKeyCode(key);
        if (vk != 0)
        {
            Log($"Sending virtual key code: {vk}");
            
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
            Log($"SendInput result: {result} (should be 2)");
        }
        else
        {
            Log($"ERROR: Unknown key code for '{key}'");
        }

        // Return focus to keyboard window
        System.Threading.Tasks.Task.Delay(50).ContinueWith(_ => 
        {
            DispatcherQueue.TryEnqueue(() => 
            {
                bool returnResult = BringWindowToForeground(_thisWindowHandle);
                Log($"Return focus to keyboard result: {returnResult}");
            });
        });
    }

    private bool BringWindowToForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            Log("BringWindowToForeground: hWnd is Zero");
            return false;
        }

        IntPtr foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == hWnd)
        {
            Log("BringWindowToForeground: Already foreground");
            return true;
        }

        uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        uint targetThreadId = GetWindowThreadProcessId(hWnd, out _);
        uint currentThreadId = GetCurrentThreadId();

        Log($"Thread IDs - Current: {currentThreadId}, Foreground: {foregroundThreadId}, Target: {targetThreadId}");

        // Attach input threads to allow setting foreground
        if (foregroundThreadId != currentThreadId)
        {
            bool attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            Log($"Attach current to foreground: {attached}");
        }
        if (targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
        {
            bool attached = AttachThreadInput(currentThreadId, targetThreadId, true);
            Log($"Attach current to target: {attached}");
        }

        bool result = SetForegroundWindow(hWnd);
        Log($"SetForegroundWindow result: {result}");

        // Detach input threads
        if (foregroundThreadId != currentThreadId)
        {
            AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
        if (targetThreadId != currentThreadId && targetThreadId != foregroundThreadId)
        {
            AttachThreadInput(currentThreadId, targetThreadId, false);
        }

        return result;
    }

    private string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "NULL";
        
        int length = GetWindowTextLength(hWnd);
        if (length == 0) return "NO_TITLE";
        
        StringBuilder sb = new StringBuilder(length + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private void Log(string message)
    {
        if (_logTextBlock == null)
        {
            // Find or create log text block
            if (Content is ScrollViewer sv && sv.Content is StackPanel mainPanel)
            {
                _logTextBlock = new TextBlock
                {
                    Text = "",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                    FontSize = 10,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
                };
                
                mainPanel.Children.Add(new Border
                {
                    BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.Gray),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8),
                    Child = new ScrollViewer
                    {
                        Height = 80,
                        Content = _logTextBlock
                    }
                });
            }
        }

        if (_logTextBlock != null)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logTextBlock.Text += $"[{timestamp}] {message}\n";
        }
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