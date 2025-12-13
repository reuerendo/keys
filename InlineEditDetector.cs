using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualKeyboard
{
    /// <summary>
    /// Detects inline edit controls (rename in Explorer, TreeView, ListView, etc.)
    /// </summary>
    public static class InlineEditDetector
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        /// <summary>
        /// Check if current focused control is an inline edit control
        /// </summary>
        public static bool IsInlineEditActive(out IntPtr editControl)
        {
            editControl = IntPtr.Zero;

            try
            {
                IntPtr foregroundWindow = GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero)
                    return false;

                uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
                uint currentThreadId = GetCurrentThreadId();

                bool attached = false;
                try
                {
                    // Attach to foreground window's thread to get its focused control
                    if (foregroundThreadId != currentThreadId)
                    {
                        attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                        if (!attached)
                        {
                            Logger.Debug("Failed to attach thread input for inline edit detection");
                            return false;
                        }
                    }

                    IntPtr focusedControl = GetFocus();
                    if (focusedControl == IntPtr.Zero)
                        return false;

                    if (IsInlineEditControl(focusedControl))
                    {
                        editControl = focusedControl;
                        Logger.Info($"Inline edit control detected: 0x{focusedControl:X}");
                        return true;
                    }

                    return false;
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, foregroundThreadId, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error detecting inline edit control", ex);
                return false;
            }
        }

        /// <summary>
        /// Check if a window handle is an inline edit control based on class name
        /// </summary>
        private static bool IsInlineEditControl(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;

            var sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, 256) > 0)
            {
                string className = sb.ToString();

                // Common inline edit control class names
                // "Edit" - standard edit control used for rename in Explorer
                // "DirectUIHWND" - modern Windows UI inline edit
                // Additional checks for TreeView/ListView that host edit controls
                bool isInlineEdit = className == "Edit" ||
                                   className == "DirectUIHWND" ||
                                   className.Contains("Edit");

                if (isInlineEdit)
                {
                    Logger.Debug($"Inline edit control class detected: {className}");
                }

                return isInlineEdit;
            }

            return false;
        }
    }
}