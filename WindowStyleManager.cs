using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard
{
    /// <summary>
    /// Manages window styles and Win32 window properties
    /// </summary>
    public class WindowStyleManager
    {
        // Window Long indexes
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        // Standard window styles
        private const long WS_MINIMIZEBOX = 0x00020000L;  // кнопка свернуть
        private const long WS_MAXIMIZEBOX = 0x00010000L;  // кнопка развернуть
        private const long WS_THICKFRAME  = 0x00040000L;  // рамка ресайза

        // Extended window styles
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        // private const long WS_EX_TOOLWINDOW = 0x00000080L;  // маленькое окно без иконки

        // SetWindowPos flags
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        // P/Invoke
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private readonly IntPtr _hwnd;

        public WindowStyleManager(IntPtr windowHandle)
        {
            _hwnd = windowHandle;
        }

        /// <summary>
        /// Removes minimize/maximize buttons and window resize border
        /// </summary>
        public void RemoveMinMaxButtons()
        {
            try
            {
                IntPtr stylePtr = GetWindowLongPtr(_hwnd, GWL_STYLE);
                long style = stylePtr.ToInt64();
                long newStyle = style;

                // Remove buttons and resize border
                newStyle &= ~WS_MINIMIZEBOX;
                newStyle &= ~WS_MAXIMIZEBOX;
                newStyle &= ~WS_THICKFRAME;

                if (newStyle != style)
                {
                    SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)newStyle);

                    // Force window to update its frame
                    SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);

                    Logger.Info($"Updated normal window style: 0x{newStyle:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to remove min/max buttons", ex);
            }
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

                // Prevent activation and make always on top
                newFlags |= WS_EX_NOACTIVATE;
                newFlags |= WS_EX_TOPMOST;
                // newFlags |= WS_EX_TOOLWINDOW; // <-- If you want no taskbar button and small title bar

                if (newFlags != exStyle)
                {
                    SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)newFlags);
                    Logger.Info($"Applied extended window styles: 0x{newFlags:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to apply window exstyle", ex);
            }
        }
    }
}
