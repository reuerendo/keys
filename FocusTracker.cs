using System;
using System.Runtime.InteropServices;
using System.Text;

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
            
            Logger.Info("FocusTracker initialized with hooks");
        }

        public IntPtr GetLastFocusedWindow()
        {
            return _lastInterestingWindow;
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, long idObject, long idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                // Only consider real window focus events (idObject == OBJID_WINDOW(0) and idChild == CHILDID_SELF(0))
                const long OBJID_WINDOW = 0x00000000;
                const long CHILDID_SELF = 0x00000000;
                
                if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
                    return;

                if (hwnd == IntPtr.Zero)
                    return;

                // Ignore our own window (keyboard)
                if (hwnd == _ownHwnd)
                    return;

                // Filter out shell windows (explorer/tray/taskbar)
                if (IsShellWindow(hwnd))
                    return;

                // Filter out invisible windows
                if (!IsWindowVisible(hwnd))
                    return;

                // Filter out message-only windows and other non-interactive windows
                if (!IsInteractiveWindow(hwnd))
                    return;

                // Save as last interesting window
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
            try
            {
                // Check class names for common shell windows
                const int length = 256;
                var sb = new StringBuilder(length);
                if (GetClassName(hwnd, sb, length) > 0)
                {
                    string cls = sb.ToString();
                    
                    // Common shell class names that should be ignored
                    if (cls.StartsWith("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                        cls.StartsWith("TrayNotifyWnd", StringComparison.OrdinalIgnoreCase) ||
                        cls.StartsWith("WorkerW", StringComparison.OrdinalIgnoreCase) ||
                        cls.StartsWith("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                        cls.StartsWith("Progman", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // Check if it's the actual shell window
                IntPtr shell = GetShellWindow();
                if (shell != IntPtr.Zero && shell == hwnd)
                    return true;

                // Check if process is explorer.exe
                GetWindowThreadProcessId(hwnd, out uint pid);
                try
                {
                    var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                    if (proc != null && string.Equals(proc.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                    {
                        // Additional check: Explorer main windows are usually shell windows
                        // but Explorer file dialogs are not
                        var sb2 = new StringBuilder(256);
                        if (GetClassName(hwnd, sb2, 256) > 0)
                        {
                            string cls2 = sb2.ToString();
                            // CabinetWClass = File Explorer windows (these are OK to track)
                            // ExploreWClass = older Explorer windows (these are OK to track)
                            if (cls2 == "CabinetWClass" || cls2 == "ExploreWClass")
                                return false;
                        }
                        return true;
                    }
                }
                catch
                {
                    // Process might have exited
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.Error("Error in IsShellWindow", ex);
                return false;
            }
        }

        private bool IsInteractiveWindow(IntPtr hwnd)
        {
            try
            {
                // Get window styles
                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();

                // Filter out tool windows and other non-main windows
                const long WS_EX_TOOLWINDOW = 0x00000080L;
                const long WS_EX_NOACTIVATE = 0x08000000L;

                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                    return false;

                if ((exStyle & WS_EX_NOACTIVATE) != 0)
                    return false;

                // Check if window has caption or is sizeable (main windows usually have these)
                const long WS_CAPTION = 0x00C00000L;
                const long WS_THICKFRAME = 0x00040000L;

                bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
                bool hasThickFrame = (style & WS_THICKFRAME) != 0;

                // Accept windows with caption or thick frame
                return hasCaption || hasThickFrame;
            }
            catch (Exception ex)
            {
                Logger.Error("Error in IsInteractiveWindow", ex);
                return true; // Default to true on error to be safe
            }
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
            
            Logger.Info("FocusTracker disposed");
        }

        #region PInvoke

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        #endregion
    }
}