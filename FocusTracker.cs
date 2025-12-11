using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard
{
    /// <summary>
    /// Tracks the last 'interesting' focused window (the one that user had focus in),
    /// using WinEvent hooks. Use GetLastFocusedWindow() when you need to restore focus
    /// after an external focus grab (e.g. click on tray).
    /// </summary>
    public class FocusTracker : IDisposable
    {
        // WinEvent constants
        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType,
            IntPtr hwnd, long idObject, long idChild, uint dwEventThread, uint dwmsEventTime);

        private WinEventDelegate _procDelegate;
        private IntPtr _hook1 = IntPtr.Zero;
        private IntPtr _hook2 = IntPtr.Zero;

        private IntPtr _lastInterestingWindow = IntPtr.Zero;
        private readonly IntPtr _ownHwnd;

        public FocusTracker(IntPtr ownWindowHandle)
        {
            _ownHwnd = ownWindowHandle;
            _procDelegate = new WinEventDelegate(WinEventProc);
            // Hook both focus and foreground changes
            _hook1 = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _procDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            _hook2 = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _procDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
        }

        public IntPtr GetLastFocusedWindow()
        {
            return _lastInterestingWindow;
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, long idObject, long idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                // We only consider real window focus events (idObject == OBJID_WINDOW(0) and idChild == CHILDID_SELF(0))
                const long OBJID_WINDOW = 0x00000000;
                const long CHILDID_SELF = 0x00000000;
                if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
                    return;

                if (hwnd == IntPtr.Zero)
                    return;

                // Ignore our own window (keyboard) and tray message window if desired
                if (hwnd == _ownHwnd)
                    return;

                // Filter out shell windows (explorer/tray/taskbar) — we don't want to set them as "last interesting"
                if (IsShellWindow(hwnd))
                    return;

                // Optionally: filter out invisible/disabled windows etc.
                if (!IsWindowVisible(hwnd))
                    return;

                // Save as last interesting
                _lastInterestingWindow = hwnd;
                Logger.Debug($"FocusTracker: saved last interesting window 0x{hwnd.ToInt64():X}");
            }
            catch (Exception ex)
            {
                Logger.Error("FocusTracker WinEventProc error", ex);
            }
        }

        private bool IsShellWindow(IntPtr hwnd)
        {
            // Quick heuristics: check class names or process names to avoid storing Explorer/tray
            const int length = 256;
            var sb = new System.Text.StringBuilder(length);
            if (GetClassName(hwnd, sb, length) > 0)
            {
                string cls = sb.ToString();
                // Common shell class names: "Shell_TrayWnd", "TrayNotifyWnd", "WorkerW", "Shell_SecondaryTrayWnd"
                if (cls.StartsWith("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                    || cls.StartsWith("TrayNotifyWnd", StringComparison.OrdinalIgnoreCase)
                    || cls.StartsWith("WorkerW", StringComparison.OrdinalIgnoreCase)
                    || cls.StartsWith("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Also exclude the actual shell window handle returned by GetShellWindow()
            IntPtr shell = GetShellWindow();
            if (shell != IntPtr.Zero && shell == hwnd)
                return true;

            // Optionally check process name = explorer.exe
            GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                if (proc != null && string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { /* ignore */ }

            return false;
        }

        public void Dispose()
        {
            if (_hook1 != IntPtr.Zero)
            {
                UnhookWinEvent(_hook1);
                _hook1 = IntPtr.Zero;
            }
            if (_hook2 != IntPtr.Zero)
            {
                UnhookWinEvent(_hook2);
                _hook2 = IntPtr.Zero;
            }
        }

        #region PInvoke
        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        #endregion
    }
}
