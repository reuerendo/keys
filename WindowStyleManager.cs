using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard
{
    /// <summary>
    /// Manages window styles with detailed debugging and verification
    /// Ensures WS_EX_NOACTIVATE is properly applied and persists
    /// </summary>
    public class WindowStyleManager
    {
        // Window Style Constants
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int GWL_WNDPROC = -4;
        
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const long WS_CAPTION = 0x00C00000L;
        
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const long WS_EX_TOOLWINDOW = 0x00000080L;
        private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;

        // Window Messages
        private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
        private const uint WM_ACTIVATE = 0x0006;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint HTCAPTION = 0x0002;
        private const uint MA_NOACTIVATE = 0x0003;

        // P/Invoke
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly IntPtr _hwnd;
        private readonly Window _window;
        private WndProcDelegate _wndProcDelegate;
        private IntPtr _oldWndProc;
        private uint _doubleClickTime;

        public WindowStyleManager(IntPtr windowHandle, Window window)
        {
            _hwnd = windowHandle;
            _window = window;
            _doubleClickTime = GetDoubleClickTime();
        }

        /// <summary>
        /// Apply WS_EX_NOACTIVATE (TOPMOST handled by Presenter)
        /// CRITICAL: This must be called BEFORE any window show operations
        /// </summary>
        public void ApplyNoActivateStyle()
        {
            Logger.Info("═══════════════════════════════════════════════════════");
            Logger.Info("▶ ApplyNoActivateStyle: Starting");
            
            try
            {
                // Get current extended style
                IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                long currentExStyle = exStylePtr.ToInt64();
                
                Logger.Info($"│ Current extended style: 0x{currentExStyle:X16}");
                LogExtendedStyleFlags(currentExStyle, "BEFORE");
                
                // Build new style - ONLY WS_EX_NOACTIVATE
                // NOTE: WS_EX_TOPMOST doesn't work in extended styles for WinUI3
                // We use Presenter.IsAlwaysOnTop instead
                long newExStyle = currentExStyle;
                newExStyle |= WS_EX_NOACTIVATE;  // ✅ CRITICAL: Prevent activation
                
                // Apply if changed
                if (newExStyle != currentExStyle)
                {
                    Logger.Info($"│ Applying new extended style: 0x{newExStyle:X16}");
                    
                    IntPtr result = SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)newExStyle);
                    
                    if (result != IntPtr.Zero)
                    {
                        Logger.Info("│ ✅ SetWindowLongPtr succeeded");
                        
                        // Verify the change
                        VerifyExtendedStyle(newExStyle);
                    }
                    else
                    {
                        int error = Marshal.GetLastWin32Error();
                        Logger.Error($"│ ❌ SetWindowLongPtr failed with error {error}");
                    }
                }
                else
                {
                    Logger.Info("│ ✅ Extended style already correct");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to apply window style", ex);
            }
            
            Logger.Info("═══════════════════════════════════════════════════════");
        }

        /// <summary>
        /// Verify that extended style was properly applied
        /// </summary>
        private void VerifyExtendedStyle(long expectedStyle)
        {
            try
            {
                IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
                long actualStyle = exStylePtr.ToInt64();
                
                Logger.Info($"│ Verification: 0x{actualStyle:X16}");
                LogExtendedStyleFlags(actualStyle, "AFTER");
                
                bool hasNoActivate = (actualStyle & WS_EX_NOACTIVATE) != 0;
                
                if (hasNoActivate)
                {
                    Logger.Info("│ ✅ VERIFICATION PASSED: WS_EX_NOACTIVATE is set");
                }
                else
                {
                    Logger.Error($"│ ❌ VERIFICATION FAILED: WS_EX_NOACTIVATE not set");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to verify extended style", ex);
            }
        }

        /// <summary>
        /// Log individual extended style flags
        /// </summary>
        private void LogExtendedStyleFlags(long style, string context)
        {
            Logger.Debug($"│ Extended Style Flags ({context}):");
            Logger.Debug($"│   WS_EX_NOACTIVATE:  {((style & WS_EX_NOACTIVATE) != 0 ? "✅ SET" : "❌ NOT SET")}");
            Logger.Debug($"│   WS_EX_TOPMOST:     {((style & WS_EX_TOPMOST) != 0 ? "SET (unused)" : "not set (using Presenter.IsAlwaysOnTop)")}");
            Logger.Debug($"│   WS_EX_TOOLWINDOW:  {((style & WS_EX_TOOLWINDOW) != 0 ? "SET" : "not set")}");
        }

        /// <summary>
        /// Remove minimize and maximize buttons
        /// </summary>
        public void RemoveMinMaxButtons()
        {
            Logger.Info("▶ RemoveMinMaxButtons: Starting");
            
            try
            {
                IntPtr stylePtr = GetWindowLongPtr(_hwnd, GWL_STYLE);
                long currentStyle = stylePtr.ToInt64();
                
                Logger.Debug($"│ Current style: 0x{currentStyle:X16}");
                
                long newStyle = currentStyle;
                newStyle &= ~WS_MINIMIZEBOX;
                newStyle &= ~WS_MAXIMIZEBOX;
                
                if (newStyle != currentStyle)
                {
                    SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)newStyle);
                    Logger.Info($"│ ✅ Min/Max buttons removed. New style: 0x{newStyle:X16}");
                }
                else
                {
                    Logger.Debug("│ Min/Max buttons already removed");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to remove min/max buttons", ex);
            }
        }

        /// <summary>
        /// Subclass window to intercept activation and double-click messages
        /// </summary>
        public void SubclassWindow()
        {
            Logger.Info("▶ SubclassWindow: Starting");
            
            try
            {
                _wndProcDelegate = new WndProcDelegate(WndProc);
                IntPtr newWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
                _oldWndProc = SetWindowLongPtr(_hwnd, GWL_WNDPROC, newWndProc);
                
                if (_oldWndProc == IntPtr.Zero)
                {
                    Logger.Warning("│ ⚠ Failed to subclass window");
                }
                else
                {
                    Logger.Info($"│ ✅ Window subclassed (DoubleClickTime: {_doubleClickTime}ms)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to subclass window", ex);
            }
        }

        /// <summary>
        /// Restore original window procedure
        /// </summary>
        public void RestoreWindowProc()
        {
            if (_oldWndProc != IntPtr.Zero)
            {
                Logger.Info("▶ RestoreWindowProc: Restoring original window procedure");
                SetWindowLongPtr(_hwnd, GWL_WNDPROC, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
            }
        }

        /// <summary>
        /// Custom window procedure - intercepts messages that could cause activation
        /// </summary>
        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_NCLBUTTONDBLCLK when wParam.ToInt32() == HTCAPTION:
                    Logger.Debug("WndProc: Blocked title bar double-click (prevent maximize)");
                    return IntPtr.Zero;

                case WM_MOUSEACTIVATE:
                    Logger.Debug("WndProc: WM_MOUSEACTIVATE - returning MA_NOACTIVATE");
                    return new IntPtr(MA_NOACTIVATE);

                case WM_ACTIVATE:
                    int activateType = wParam.ToInt32() & 0xFFFF;
                    Logger.Debug($"WndProc: WM_ACTIVATE - type={activateType} (0=deactivate, 1=click, 2=other)");
                    break;
            }

            if (_oldWndProc != IntPtr.Zero)
            {
                return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
            }

            return IntPtr.Zero;
        }

        /// <summary>
        /// Configure custom title bar
        /// </summary>
        public void ConfigureTitleBar()
        {
            Logger.Info("▶ ConfigureTitleBar: Starting");
            
            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported())
                {
                    Logger.Warning("│ ⚠ Title bar customization not supported");
                    return;
                }

                var titleBar = _window.AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                
                // Transparent buttons except close button
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                
                Logger.Info("│ ✅ Title bar configured (close button visible)");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to configure title bar", ex);
            }
        }

        /// <summary>
        /// Configure window presenter
        /// </summary>
        public void ConfigurePresenter()
        {
            Logger.Info("▶ ConfigurePresenter: Starting");
            
            try
            {
                if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    
                    Logger.Info("│ ✅ Presenter configured");
                    Logger.Debug($"│   IsAlwaysOnTop: {presenter.IsAlwaysOnTop}");
                    Logger.Debug($"│   IsResizable: {presenter.IsResizable}");
                }
                else
                {
                    Logger.Warning("│ ⚠ Presenter is not OverlappedPresenter");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to configure presenter", ex);
            }
        }
    }
}