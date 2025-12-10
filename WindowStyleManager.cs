using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Manages window styles and Win32 window properties
/// </summary>
public class WindowStyleManager
{
    // Window Styles
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    // P/Invoke
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly IntPtr _hwnd;

    public WindowStyleManager(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
    }

    /// <summary>
    /// Apply WS_EX_NOACTIVATE style to prevent window from stealing focus
    /// </summary>
    public void ApplyNoActivateStyle()
    {
        try
        {
            IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            long exStyle = exStylePtr.ToInt64();
            
            if ((exStyle & WS_EX_NOACTIVATE) == 0)
            {
                exStyle |= WS_EX_NOACTIVATE;
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)exStyle);
                Logger.Info("Applied WS_EX_NOACTIVATE style.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply window style", ex);
        }
    }
}