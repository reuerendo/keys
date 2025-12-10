using System;
using System.Runtime.InteropServices;
using System.Text;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard display when text input fields receive focus
/// </summary>
public class AutoShowManager : IDisposable
{
    #region Win32 Constants and Delegates

    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    #endregion

    #region Win32 API Imports

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    #endregion

    #region Fields

    private readonly IntPtr _keyboardWindowHandle;
    private IntPtr _hookHandle;
    private WinEventDelegate _hookDelegate;
    private bool _isEnabled;
    private bool _disposed;
    
    // Track last focused window to avoid duplicate triggers
    private IntPtr _lastFocusedWindow = IntPtr.Zero;

    // Known text input control class names
    private static readonly string[] TextInputClasses = new[]
    {
        "Edit",                    // Standard Windows edit control
        "RichEdit",                // Rich edit control
        "RichEdit20A",             // Rich edit 2.0 ANSI
        "RichEdit20W",             // Rich edit 2.0 Unicode
        "RichEdit50W",             // Rich edit 5.0
        "RICHEDIT60W",             // Rich edit 6.0
        "TextBox",                 // WPF TextBox
        "TextBlock",               // WPF TextBlock
        "Windows.UI.Xaml.Controls.TextBox", // WinUI TextBox
        "Chrome_RenderWidgetHostHWND", // Chrome/Edge browser text fields
        "MozillaWindowClass",      // Firefox
    };

    #endregion

    #region Events

    /// <summary>
    /// Raised when keyboard should be shown due to text field focus
    /// </summary>
    public event EventHandler ShowKeyboardRequested;

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets whether auto-show is enabled
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                
                if (_isEnabled)
                {
                    StartMonitoring();
                }
                else
                {
                    StopMonitoring();
                }
                
                Logger.Info($"AutoShowManager enabled: {_isEnabled}");
            }
        }
    }

    #endregion

    #region Constructor

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        Logger.Info("AutoShowManager initialized");
    }

    #endregion

    #region Monitoring Control

    /// <summary>
    /// Start monitoring for text field focus events
    /// </summary>
    private void StartMonitoring()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Logger.Warning("AutoShow monitoring already started");
            return;
        }

        try
        {
            // Keep delegate reference to prevent GC
            _hookDelegate = new WinEventDelegate(WinEventCallback);
            
            // Set hook for focus events
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_FOCUS,
                EVENT_OBJECT_FOCUS,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (_hookHandle == IntPtr.Zero)
            {
                Logger.Error("Failed to set WinEvent hook for auto-show");
            }
            else
            {
                Logger.Info("AutoShow monitoring started successfully");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception starting AutoShow monitoring", ex);
        }
    }

    /// <summary>
    /// Stop monitoring for text field focus events
    /// </summary>
    private void StopMonitoring()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            try
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
                _lastFocusedWindow = IntPtr.Zero;
                Logger.Info("AutoShow monitoring stopped");
            }
            catch (Exception ex)
            {
                Logger.Error("Exception stopping AutoShow monitoring", ex);
            }
        }
    }

    #endregion

    #region Event Callback

    /// <summary>
    /// Callback for Windows focus events
    /// </summary>
    private void WinEventCallback(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        try
        {
            // Ignore if disabled or invalid window
            if (!_isEnabled || hwnd == IntPtr.Zero)
                return;

            // Skip if this is the keyboard window itself
            if (hwnd == _keyboardWindowHandle)
            {
                Logger.Debug("Focus on keyboard window - ignoring");
                return;
            }

            // Skip if same window as last time (avoid duplicates)
            if (hwnd == _lastFocusedWindow)
                return;

            // Check if the focused window is a text input control
            if (IsTextInputControl(hwnd))
            {
                _lastFocusedWindow = hwnd;
                
                string className = GetWindowClassName(hwnd);
                Logger.Info($"Text input focused: {className} (0x{hwnd:X})");
                
                // Trigger keyboard show event
                ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception in WinEvent callback", ex);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Check if the given window is a text input control
    /// </summary>
    private bool IsTextInputControl(IntPtr hwnd)
    {
        try
        {
            string className = GetWindowClassName(hwnd);
            
            if (string.IsNullOrEmpty(className))
                return false;

            // Check against known text input class names
            foreach (string textClass in TextInputClasses)
            {
                if (className.IndexOf(textClass, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Logger.Debug($"Detected text input: {className}");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Exception checking if control is text input", ex);
            return false;
        }
    }

    /// <summary>
    /// Get the class name of a window
    /// </summary>
    private string GetWindowClassName(IntPtr hwnd)
    {
        try
        {
            StringBuilder className = new StringBuilder(256);
            int length = GetClassName(hwnd, className, className.Capacity);
            
            if (length > 0)
            {
                return className.ToString();
            }
            
            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Error("Exception getting window class name", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Get focused control in the current thread
    /// </summary>
    private IntPtr GetFocusedControl(IntPtr foregroundWindow)
    {
        try
        {
            // Get foreground window thread
            uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
            uint currentThreadId = GetWindowThreadProcessId(_keyboardWindowHandle, out _);

            // Attach to foreground thread to get its focus
            if (foregroundThreadId != currentThreadId)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, true);
                IntPtr focusedControl = GetFocus();
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
                
                return focusedControl;
            }
            
            return GetFocus();
        }
        catch (Exception ex)
        {
            Logger.Error("Exception getting focused control", ex);
            return IntPtr.Zero;
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Managed cleanup
                StopMonitoring();
            }

            // Unmanaged cleanup
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _disposed = true;
            Logger.Info("AutoShowManager disposed");
        }
    }

    ~AutoShowManager()
    {
        Dispose(false);
    }

    #endregion
}