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

        /// <summary>
        /// Check if a window handle (already captured) is an inline edit control
        /// Non-invasive check that doesn't disturb focus
        /// </summary>
        public static bool IsInlineEditControl(IntPtr controlHandle)
        {
            if (controlHandle == IntPtr.Zero)
                return false;

            try
            {
                var sb = new StringBuilder(256);
                if (GetClassName(controlHandle, sb, 256) > 0)
                {
                    string className = sb.ToString();

                    // Common inline edit control class names
                    // "Edit" - standard edit control used for rename in Explorer
                    // "DirectUIHWND" - modern Windows UI inline edit
                    bool isInlineEdit = className == "Edit" ||
                                       className == "DirectUIHWND" ||
                                       className.Contains("Edit");

                    if (isInlineEdit)
                    {
                        Logger.Info($"Inline edit control detected: class={className}, handle=0x{controlHandle:X}");
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error("Error checking if control is inline edit", ex);
                return false;
            }
        }
    }
}