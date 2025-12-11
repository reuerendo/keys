using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard
{
    /// <summary>
    /// Manages window styles and Win32 window properties
    /// </summary>
    public class WindowStyleManager
    {
        // Window Styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;

        // P/Invoke
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private readonly IntPtr _hwnd;

        public WindowStyleManager(IntPtr windowHandle)
        {
            _hwnd = windowHandle;
        }

        /// <summary>
        /// Apply WS_EX_NOACTIVATE + WS_EX_TOPMOST to prevent window from stealing focus
        /// </summary>
        public void ApplyNoActivateStyle()
        {
            try
            {
                IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                long exStyle = exStylePtr.ToInt64();

                long newFlags = exStyle;
                newFlags |= WS_EX_NOACTIVATE;
                newFlags |= WS_EX_TOPMOST;

                if (newFlags != exStyle)
                {
                    SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)newFlags);
                    Logger.Info($"Applied extended window styles: 0x{newFlags:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to apply window style", ex);
            }
        }

        /// <summary>
        /// Remove minimize and maximize buttons from the window
        /// </summary>
        public void RemoveMinMaxButtons()
        {
            try
            {
                IntPtr stylePtr = GetWindowLongPtr(_hwnd, GWL_STYLE);
                long style = stylePtr.ToInt64();

                // Remove minimize and maximize box styles
                long newStyle = style;
                newStyle &= ~WS_MINIMIZEBOX;
                newStyle &= ~WS_MAXIMIZEBOX;

                if (newStyle != style)
                {
                    SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)newStyle);
                    Logger.Info($"Removed minimize/maximize buttons. Style: 0x{newStyle:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to remove min/max buttons", ex);
            }
        }
    }
}