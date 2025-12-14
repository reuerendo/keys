using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace VirtualKeyboard
{
    /// <summary>
    /// Manages window styles, Win32 window properties, subclassing, and title bar configuration
    /// </summary>
    public class WindowStyleManager
    {
        // Window Styles
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int GWL_WNDPROC = -4;
        
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        
        private const long WS_EX_NOACTIVATE = 0x08000000L;
        private const long WS_EX_TOPMOST = 0x00000008L;
        private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
		
		private const uint WM_MOUSEACTIVATE = 0x0021;
		private const int MA_NOACTIVATE = 3;
		private const long WS_EX_TOOLWINDOW = 0x00000080L;

        // Window Messages
        private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
        private const uint HTCAPTION = 0x0002;

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
        /// Apply WS_EX_NOACTIVATE + WS_EX_TOPMOST to prevent window from stealing focus
        /// </summary>
		public void ApplyNoActivateStyle()
		{
			try
			{
				IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
				long exStyle = exStylePtr.ToInt64();

				long newFlags = exStyle;
				newFlags |= WS_EX_NOACTIVATE;
				newFlags |= WS_EX_TOPMOST;
				newFlags |= WS_EX_TOOLWINDOW; // Prevents taskbar button and helps with activation

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

        /// <summary>
        /// Remove minimize and maximize buttons from the window
        /// </summary>
        public void RemoveMinMaxButtons()
        {
            try
            {
                IntPtr stylePtr = GetWindowLongPtr(_hwnd, GWL_STYLE);
                long style = stylePtr.ToInt64();

                long newStyle = style;
                newStyle &= ~WS_MINIMIZEBOX;
                newStyle &= ~WS_MAXIMIZEBOX;

                if (newStyle != style)
                {
                    SetWindowLongPtr(_hwnd, GWL_STYLE, (IntPtr)newStyle);
                    Logger.Info($"Removed minimize/maximize buttons. Style: 0x{newStyle:X}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to remove min/max buttons", ex);
            }
        }

        /// <summary>
        /// Subclass window to intercept double-click messages
        /// </summary>
        public void SubclassWindow()
        {
            try
            {
                _wndProcDelegate = new WndProcDelegate(WndProc);
                IntPtr newWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
                _oldWndProc = SetWindowLongPtr(_hwnd, GWL_WNDPROC, newWndProc);
                
                if (_oldWndProc == IntPtr.Zero)
                {
                    Logger.Warning("Failed to subclass window");
                }
                else
                {
                    Logger.Info($"Window subclassed successfully to intercept double-click messages. DoubleClickTime: {_doubleClickTime}ms");
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
                SetWindowLongPtr(_hwnd, GWL_WNDPROC, _oldWndProc);
                _oldWndProc = IntPtr.Zero;
                Logger.Info("Window procedure restored");
            }
        }

        /// <summary>
        /// Custom window procedure to block double-click on title bar
        /// </summary>
		private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
		{
			// Prevent activation on mouse click
			if (msg == WM_MOUSEACTIVATE)
			{
				Logger.Debug("WM_MOUSEACTIVATE intercepted - returning MA_NOACTIVATE");
				return new IntPtr(MA_NOACTIVATE);
			}

			// Block double-click on title bar
			if (msg == WM_NCLBUTTONDBLCLK && wParam.ToInt32() == HTCAPTION)
			{
				Logger.Info("Blocked double-click on title bar");
				return IntPtr.Zero;
			}

			if (_oldWndProc != IntPtr.Zero)
			{
				return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
			}

			return IntPtr.Zero;
		}

		/// <summary>
		/// Apply comprehensive no-activate styles to prevent focus stealing
		/// Call this method periodically to ensure styles remain applied
		/// </summary>
		public void EnforceNoActivateStyle()
		{
			try
			{
				IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
				long exStyle = exStylePtr.ToInt64();

				// Apply all relevant extended styles
				long newFlags = exStyle;
				newFlags |= WS_EX_NOACTIVATE;  // Don't activate on click
				newFlags |= WS_EX_TOPMOST;     // Stay on top
				newFlags |= 0x00000080L;       // WS_EX_TOOLWINDOW - prevents taskbar button

				if (newFlags != exStyle)
				{
					IntPtr result = SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)newFlags);
					
					if (result != IntPtr.Zero)
					{
						Logger.Info($"Enforced extended window styles: 0x{newFlags:X}");
					}
					else
					{
						int error = Marshal.GetLastWin32Error();
						Logger.Warning($"Failed to enforce styles. Error: {error}");
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Error("Failed to enforce no-activate style", ex);
			}
		}

		/// <summary>
		/// Verify that no-activate styles are still applied
		/// Returns true if styles are correct, false otherwise
		/// </summary>
		public bool VerifyNoActivateStyle()
		{
			try
			{
				IntPtr exStylePtr = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
				long exStyle = exStylePtr.ToInt64();

				bool hasNoActivate = (exStyle & WS_EX_NOACTIVATE) != 0;
				bool hasTopmost = (exStyle & WS_EX_TOPMOST) != 0;

				Logger.Debug($"Style check - NoActivate: {hasNoActivate}, Topmost: {hasTopmost}");

				return hasNoActivate && hasTopmost;
			}
			catch (Exception ex)
			{
				Logger.Error("Failed to verify no-activate style", ex);
				return false;
			}
		}

		/// <summary>
		/// Additional method: Prevent window from being activated via SetActiveWindow
		/// This intercepts WM_MOUSEACTIVATE messages
		/// </summary>
		private const uint WM_MOUSEACTIVATE = 0x0021;
		private const int MA_NOACTIVATE = 3;

		// Update the WndProc method to handle WM_MOUSEACTIVATE
		private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
		{
			// Prevent activation on mouse click
			if (msg == WM_MOUSEACTIVATE)
			{
				Logger.Debug("WM_MOUSEACTIVATE intercepted - returning MA_NOACTIVATE");
				return new IntPtr(MA_NOACTIVATE);
			}

			// Block double-click on title bar
			if (msg == WM_NCLBUTTONDBLCLK && wParam.ToInt32() == HTCAPTION)
			{
				Logger.Info("Blocked double-click on title bar");
				return IntPtr.Zero;
			}

			if (_oldWndProc != IntPtr.Zero)
			{
				return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
			}

			return IntPtr.Zero;
		}

        /// <summary>
        /// Configure custom title bar with close button visible
        /// </summary>
        public void ConfigureTitleBar()
        {
            try
            {
                if (!AppWindowTitleBar.IsCustomizationSupported())
                {
                    Logger.Warning("Title bar customization not supported");
                    return;
                }

                var titleBar = _window.AppWindow.TitleBar;
                titleBar.ExtendsContentIntoTitleBar = true;
                
                // Set title bar button colors - close button will be visible with default color
                titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                
                Logger.Info("Custom title bar configured - close button visible");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to configure title bar", ex);
            }
        }

        /// <summary>
        /// Configure window presenter (always on top, non-resizable)
        /// </summary>
        public void ConfigurePresenter()
        {
            try
            {
                if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = true;
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    Logger.Info("Window presenter configured");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to configure presenter", ex);
            }
        }
    }
}