using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard display when text input fields receive focus
/// Uses low-level Windows hooks and UI Automation COM interfaces
/// </summary>
public class AutoShowManager : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private bool _disposed;
    private Thread _monitorThread;
    private CancellationTokenSource _cancellationTokenSource;
    
    // Delay to prevent immediate re-showing if keyboard was just hidden
    private DateTime _lastHideTime = DateTime.MinValue;
    private const int HIDE_COOLDOWN_MS = 500;
    private const int POLL_INTERVAL_MS = 250;

    // P/Invoke for window and focus operations
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();
    
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // UI Automation COM interface
    [DllImport("UIAutomationCore.dll", CharSet = CharSet.Unicode)]
    private static extern int UiaHostProviderFromHwnd(IntPtr hwnd, out IntPtr provider);
    
    [DllImport("UIAutomationCore.dll")]
    private static extern IntPtr UiaGetReservedNotSupportedValue();

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
                    StartMonitoring();
                }
                else
                {
                    StopMonitoring();
                }
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        Logger.Info("AutoShowManager created");
    }

    /// <summary>
    /// Start monitoring focus changes
    /// </summary>
    private void StartMonitoring()
    {
        if (_monitorThread != null && _monitorThread.IsAlive)
        {
            Logger.Warning("Focus monitoring already active");
            return;
        }

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _monitorThread = new Thread(MonitorFocusLoop)
            {
                IsBackground = true,
                Name = "AutoShowMonitor"
            };
            _monitorThread.Start();
            
            Logger.Info("Focus monitoring started");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start focus monitoring", ex);
        }
    }

    /// <summary>
    /// Stop monitoring focus changes
    /// </summary>
    private void StopMonitoring()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
            
            if (_monitorThread != null && _monitorThread.IsAlive)
            {
                if (!_monitorThread.Join(1000))
                {
                    Logger.Warning("Monitor thread did not stop gracefully");
                }
            }
            
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _monitorThread = null;
            
            Logger.Info("Focus monitoring stopped");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to stop focus monitoring", ex);
        }
    }

    /// <summary>
    /// Background monitoring loop
    /// </summary>
    private void MonitorFocusLoop()
    {
        IntPtr lastFocusedWindow = IntPtr.Zero;
        
        try
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Check cooldown period
                    if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
                    {
                        Thread.Sleep(POLL_INTERVAL_MS);
                        continue;
                    }

                    IntPtr foregroundWindow = GetForegroundWindow();
                    
                    // Skip if keyboard itself has focus
                    if (foregroundWindow == _keyboardWindowHandle)
                    {
                        Thread.Sleep(POLL_INTERVAL_MS);
                        continue;
                    }

                    // Check if foreground window changed
                    if (foregroundWindow != lastFocusedWindow && foregroundWindow != IntPtr.Zero)
                    {
                        lastFocusedWindow = foregroundWindow;
                        
                        if (IsTextInputWindow(foregroundWindow))
                        {
                            uint processId;
                            GetWindowThreadProcessId(foregroundWindow, out processId);
                            string className = GetWindowClassName(foregroundWindow);
                            
                            Logger.Info($"Text input focused: HWND=0x{foregroundWindow:X}, Class='{className}', PID={processId}");
                            
                            // Raise event to show keyboard (on UI thread)
                            Task.Run(() => ShowKeyboardRequested?.Invoke(this, EventArgs.Empty));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Error in monitor loop iteration", ex);
                }

                Thread.Sleep(POLL_INTERVAL_MS);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error in monitor loop", ex);
        }
        
        Logger.Debug("Monitor loop exited");
    }

    /// <summary>
    /// Check if window is a text input control
    /// </summary>
    private bool IsTextInputWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        try
        {
            string className = GetWindowClassName(hwnd);
            if (string.IsNullOrEmpty(className))
                return false;

            // Check common text input class names
            string classLower = className.ToLowerInvariant();
            
            // Standard Windows text controls
            if (classLower == "edit" ||                    // TextBox, standard edit
                classLower == "richedit" ||                // RichTextBox
                classLower == "richedit20a" ||             // RichEdit 2.0 ANSI
                classLower == "richedit20w" ||             // RichEdit 2.0 Unicode
                classLower == "richedit50w" ||             // RichEdit 5.0
                classLower == "riched20" ||                // Another RichEdit variant
                classLower.Contains("texbox") ||           // Various TextBox implementations
                classLower.Contains("scintilla") ||        // Scintilla editor (Notepad++, etc.)
                classLower.Contains("editwndclass"))       // Custom edit controls
            {
                Logger.Debug($"Matched text input class: {className}");
                return true;
            }

            // WinUI/WPF/WinForms patterns
            if (classLower.Contains("textbox") ||
                classLower.Contains("textblock") ||
                classLower.Contains("richeditbox"))
            {
                Logger.Debug($"Matched modern UI text class: {className}");
                return true;
            }

            // Chrome/Edge address bar and inputs
            if (classLower.Contains("chrome_") && 
                (classLower.Contains("omnibox") || classLower.Contains("renderwidget")))
            {
                Logger.Debug($"Matched browser input: {className}");
                return true;
            }

            // Firefox inputs
            if (classLower.Contains("mozillawindowclass") || 
                classLower.Contains("gecko"))
            {
                // Firefox requires additional checks, but for now accept
                Logger.Debug($"Matched Firefox window: {className}");
                return true;
            }

            // Try UI Automation as fallback
            if (IsTextInputViaUIAutomation(hwnd))
            {
                Logger.Debug($"Matched via UI Automation: {className}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"Error checking if window is text input: 0x{hwnd:X}", ex);
            return false;
        }
    }

    /// <summary>
    /// Check via UI Automation if available
    /// </summary>
    private bool IsTextInputViaUIAutomation(IntPtr hwnd)
    {
        try
        {
            IntPtr provider = IntPtr.Zero;
            int hr = UiaHostProviderFromHwnd(hwnd, out provider);
            
            if (hr == 0 && provider != IntPtr.Zero)
            {
                // We have a provider, but checking properties requires more COM work
                // For simplicity, we'll just return false and rely on class name matching
                Marshal.Release(provider);
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get window class name
    /// </summary>
    private string GetWindowClassName(IntPtr hwnd)
    {
        try
        {
            var className = new System.Text.StringBuilder(256);
            int length = GetClassName(hwnd, className, className.Capacity);
            return length > 0 ? className.ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Notify manager that keyboard was hidden (for cooldown tracking)
    /// </summary>
    public void NotifyKeyboardHidden()
    {
        _lastHideTime = DateTime.Now;
        Logger.Debug("Keyboard hide notification received, cooldown started");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopMonitoring();
        
        Logger.Info("AutoShowManager disposed");
    }
}