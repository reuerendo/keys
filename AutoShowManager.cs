using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility using Windows Events API with enhanced detection.
/// Works with Browsers, Office, UWP, WPF, and standard Win32 apps.
/// </summary>
public class AutoShowManager : IDisposable
{
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    
    private readonly IntPtr _keyboardWindowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    
    private IntPtr _hookHandleFocus;
    private IntPtr _hookHandleValueChange;
    private WinEventDelegate _hookDelegate;
    private System.Threading.Timer _focusCheckTimer;
    private IntPtr _lastFocusedWindow = IntPtr.Zero;
    
    private bool _isEnabled;
    private bool _isDisposed;

    public event EventHandler ShowKeyboardRequested;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled)
                    SubscribeToFocusEvents();
                else
                    UnsubscribeFromFocusEvents();
                
                Logger.Info($"AutoShow (WinEvents Enhanced) {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        InitializeWinEvents();
    }

    private void InitializeWinEvents()
    {
        try
        {
            Logger.Info("Initializing AutoShow using Enhanced Windows Events API...");
            
            _hookDelegate = new WinEventDelegate(WinEventProc);
            
            Logger.Info("✓ AutoShow (WinEvents Enhanced) initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize Windows Events", ex);
        }
    }

    private void SubscribeToFocusEvents()
    {
        if (_hookHandleFocus != IntPtr.Zero)
        {
            Logger.Warning("Already subscribed to focus events");
            return;
        }

        try
        {
            // Hook 1: Main focus events
            _hookHandleFocus = SetWinEventHook(
                EVENT_OBJECT_FOCUS,
                EVENT_OBJECT_FOCUS,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            // Hook 2: Value change events (for input fields)
            _hookHandleValueChange = SetWinEventHook(
                EVENT_OBJECT_VALUECHANGE,
                EVENT_OBJECT_VALUECHANGE,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (_hookHandleFocus != IntPtr.Zero)
            {
                Logger.Info("✓ Subscribed to focus events (WinEvents Enhanced)");
                
                // Start timer for periodic focus checking (for complex applications)
                _focusCheckTimer = new System.Threading.Timer(CheckFocusTimer, null, 500, 500);
                Logger.Info("✓ Started focus monitoring timer");
            }
            else
            {
                Logger.Error("Failed to set WinEvent hooks");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to subscribe to focus events", ex);
        }
    }

    private void UnsubscribeFromFocusEvents()
    {
        _focusCheckTimer?.Dispose();
        _focusCheckTimer = null;

        if (_hookHandleFocus != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandleFocus);
            _hookHandleFocus = IntPtr.Zero;
        }

        if (_hookHandleValueChange != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandleValueChange);
            _hookHandleValueChange = IntPtr.Zero;
        }

        Logger.Info("Unsubscribed from focus events");
    }

    private void CheckFocusTimer(object state)
    {
        if (_isDisposed) return;

        try
        {
            IntPtr focusedWindow = GetForegroundWindow();
            
            if (focusedWindow == IntPtr.Zero || focusedWindow == _keyboardWindowHandle)
                return;

            // Check only if focus changed
            if (focusedWindow != _lastFocusedWindow)
            {
                _lastFocusedWindow = focusedWindow;
                
                // Check for text caret presence
                if (HasTextCaret(focusedWindow) && IsActualInputElement(focusedWindow))
                {
                    Logger.Info($"Text input detected via timer: HWND=0x{focusedWindow:X}");
                    
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        OnShowKeyboardRequested();
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in focus check timer: {ex.Message}");
        }
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || hwnd == _keyboardWindowHandle)
            return;

        try
        {
            if (IsActualInputElement(hwnd))
            {
                string className = GetWindowClassName(hwnd);
                Logger.Info($"Input focus detected: HWND=0x{hwnd:X}, Class={className}, Event=0x{eventType:X}");

                _dispatcherQueue.TryEnqueue(() =>
                {
                    OnShowKeyboardRequested();
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error processing focus event: {ex.Message}");
        }
    }

    private bool IsActualInputElement(IntPtr hwnd)
    {
        try
        {
            string className = GetWindowClassName(hwnd);
            
            // Exclude main application windows (browsers, editors main windows)
            string[] excludedClasses = new[]
            {
                "MozillaWindowClass",           // Firefox main window
                "Chrome_WidgetWin_1",            // Chrome main window
                "ApplicationFrameWindow",        // UWP app frame
                "Notepad",                       // Notepad main window
                "OpusApp",                       // Word main window
                "XLMAIN",                        // Excel main window
                "PPTFrameClass",                 // PowerPoint main window
                "SciTEWindow"                    // SciTE main window
            };

            // Check if this is an excluded main window
            foreach (string excludedClass in excludedClasses)
            {
                if (className.Equals(excludedClass, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Debug($"Excluded main window: {className}");
                    return false;
                }
            }

            // Specific input element classes
            string[] inputClasses = new[]
            {
                // Standard Windows input controls
                "Edit", "RichEdit", "RICHEDIT", "RICHEDIT20", "RICHEDIT50W",
                
                // Chromium browsers (Chrome, Edge, Opera, Brave) - content areas only
                "Chrome_RenderWidgetHostHWND",
                "Chrome_WidgetWin_0",
                
                // Firefox content areas
                "MozillaContentWindowClass",
                
                // Internet Explorer
                "Internet Explorer_Server",
                
                // Office input areas
                "_WwG",
                "EXCEL7",
                
                // UWP / WinUI 3 input controls
                "TextBox", "RichEditBox",
                "Windows.UI.Core.CoreWindow",
                
                // WPF (needs additional checking)
                "HwndWrapper",
                
                // Other input controls
                "ThunderRT6TextBox",     // Visual Basic
                "Scintilla",             // Code editors
                "ConsoleWindowClass"     // Console
            };

            // Check direct match or partial match
            foreach (string inputClass in inputClasses)
            {
                if (className.Equals(inputClass, StringComparison.OrdinalIgnoreCase) ||
                    className.Contains(inputClass, StringComparison.OrdinalIgnoreCase))
                {
                    // Additional check for text caret to ensure it's really an input field
                    if (HasTextCaret(hwnd))
                    {
                        Logger.Debug($"Valid input element with caret: {className}");
                        return true;
                    }
                }
            }

            // For complex applications, check if there's a text caret
            // and the element has input-related styles
            if (HasTextCaret(hwnd) && IsEditableControl(hwnd))
            {
                Logger.Debug($"Text caret detected in editable control: {className}");
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool IsEditableControl(IntPtr hwnd)
    {
        try
        {
            // Check if control has ES_READONLY style (if not, it's editable)
            const int GWL_STYLE = -16;
            const int ES_READONLY = 0x0800;
            
            int style = GetWindowLong(hwnd, GWL_STYLE);
            
            // If ES_READONLY is not set, it's editable
            return (style & ES_READONLY) == 0;
        }
        catch
        {
            return false;
        }
    }

    private string GetWindowClassName(IntPtr hwnd)
    {
        const int maxClassNameLength = 256;
        System.Text.StringBuilder className = new System.Text.StringBuilder(maxClassNameLength);
        
        if (GetClassName(hwnd, className, maxClassNameLength) > 0)
        {
            return className.ToString();
        }
        
        return string.Empty;
    }

    private bool HasTextCaret(IntPtr hwnd)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            
            GUITHREADINFO guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = Marshal.SizeOf(guiInfo);
            
            if (GetGUIThreadInfo(threadId, ref guiInfo))
            {
                // If there's a caret and the caret window is not our keyboard
                if (guiInfo.hwndCaret != IntPtr.Zero && guiInfo.hwndCaret != _keyboardWindowHandle)
                {
                    return true;
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void OnShowKeyboardRequested()
    {
        ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        UnsubscribeFromFocusEvents();
        
        Logger.Info("AutoShowManager (WinEvents Enhanced) disposed");
    }

    // =========================================================================
    // Native Methods & Structures
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public System.Drawing.Rectangle rcCaret;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, 
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}
