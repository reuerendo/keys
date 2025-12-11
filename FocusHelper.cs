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
                if (targetWindow == IntPtr.Zero) return false;

                IntPtr currentForeground = GetForegroundWindow();
                if (currentForeground == targetWindow)
                {
                    Logger.Debug("Target window is already foreground.");
                    return true;
                }

                uint targetThreadId = GetWindowThreadProcessId(targetWindow, out _);
                uint currentThreadId = GetCurrentThreadId();

                // If same thread, simple SetForegroundWindow may work
                if (targetThreadId == currentThreadId)
                {
                    bool ok = SetForegroundWindow(targetWindow);
                    Logger.Info($"SetForegroundWindow (same thread) returned {ok}");
                    return ok;
                }

                // Temporarily attach input queues
                bool attached = AttachThreadInput(currentThreadId, targetThreadId, true);
                if (!attached)
                {
                    int err = Marshal.GetLastWin32Error();
                    Logger.Warning($"AttachThreadInput failed (errno={err}). Trying SetForegroundWindow without attaching.");
                    // fallback
                    bool okFallback = SetForegroundWindow(targetWindow);
                    Logger.Info($"SetForegroundWindow (fallback) returned {okFallback}");
                    return okFallback;
                }

                // Now set foreground
                bool result = SetForegroundWindow(targetWindow);

                // Detach threads
                AttachThreadInput(currentThreadId, targetThreadId, false);

                Logger.Info($"RestoreForegroundWindow result: {result}");
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
