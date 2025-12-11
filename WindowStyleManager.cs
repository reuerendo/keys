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
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L; // optional, may help with rendering/compat

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
        /// Apply WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW + WS_EX_TOPMOST to prevent window from stealing focus
        /// </summary>
        public void ApplyNoActivateStyle()
        {
            try
            {
                IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                long exStyle = exStylePtr.ToInt64();

                long newFlags = exStyle;
                // ensure we set required flags; use bitwise OR to preserve existing flags
                newFlags |= WS_EX_NOACTIVATE;
                newFlags |= WS_EX_TOPMOST;
                newFlags |= WS_EX_TOOLWINDOW;
                // optional: newFlags |= WS_EX_NOREDIRECTIONBITMAP;

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
    }
}
