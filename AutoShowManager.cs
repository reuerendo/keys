using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard display using Windows Event Hooks
/// Monitors focus changes system-wide and shows keyboard for text input controls
/// </summary>
public class AutoShowManager : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private bool _disposed;
    
    private IntPtr _hookHandle;
    private WinEventDelegate _hookDelegate;
    private GCHandle _delegateHandle;
    
    // Delay to prevent immediate re-showing
    private DateTime _lastHideTime = DateTime.MinValue;
    private const int HIDE_COOLDOWN_MS = 500;

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
        Logger.Info("AutoShowManager created (WinEventHook-based)");
    }

    /// <summary>
    /// Start Windows Event Hook monitoring
    /// </summary>
    private void StartMonitoring()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Logger.Warning("Event hook already active");
            return;
        }

        try
        {
            // Create delegate and pin it to prevent GC collection
            _hookDelegate = new WinEventDelegate(WinEventProc);
            _delegateHandle = GCHandle.Alloc(_hookDelegate);

            // Set up hook for focus events
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_FOCUS,              // eventMin
                EVENT_OBJECT_FOCUS,              // eventMax
                IntPtr.Zero,                     // hmodWinEventProc
                _hookDelegate,                   // pfnWinEventProc
                0,                               // idProcess (0 = all processes)
                0,                               // idThread (0 = all threads)
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (_hookHandle == IntPtr.Zero)
            {
                Logger.Error("Failed to set Windows event hook");
                if (_delegateHandle.IsAllocated)
                    _delegateHandle.Free();
                return;
            }

            Logger.Info($"Windows event hook registered successfully (Handle=0x{_hookHandle:X})");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start event hook monitoring", ex);
        }
    }

    /// <summary>
    /// Stop Windows Event Hook monitoring
    /// </summary>
    private void StopMonitoring()
    {
        try
        {
            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWinEvent(_hookHandle);
                _hookHandle = IntPtr.Zero;
                Logger.Info("Windows event hook unregistered");
            }

            if (_delegateHandle.IsAllocated)
            {
                _delegateHandle.Free();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to stop event hook monitoring", ex);
        }
    }

    /// <summary>
    /// Callback for Windows events
    /// </summary>
    private void WinEventProc(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!_isEnabled || _disposed)
            return;

        try
        {
            // Check cooldown period
            if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
            {
                return;
            }

            // Ignore our own window
            if (hwnd == _keyboardWindowHandle)
            {
                return;
            }

            // Only process window objects
            if (idObject != OBJID_WINDOW && idObject != OBJID_CLIENT)
            {
                return;
            }

            // Check if focused element is a text input
            if (IsTextInputElement(hwnd))
            {
                Logger.Info($"Text input focused (hwnd=0x{hwnd:X}) - showing keyboard");
                ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in WinEventProc: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if element is a text input control
    /// </summary>
    private bool IsTextInputElement(IntPtr hwnd)
    {
        try
        {
            // Get automation element from window handle
            AutomationElement element = AutomationElement.FromHandle(hwnd);
            if (element == null)
                return false;

            // Get focused element
            AutomationElement focusedElement = AutomationElement.FocusedElement;
            if (focusedElement == null)
                return false;

            // Check control type
            var controlType = focusedElement.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
            
            if (controlType == ControlType.Edit || 
                controlType == ControlType.Document ||
                controlType == ControlType.Text)
            {
                Logger.Debug($"Text control detected: {controlType.ProgrammaticName}");
                return true;
            }

            // Check if control is editable (has ValuePattern)
            if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
            {
                var pattern = valuePattern as ValuePattern;
                if (pattern != null && !pattern.Current.IsReadOnly)
                {
                    Logger.Debug("Editable control with ValuePattern detected");
                    return true;
                }
            }

            // Check if control supports text input (has TextPattern)
            if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern))
            {
                Logger.Debug("Control with TextPattern detected");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error checking text input: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Notify manager that keyboard was hidden
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

    #region P/Invoke and Constants

    // Delegate for event hook callback
    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

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

    // Event constants
    private const uint EVENT_OBJECT_FOCUS = 0x8005;

    // Object identifiers
    private const int OBJID_WINDOW = 0x00000000;
    private const int OBJID_CLIENT = unchecked((int)0xFFFFFFFC);

    // Flags
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    #endregion
}