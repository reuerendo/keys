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
    
    private IntPtr _lastFocusedControl = IntPtr.Zero;
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
        
        _lastFocusedControl = IntPtr.Zero;
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
            // hwnd from WinEvent is the actual focused control
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
            // Get the actual focused control from the foreground window's thread
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero || foregroundWindow == _keyboardWindowHandle)
            {
                // No foreground window or keyboard has focus
                ProcessFocusChange(IntPtr.Zero);
                return;
            }

            // Get the thread's focused control
            IntPtr focusedControl = GetThreadFocusedControl(foregroundWindow);
            
            if (focusedControl != IntPtr.Zero)
            {
                ProcessFocusChange(focusedControl);
            }
            else
            {
                // No specific control focused, check the window itself
                ProcessFocusChange(foregroundWindow);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in focus check timer: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the focused control in the foreground window's thread
    /// </summary>
    private IntPtr GetThreadFocusedControl(IntPtr foregroundWindow)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(foregroundWindow, out _);
            
            GUITHREADINFO guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = Marshal.SizeOf(guiInfo);
            
            if (GetGUIThreadInfo(threadId, ref guiInfo))
            {
                // Return the focused control if it exists
                if (guiInfo.hwndFocus != IntPtr.Zero)
                {
                    return guiInfo.hwndFocus;
                }
            }
            
            return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Core logic: determine if focused control is a text input
    /// </summary>
    private void ProcessFocusChange(IntPtr hwnd)
    {
        // Skip if same control is still focused
        if (hwnd == _lastFocusedControl)
            return;

        _lastFocusedControl = hwnd;
        
        bool isTextInput = hwnd != IntPtr.Zero && IsTextInputControl(hwnd);
        
        // Only trigger events on state change
        if (isTextInput != _lastInputState)
        {
            _lastInputState = isTextInput;
            
            if (hwnd != IntPtr.Zero)
            {
                string className = GetWindowClassName(hwnd);
                Logger.Info($"Input state changed: {isTextInput} (HWND=0x{hwnd:X}, Class={className})");
            }
            else
            {
                Logger.Info($"Input state changed: {isTextInput} (no focus)");
            }
            
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
                    // For browser content areas, verify text input is actually active
                    if (className.Contains("Chrome_RenderWidgetHostHWND") ||
                        className.Contains("MozillaContentWindowClass"))
                    {
                        bool hasTextInput = HasTextCaret(hwnd);
                        Logger.Debug($"Browser content area: {className}, HasCaret={hasTextInput}");
                        return hasTextInput;
                    }
                    
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

            // For unknown controls, check if they have text caret (actual text input active)
            if (HasTextCaret(hwnd) && IsEditableControl(hwnd))
            {
                Logger.Debug($"✓ Text input detected (has caret): {className}");
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
    /// Checks if control has text caret (text cursor)
    /// </summary>
    private bool HasTextCaret(IntPtr hwnd)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            
            GUITHREADINFO guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = Marshal.SizeOf(guiInfo);
            
            if (GetGUIThreadInfo(threadId, ref guiInfo))
            {
                // Check if there's a text caret and it belongs to this control or its children
                if (guiInfo.hwndCaret != IntPtr.Zero && guiInfo.hwndCaret != _keyboardWindowHandle)
                {
                    // Caret can be in the control itself or its child
                    IntPtr caretParent = guiInfo.hwndCaret;
                    for (int i = 0; i < 10; i++)
                    {
                        if (caretParent == hwnd)
                            return true;
                        
                        caretParent = GetParent(caretParent);
                        if (caretParent == IntPtr.Zero)
                            break;
                    }
                    
                    // Also check if hwnd is the caret itself
                    if (guiInfo.hwndCaret == hwnd)
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
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
}