using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard
{
    /// <summary>
    /// Manages window styles and Win32 window properties
    /// </summary>
    public class WindowStyleManager
    {
        // Window long indices
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        // Window styles
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;

        // Extended styles
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOPMOST = 0x00000008L;

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
        /// Applies all required modifications:
        /// - Removes minimize & maximize buttons
        /// - Prevents window from stealing focus (WS_EX_NOACTIVATE)
        /// - Keeps window always on top (WS_EX_TOPMOST)
        /// </summary>
        public void ApplyOptimizedStyles()
        {
            try
            {
                ModifyMainStyle();
                ModifyExtendedStyle();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to apply optimized window styles", ex);
            }
        }

        private void ModifyMainStyle()
        {
            IntPtr stylePtr = GetWindowLongPtr(_hwnd, GWL_STYLE);
            long style = stylePtr.ToInt64();

            long newStyle = style;

            // Remove minimize & maximize buttons
            newStyle &= ~WS_MINIMIZEBOX;
            newStyle &= ~WS_MAXIMIZEBOX;

            if (newStyle != style)
            {
                SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)newStyle);
                Logger.Info($"Updated GWL_STYLE: 0x{newStyle:X}");
            }
        }

        private void ModifyExtendedStyle()
        {
            IntPtr exPtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            long exStyle = exPtr.ToInt64();

            long newStyle = exStyle;

            // Add required flags
            newStyle |= WS_EX_NOACTIVATE;
            newStyle |= WS_EX_TOPMOST;

            if (newStyle != exStyle)
            {
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)newStyle);
                Logger.Info($"Updated GWL_EXSTYLE: 0x{newStyle:X}");
            }
        }
    }
}
