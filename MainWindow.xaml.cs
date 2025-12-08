using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    const uint KEYEVENTF_KEYUP = 0x0002;

    public MainWindow()
    {
        this.InitializeComponent();
        Title = "Virtual Keyboard";
        
        // Get the AppWindow for advanced windowing features
        var appWindow = this.AppWindow;
        
        // Set window size to fit all keyboard buttons (712px content + 32px margins + some extra for window chrome)
        appWindow.Resize(new Windows.Graphics.SizeInt32(760, 330));
        
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
        byte vk = GetVirtualKeyCode(key);
        if (vk != 0)
        {
            // Press key
            keybd_event(vk, 0, 0, 0);
            // Release key
            keybd_event(vk, 0, KEYEVENTF_KEYUP, 0);
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