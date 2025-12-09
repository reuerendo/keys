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

    // Keyboard state
    private bool _isShiftActive = false;
    private bool _isCapsLockActive = false;
    private bool _isCtrlActive = false;
    private bool _isAltActive = false;
    private Button _shiftButton;
    private Button _capsButton;
    private Button _ctrlButton;
    private Button _altButton;
    private Button _langButton;
    
    // Layouts
    private KeyboardLayout _englishLayout;
    private KeyboardLayout _russianLayout;
    private KeyboardLayout _symbolLayout;
    private KeyboardLayout _currentLayout;
    private bool _isSymbolMode = false;

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
        
        // Initialize layouts
        _englishLayout = KeyboardLayout.CreateEnglishLayout();
        _russianLayout = KeyboardLayout.CreateRussianLayout();
        _symbolLayout = KeyboardLayout.CreateSymbolLayout();
        _currentLayout = _englishLayout;
        
        _thisWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Logger.Info($"This window handle: 0x{_thisWindowHandle.ToString("X")}");
        
        uint dpi = GetDpiForWindow(_thisWindowHandle);
        float scalingFactor = dpi / 96f;
        
        // Calculate window size with extra margin for ScrollViewer and borders
        // Content width: 1002 (buttons + gaps) + margins 22*2 = 1046
        // Adding extra 40px for ScrollViewer padding and window chrome
        int physicalWidth = (int)(1034 * scalingFactor);
        int physicalHeight = (int)(366 * scalingFactor);
        
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
            else if (keyCode == "Caps")
            {
                ToggleCapsLock();
            }
            else if (keyCode == "Ctrl")
            {
                ToggleCtrl();
            }
            else if (keyCode == "Alt")
            {
                ToggleAlt();
            }
            else if (keyCode == "Lang")
            {
                SwitchLanguage();
            }
            else if (keyCode == "&..")
            {
                ToggleSymbolMode();
            }
            else
            {
                SendKey(keyCode);
                
                // Deactivate shift after typing (but not Caps Lock)
                if (_isShiftActive && IsLayoutKey(keyCode))
                {
                    ToggleShift();
                }
                
                // НЕ деактивируем Ctrl и Alt - они остаются нажатыми до повторного клика
                // Это позволяет выбирать несколько файлов с Ctrl
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
        UpdateModifierButtonStyle();
        Logger.Info($"Shift toggled: {_isShiftActive}");
    }

    private void ToggleCapsLock()
    {
        _isCapsLockActive = !_isCapsLockActive;
        
        // Lazy initialization of caps button reference
        if (_capsButton == null)
        {
            FindCapsButton(this.Content as FrameworkElement);
        }
        
        UpdateKeyLabels();
        UpdateModifierButtonStyle();
        Logger.Info($"Caps Lock toggled: {_isCapsLockActive}");
    }

    private void ToggleCtrl()
    {
        _isCtrlActive = !_isCtrlActive;
        
        // Lazy initialization of ctrl button reference
        if (_ctrlButton == null)
        {
            FindCtrlButton(this.Content as FrameworkElement);
        }
        
        UpdateModifierButtonStyle();
        Logger.Info($"Ctrl toggled: {_isCtrlActive}");
    }

    private void ToggleAlt()
    {
        _isAltActive = !_isAltActive;
        
        // Lazy initialization of alt button reference
        if (_altButton == null)
        {
            FindAltButton(this.Content as FrameworkElement);
        }
        
        UpdateModifierButtonStyle();
        Logger.Info($"Alt toggled: {_isAltActive}");
    }

    private void SwitchLanguage()
    {
        if (_isSymbolMode)
        {
            return; // Don't switch language in symbol mode
        }
        
        _currentLayout = (_currentLayout == _englishLayout) ? _russianLayout : _englishLayout;
        UpdateKeyLabels();
        Logger.Info($"Switched to layout: {_currentLayout.Name}");
    }

    private void ToggleSymbolMode()
    {
        _isSymbolMode = !_isSymbolMode;
        
        if (_isSymbolMode)
        {
            _currentLayout = _symbolLayout;
        }
        else
        {
            _currentLayout = _englishLayout;
        }
        
        UpdateKeyLabels();
        UpdateLangButtonLabel();
        Logger.Info($"Symbol mode: {_isSymbolMode}, Layout: {_currentLayout.Name}");
    }

    private void UpdateModifierButtonStyle()
    {
        if (_shiftButton != null)
        {
            _shiftButton.Opacity = _isShiftActive ? 0.7 : 1.0;
        }
        if (_capsButton != null)
        {
            _capsButton.Opacity = _isCapsLockActive ? 0.7 : 1.0;
        }
        if (_ctrlButton != null)
        {
            _ctrlButton.Opacity = _isCtrlActive ? 0.7 : 1.0;
        }
        if (_altButton != null)
        {
            _altButton.Opacity = _isAltActive ? 0.7 : 1.0;
        }
    }

    private void UpdateLangButtonLabel()
    {
        if (_langButton == null)
        {
            FindLangButton(this.Content as FrameworkElement);
        }
        
        if (_langButton != null)
        {
            _langButton.Content = _isSymbolMode ? "abc" : "Lang";
        }
    }

    private void UpdateKeyLabels()
    {
        UpdateButtonLabelsRecursive(this.Content as FrameworkElement);
        UpdateLangButtonLabel();
    }

    private void UpdateButtonLabelsRecursive(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag is string tag)
        {
            // Skip control keys
            if (tag == "Shift" || tag == "Lang" || tag == "&.." || 
                tag == "Esc" || tag == "Tab" || tag == "Caps" || 
                tag == "Ctrl" || tag == "Alt" || tag == "Enter" || 
                tag == "Backspace" || tag == " ")
            {
                // Don't update control keys except Lang button (handled separately)
            }
            else if (_currentLayout.Keys.ContainsKey(tag))
            {
                var keyDef = _currentLayout.Keys[tag];
                // Apply shift OR caps lock for letters
                bool shouldCapitalize = (_isShiftActive || _isCapsLockActive) && keyDef.IsLetter;
                // For Shift with Caps Lock, they cancel each other out
                if (_isShiftActive && _isCapsLockActive && keyDef.IsLetter)
                {
                    shouldCapitalize = false;
                }
                // For non-letters, only shift affects display
                bool useShift = _isShiftActive && !keyDef.IsLetter;
                
                btn.Content = (shouldCapitalize || useShift) ? keyDef.DisplayShift : keyDef.Display;
            }
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

    private bool IsLayoutKey(string key)
    {
        return _currentLayout.Keys.ContainsKey(key);
    }

    private void SendKey(string key)
    {
        IntPtr currentForeground = GetForegroundWindow();
        string currentTitle = GetWindowTitle(currentForeground);

        Logger.Info($"Clicking '{key}'. Target Window: 0x{currentForeground:X} ({currentTitle}). Modifiers: Ctrl={_isCtrlActive}, Alt={_isAltActive}, Shift={_isShiftActive}, Caps={_isCapsLockActive}");

        if (currentForeground == _thisWindowHandle)
        {
            Logger.Warning("CRITICAL: Keyboard has focus! Keys will not be sent to target app. WS_EX_NOACTIVATE failed.");
        }

        // Press modifier keys first
        if (_isCtrlActive)
        {
            SendModifierKeyDown(0x11); // VK_CONTROL
        }
        if (_isAltActive)
        {
            SendModifierKeyDown(0x12); // VK_MENU (Alt)
        }
        if (_isShiftActive)
        {
            SendModifierKeyDown(0x10); // VK_SHIFT
        }

        // Проверяем, является ли это служебной клавишей (стрелки и т.д.)
        byte controlVk = GetVirtualKeyCode(key);
        if (controlVk != 0)
        {
            // Это служебная клавиша - отправляем VK-код
            SendVirtualKey(controlVk);
        }
        // Check if key is in current layout - use Unicode input
        else if (_currentLayout.Keys.ContainsKey(key))
        {
            var keyDef = _currentLayout.Keys[key];
            
            // For shortcuts (Ctrl/Alt pressed), we need to send VK codes, not Unicode
            if (_isCtrlActive || _isAltActive)
            {
                // Try to get VK code for the key
                byte vk = GetVirtualKeyCodeForLayoutKey(key);
                if (vk != 0)
                {
                    SendVirtualKey(vk, skipModifiers: true);
                }
                else
                {
                    Logger.Warning($"No VK code found for '{key}' - shortcuts may not work");
                }
            }
            else
            {
                // Normal typing - use Unicode
                // Apply shift OR caps lock for letters
                bool shouldCapitalize = (_isShiftActive || _isCapsLockActive) && keyDef.IsLetter;
                // Shift + Caps Lock cancel each other
                if (_isShiftActive && _isCapsLockActive && keyDef.IsLetter)
                {
                    shouldCapitalize = false;
                }
                // For non-letters, only shift affects output
                bool useShift = _isShiftActive && !keyDef.IsLetter;
                
                string charToSend = (shouldCapitalize || useShift) ? keyDef.ValueShift : keyDef.Value;
                
                // Send each character
                foreach (char c in charToSend)
                {
                    SendUnicodeChar(c);
                }
            }
        }
        else
        {
            // For standalone characters not in layout
            // Try to send as Unicode first if it's a printable character
            if (key.Length == 1 && !char.IsControl(key[0]))
            {
                SendUnicodeChar(key[0]);
            }
        }

        // Release modifier keys
        if (_isShiftActive)
        {
            SendModifierKeyUp(0x10); // VK_SHIFT
        }
        if (_isAltActive)
        {
            SendModifierKeyUp(0x12); // VK_MENU (Alt)
        }
        if (_isCtrlActive)
        {
            SendModifierKeyUp(0x11); // VK_CONTROL
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
            Logger.Info($"Success. Sent Unicode char '{character}' (U+{((int)character):X4})");
        }
    }

    private void SendModifierKeyDown(byte vk)
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
    }

    private void SendModifierKeyUp(byte vk)
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
    }

    private void SendVirtualKey(byte vk, bool skipModifiers = false)
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
        // Control keys and arrow keys that should use VK codes
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
    
    private byte GetVirtualKeyCodeForLayoutKey(string key)
    {
        // Map layout keys to their VK codes for shortcuts (Ctrl+X, etc.)
        // This works for English layout keys
        return key switch
        {
            // Letters - map to their uppercase VK codes
            "q" => 0x51, // VK_Q
            "w" => 0x57, // VK_W
            "e" => 0x45, // VK_E
            "r" => 0x52, // VK_R
            "t" => 0x54, // VK_T
            "y" => 0x59, // VK_Y
            "u" => 0x55, // VK_U
            "i" => 0x49, // VK_I
            "o" => 0x4F, // VK_O
            "p" => 0x50, // VK_P
            "a" => 0x41, // VK_A
            "s" => 0x53, // VK_S
            "d" => 0x44, // VK_D
            "f" => 0x46, // VK_F
            "g" => 0x47, // VK_G
            "h" => 0x48, // VK_H
            "j" => 0x4A, // VK_J
            "k" => 0x4B, // VK_K
            "l" => 0x4C, // VK_L
            "z" => 0x5A, // VK_Z
            "x" => 0x58, // VK_X
            "c" => 0x43, // VK_C
            "v" => 0x56, // VK_V
            "b" => 0x42, // VK_B
            "n" => 0x4E, // VK_N
            "m" => 0x4D, // VK_M
            
            // Numbers
            "0" => 0x30,
            "1" => 0x31,
            "2" => 0x32,
            "3" => 0x33,
            "4" => 0x34,
            "5" => 0x35,
            "6" => 0x36,
            "7" => 0x37,
            "8" => 0x38,
            "9" => 0x39,
            
            _ => 0
        };
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

    private void FindCapsButton(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag as string == "Caps")
        {
            _capsButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindCapsButton(fe);
                if (_capsButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindCapsButton(scrollContent);
        }
    }

    private void FindCtrlButton(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag as string == "Ctrl")
        {
            _ctrlButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindCtrlButton(fe);
                if (_ctrlButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindCtrlButton(scrollContent);
        }
    }

    private void FindAltButton(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag as string == "Alt")
        {
            _altButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindAltButton(fe);
                if (_altButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindAltButton(scrollContent);
        }
    }

    private void FindLangButton(FrameworkElement element)
    {
        if (element is Button btn && btn.Tag as string == "Lang")
        {
            _langButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindLangButton(fe);
                if (_langButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindLangButton(scrollContent);
        }
    }
    
    private string GetWindowTitle(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "<null>";
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}