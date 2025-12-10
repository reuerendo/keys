using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility based on text input focus detection.
/// Uses the same approach as system IME and On-Screen Keyboard (OSK).
/// </summary>
public class AutoShowManager : IDisposable
{
    // WinEvent constants
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    
    // Window class name buffer size
    private const int MAX_CLASS_NAME = 256;
    
    private readonly IntPtr _keyboardWindowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    
    private IntPtr _focusHook;
    private WinEventDelegate _hookDelegate;
    private System.Threading.Timer _focusCheckTimer;
    
    private IntPtr _lastCheckedWindow = IntPtr.Zero;
    private bool _lastInputState = false;
    
    private bool _isEnabled;
    private bool _isDisposed;

    public event EventHandler ShowKeyboardRequested;
    public event EventHandler HideKeyboardRequested;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled)
                    Start();
                else
                    Stop();
                
                Logger.Info($"AutoShow {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        
        Logger.Info("AutoShowManager initialized");
    }

    private void Start()
    {
        if (_focusHook != IntPtr.Zero)
        {
            Logger.Warning("AutoShow already started");
            return;
        }

        try
        {
            // Create delegate and keep reference to prevent GC
            _hookDelegate = new WinEventDelegate(OnFocusChanged);
            
            // Hook focus change events
            _focusHook = SetWinEventHook(
                EVENT_OBJECT_FOCUS,
                EVENT_OBJECT_FOCUS,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (_focusHook != IntPtr.Zero)
            {
                Logger.Info("✓ Focus event hook installed");
                
                // Start polling timer as backup (500ms interval)
                _focusCheckTimer = new System.Threading.Timer(
                    CheckFocusState, 
                    null, 
                    500, 
                    500);
                
                Logger.Info("✓ Focus polling timer started");
                
                // Check initial state
                CheckFocusState(null);
            }
            else
            {
                Logger.Error("Failed to install focus event hook");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start AutoShow", ex);
        }
    }

    private void Stop()
    {
        _focusCheckTimer?.Dispose();
        _focusCheckTimer = null;

        if (_focusHook != IntPtr.Zero)
        {
            UnhookWinEvent(_focusHook);
            _focusHook = IntPtr.Zero;
            Logger.Info("Focus event hook removed");
        }
        
        _lastCheckedWindow = IntPtr.Zero;
        _lastInputState = false;
    }

    /// <summary>
    /// Called when focus changes (via WinEvent hook)
    /// </summary>
    private void OnFocusChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || hwnd == _keyboardWindowHandle)
            return;

        try
        {
            ProcessFocusChange(hwnd);
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in focus change handler: {ex.Message}");
        }
    }

    /// <summary>
    /// Polling timer callback - checks focus state periodically
    /// </summary>
    private void CheckFocusState(object state)
    {
        if (_isDisposed)
            return;

        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero || foregroundWindow == _keyboardWindowHandle)
                return;

            // Only recheck if foreground window changed
            if (foregroundWindow != _lastCheckedWindow)
            {
                IntPtr focusedControl = GetFocus();
                ProcessFocusChange(focusedControl != IntPtr.Zero ? focusedControl : foregroundWindow);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in focus check timer: {ex.Message}");
        }
    }

    /// <summary>
    /// Core logic: determine if focused window is a text input control
    /// </summary>
    private void ProcessFocusChange(IntPtr hwnd)
    {
        if (hwnd == _lastCheckedWindow)
            return;

        _lastCheckedWindow = hwnd;
        
        bool isTextInput = IsTextInputControl(hwnd);
        
        // Only trigger events on state change
        if (isTextInput != _lastInputState)
        {
            _lastInputState = isTextInput;
            
            string className = GetWindowClassName(hwnd);
            Logger.Info($"Input state changed: {isTextInput} (HWND=0x{hwnd:X}, Class={className})");
            
            _dispatcherQueue.TryEnqueue(() =>
            {
                if (isTextInput)
                    ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
                else
                    HideKeyboardRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>
    /// Determines if the given window handle is a text input control.
    /// Uses the same detection logic as system IME and OSK.
    /// </summary>
    private bool IsTextInputControl(IntPtr hwnd)
    {
        try
        {
            string className = GetWindowClassName(hwnd);
            if (string.IsNullOrEmpty(className))
                return false;

            // Known text input control classes
            string[] textInputClasses = new[]
            {
                // Standard Win32 edit controls
                "Edit",
                "RichEdit",
                "RICHEDIT20A",
                "RICHEDIT20W",
                "RICHEDIT50W",
                
                // Browser content areas (Chromium, Firefox, IE)
                "Chrome_RenderWidgetHostHWND",
                "MozillaContentWindowClass",
                "MozillaWindowClass",
                "Internet Explorer_Server",
                
                // Office applications
                "_WwG",                    // Word
                "EXCEL7",                  // Excel
                "mdiClass",                // Office MDI child
                
                // WinUI 3 / UWP controls
                "TextBox",
                "RichEditBox",
                "Windows.UI.Core.CoreWindow",
                
                // Code editors
                "Scintilla",
                "SciTEWindow",
                
                // Console
                "ConsoleWindowClass",
                
                // Other common controls
                "ThunderRT6TextBox",       // VB6
                "WindowsForms10.EDIT",     // WinForms
            };

            // Direct class name match
            foreach (string inputClass in textInputClasses)
            {
                if (className.Equals(inputClass, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Debug($"✓ Text input detected: {className}");
                    return true;
                }
            }

            // Partial match for versioned controls (e.g., "WindowsForms10.EDIT.app.0.378734a")
            if (className.Contains("EDIT", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase))
            {
                // Verify it's not read-only
                if (!IsReadOnly(hwnd))
                {
                    Logger.Debug($"✓ Text input detected (partial match): {className}");
                    return true;
                }
            }

            // For unknown controls, check if they're editable and have text input capability
            if (IsEditableControl(hwnd) && CanAcceptText(hwnd))
            {
                Logger.Debug($"✓ Text input detected (editable control): {className}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in IsTextInputControl: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if control is read-only
    /// </summary>
    private bool IsReadOnly(IntPtr hwnd)
    {
        const int GWL_STYLE = -16;
        const int ES_READONLY = 0x0800;
        
        try
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);
            return (style & ES_READONLY) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if control can be edited (not disabled, not read-only)
    /// </summary>
    private bool IsEditableControl(IntPtr hwnd)
    {
        const int GWL_STYLE = -16;
        const int WS_DISABLED = 0x08000000;
        const int ES_READONLY = 0x0800;
        
        try
        {
            int style = GetWindowLong(hwnd, GWL_STYLE);
            return (style & WS_DISABLED) == 0 && (style & ES_READONLY) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if control can accept text input (has text caret)
    /// </summary>
    private bool CanAcceptText(IntPtr hwnd)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            
            GUITHREADINFO guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = Marshal.SizeOf(guiInfo);
            
            if (GetGUIThreadInfo(threadId, ref guiInfo))
            {
                // Check if this window or its focus has a text caret
                return guiInfo.hwndCaret != IntPtr.Zero && 
                       guiInfo.hwndCaret != _keyboardWindowHandle;
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets window class name
    /// </summary>
    private string GetWindowClassName(IntPtr hwnd)
    {
        StringBuilder className = new StringBuilder(MAX_CLASS_NAME);
        
        if (GetClassName(hwnd, className, MAX_CLASS_NAME) > 0)
        {
            return className.ToString();
        }
        
        return string.Empty;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        
        _isDisposed = true;
        Stop();
        
        Logger.Info("AutoShowManager disposed");
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
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, 
        IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, 
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}