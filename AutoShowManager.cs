using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility based on text input focus
/// Uses polling approach to detect edit control focus
/// </summary>
public class AutoShowManager : IDisposable
{
    // Win32 Constants
    private const int GWL_STYLE = -16;
    private const int ES_READONLY = 0x0800;
    
    // Edit control class names
    private static readonly string[] EditControlClasses = 
    {
        "Edit",
        "RichEdit",
        "RichEdit20A",
        "RichEdit20W",
        "RichEdit50W",
        "RICHEDIT60W",
        "TextBox",
        "ConsoleWindowClass"
    };

    // P/Invoke
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

	[DllImport("kernel32.dll")]
	private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private const uint EM_GETSEL = 0x00B0;

    private IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private DispatcherQueueTimer _pollingTimer;
    private IntPtr _lastFocusedWindow;
    private bool _wasKeyboardShown;

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
                {
                    StartPolling();
                }
                else
                {
                    StopPolling();
                }
                
                Logger.Info($"AutoShow {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        
        Logger.Info("AutoShowManager initialized (polling mode)");
    }

    private void StartPolling()
    {
        if (_pollingTimer != null)
        {
            Logger.Warning("Polling timer already running");
            return;
        }

        try
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            if (dispatcherQueue == null)
            {
                Logger.Error("Cannot get DispatcherQueue for current thread");
                return;
            }

            _pollingTimer = dispatcherQueue.CreateTimer();
            _pollingTimer.Interval = TimeSpan.FromMilliseconds(250); // Check every 250ms
            _pollingTimer.Tick += PollingTimer_Tick;
            _pollingTimer.Start();
            
            Logger.Info("Polling timer started (250ms interval)");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start polling timer", ex);
        }
    }

    private void StopPolling()
    {
        if (_pollingTimer != null)
        {
            try
            {
                _pollingTimer.Stop();
                _pollingTimer = null;
                _lastFocusedWindow = IntPtr.Zero;
                _wasKeyboardShown = false;
                
                Logger.Info("Polling timer stopped");
            }
            catch (Exception ex)
            {
                Logger.Error("Error stopping polling timer", ex);
            }
        }
    }

    private void PollingTimer_Tick(object sender, object e)
    {
        if (!_isEnabled)
            return;

        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            
            // Skip if keyboard window is foreground
            if (foregroundWindow == _keyboardWindowHandle)
                return;

            // Skip if no foreground window
            if (foregroundWindow == IntPtr.Zero)
                return;

            // Get the focused control within the foreground window
            IntPtr focusedControl = GetFocusedControl(foregroundWindow);
            
            if (focusedControl != IntPtr.Zero && focusedControl != _lastFocusedWindow)
            {
                _lastFocusedWindow = focusedControl;
                
                if (IsEditControl(focusedControl))
                {
                    Logger.Info($"Edit control focused: 0x{focusedControl:X}");
                    
                    // Only show keyboard once per focus
                    if (!_wasKeyboardShown)
                    {
                        _wasKeyboardShown = true;
                        OnShowKeyboardRequested();
                    }
                }
                else
                {
                    _wasKeyboardShown = false;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error in polling timer tick", ex);
        }
    }

    private IntPtr GetFocusedControl(IntPtr foregroundWindow)
    {
        try
        {
            // Get thread IDs
            uint currentThreadId = GetCurrentThreadId();
            uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
            
            if (foregroundThreadId == 0)
                return IntPtr.Zero;

            // Attach to the foreground thread's input
            bool attached = false;
            if (currentThreadId != foregroundThreadId)
            {
                attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            try
            {
                // Get the focused window
                IntPtr focusedWindow = GetFocus();
                
                // If no focused window in foreground thread, use foreground window itself
                if (focusedWindow == IntPtr.Zero)
                {
                    focusedWindow = foregroundWindow;
                }
                
                return focusedWindow;
            }
            finally
            {
                // Detach from the thread
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error getting focused control: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private bool IsEditControl(IntPtr hwnd)
    {
        try
        {
            // Check class name
            var className = new System.Text.StringBuilder(256);
            int result = GetClassName(hwnd, className, className.Capacity);
            
            if (result == 0)
                return false;

            string controlClass = className.ToString();
            
            // Check if it's a known edit control class
            foreach (string editClass in EditControlClasses)
            {
                if (controlClass.Equals(editClass, StringComparison.OrdinalIgnoreCase))
                {
                    // Additional check: make sure it's not read-only
                    int style = GetWindowLong(hwnd, GWL_STYLE);
                    bool isReadOnly = (style & ES_READONLY) != 0;
                    
                    if (!isReadOnly)
                    {
                        Logger.Debug($"Editable control detected: {controlClass}");
                        return true;
                    }
                    else
                    {
                        Logger.Debug($"Read-only control skipped: {controlClass}");
                        return false;
                    }
                }
            }
            
            // Additional heuristic: check if control responds to EM_GETSEL
            // This helps detect custom edit controls
            if (controlClass.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                controlClass.Contains("Text", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    IntPtr result2 = SendMessage(hwnd, EM_GETSEL, IntPtr.Zero, IntPtr.Zero);
                    if (result2 != IntPtr.Zero)
                    {
                        Logger.Debug($"Custom edit control detected: {controlClass}");
                        return true;
                    }
                }
                catch
                {
                    // Ignore errors from SendMessage
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error checking control class: {ex.Message}");
        }

        return false;
    }

    private void OnShowKeyboardRequested()
    {
        try
        {
            ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Logger.Error("Error invoking ShowKeyboardRequested", ex);
        }
    }

    public void Dispose()
    {
        StopPolling();
        Logger.Info("AutoShowManager disposed");
    }
}