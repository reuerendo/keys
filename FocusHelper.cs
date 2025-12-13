using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard
{
    public static class FocusHelper
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        /// <summary>
        /// Try to restore foreground to specified window handle robustly.
        /// Uses AttachThreadInput to temporarily attach input queues if necessary.
        /// Returns true if foreground set (or already foreground).
        /// </summary>
        public static bool RestoreForegroundWindow(IntPtr targetWindow)
        {
            try
            {
                if (targetWindow == IntPtr.Zero)
                {
                    Logger.Warning("RestoreForegroundWindow called with null handle");
                    return false;
                }

                IntPtr currentForeground = GetForegroundWindow();
                if (currentForeground == targetWindow)
                {
                    Logger.Debug("Target window is already foreground.");
                    return true;
                }

                uint targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
                uint currentThreadId = GetCurrentThreadId();

                // If same thread, simple SetForegroundWindow works
                if (targetThreadId == currentThreadId)
                {
                    bool ok = SetForegroundWindow(targetWindow);
                    Logger.Info($"SetForegroundWindow (same thread) returned {ok}");
                    return ok;
                }

                // Attach input queues - REQUIRED for cross-thread focus
                bool attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                if (!attached)
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Error($"AttachThreadInput failed with error {err}. Cannot restore foreground to 0x{targetWindow:X}.");
                    return false;
                }

                // Set foreground
                bool result = SetForegroundWindow(targetWindow);

                // Detach threads
                AttachThreadInput(currentThreadId, targetThreadId, false);

                if (result)
                {
                    Logger.Info($"Successfully restored foreground to window 0x{targetWindow:X}");
                }
                else
                {
                    Logger.Warning($"SetForegroundWindow returned false for window 0x{targetWindow:X}");
                }

                return result;
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in RestoreForegroundWindow", ex);
                return false;
            }
        }
    }
}