using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Accessibility;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA).
/// Replaces the heavy UI Automation approach while maintaining strict click-to-show logic.
/// </summary>
public class WinEventFocusTracker : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private readonly MouseClickDetector _clickDetector;
    private NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _requireClickForAutoShow;
    private bool _isDisposed = false;
    
    // Checks if the keyboard itself is visible
    private Func<bool> _isKeyboardVisible;

    public event EventHandler<TextInputFocusEventArgs> TextInputFocused;
    public event EventHandler<FocusEventArgs> NonTextInputFocused;

    public WinEventFocusTracker(IntPtr keyboardWindowHandle, MouseClickDetector clickDetector, bool requireClickForAutoShow = true)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _clickDetector = clickDetector;
        _requireClickForAutoShow = requireClickForAutoShow;

        Initialize();
    }

    private void Initialize()
    {
        // 1. Subscribe to global focus events (Lightweight WinEvent)
        _winEventProc = new NativeMethods.WinEventDelegate(WinEventProc);
        _hookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_FOCUS, 
            NativeMethods.EVENT_OBJECT_FOCUS, 
            IntPtr.Zero, 
            _winEventProc, 
            0, 
            0, 
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_hookHandle == IntPtr.Zero)
        {
            Logger.Error("Failed to install WinEvent hook");
        }
        else
        {
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS)");
        }

        // 2. Subscribe to click detector specifically for "Already Focused" scenarios
        if (_clickDetector != null)
        {
            _clickDetector.ClickDetected += OnClickDetected;
        }
    }

    public void SetKeyboardVisibilityChecker(Func<bool> isVisible)
    {
        _isKeyboardVisible = isVisible;
    }

    /// <summary>
    /// Callback for SetWinEventHook
    /// </summary>
    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed) return;
        
        // Ignore events from our own keyboard window
        if (hwnd == _keyboardWindowHandle) return;

        try
        {
            // If we require a click, check if a click happened recently
            // This filters out programmatic focus changes (Alt+Tab, app launch, code triggers)
            if (_requireClickForAutoShow && _clickDetector != null)
            {
                if (!_clickDetector.WasRecentClick())
                {
                    Logger.Debug("Focus changed, but no recent click detected. Ignoring.");
                    return;
                }
            }

            // Get the IAccessible object from the event
            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out IAccessible acc, out object childId);
            
            if (hr >= 0 && acc != null)
            {
                ProcessAccessibleObject(acc, childId, hwnd, isDirectClick: false);
                Marshal.ReleaseComObject(acc);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error in WinEventProc", ex);
        }
    }

    /// <summary>
    /// Handles direct clicks to detect text fields that might ALREADY have focus
    /// </summary>
    private void OnClickDetected(object sender, Point clickPoint)
    {
        if (_isDisposed || !_requireClickForAutoShow) return;

        // Skip if keyboard is visible
        if (_isKeyboardVisible != null && _isKeyboardVisible()) return;

        Task.Run(() =>
        {
            try
            {
                NativeMethods.POINT pt = new NativeMethods.POINT { X = clickPoint.X, Y = clickPoint.Y };
                
                // Get object directly under mouse
                int hr = NativeMethods.AccessibleObjectFromPoint(pt, out IAccessible acc, out object childId);

                if (hr >= 0 && acc != null)
                {
                    // For OnClick, we need to find the HWND because AccessibleObjectFromPoint doesn't give it directly
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        // Try to get HWND from the accessible object
                        hwnd = NativeMethods.WindowFromAccessibleObject(acc);
                    }
                    catch { /* Best effort */ }

                    // Ignore our own window
                    if (hwnd != _keyboardWindowHandle)
                    {
                         ProcessAccessibleObject(acc, childId, hwnd, isDirectClick: true);
                    }

                    Marshal.ReleaseComObject(acc);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error checking object at click point", ex);
            }
        });
    }

    /// <summary>
    /// Core logic to determine if the object is a text input
    /// </summary>
    private void ProcessAccessibleObject(IAccessible acc, object childId, IntPtr hwnd, bool isDirectClick)
    {
        try
        {
            // 1. Check Role
            int role = (int)acc.accRole[childId];
            
            // 2. Get State
            int state = (int)acc.accState[childId];
            bool isProtected = (state & NativeMethods.STATE_SYSTEM_PROTECTED) != 0; // Password

            // 3. Get ClassName (Win32 API) - useful for specific exclusions/inclusions
            string className = "";
            if (hwnd != IntPtr.Zero)
            {
                StringBuilder sb = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                className = sb.ToString();
            }

            bool isText = IsTextInputRole(role, className);

            if (isText)
            {
                // Double check bounds if it was a direct click (ensure we actually clicked INSIDE)
                // AccessibleObjectFromPoint usually handles this, but for safety:
                if (isDirectClick && _clickDetector != null)
                {
                    acc.accLocation(out int l, out int t, out int w, out int h, childId);
                    Rectangle bounds = new Rectangle(l, t, w, h);
                    if (!_clickDetector.WasRecentClickInBounds(bounds))
                    {
                        Logger.Debug($"Click detected, but outside element bounds logic. Role: {role}");
                        return;
                    }
                }

                // Get Process ID
                uint pid = 0;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                }
                
                string name = "";
                try { name = acc.accName[childId]; } catch { }

                Logger.Info($"{(isDirectClick ? "🖱️ Click" : "⚡ Focus")} on Text Input - Role: {role}, Class: {className}, Name: {name}");

                TextInputFocused?.Invoke(this, new TextInputFocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = role, // Sending Role as ControlType
                    ClassName = className,
                    Name = name,
                    IsPassword = isProtected,
                    ProcessId = pid
                });
            }
            else
            {
                 NonTextInputFocused?.Invoke(this, new FocusEventArgs
                 {
                     WindowHandle = hwnd,
                     ControlType = role,
                     ClassName = className
                 });
            }
        }
        catch (Exception ex)
        {
            // COM exceptions can happen if object dies quickly
            Logger.Debug($"Error processing accessible object: {ex.Message}");
        }
    }

    private bool IsTextInputRole(int role, string className)
    {
        // 1. Standard Text Roles
        if (role == NativeMethods.ROLE_SYSTEM_TEXT) return true;

        // 2. Document Role (Word, Browsers often use this for content area)
        if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT) return true;

        // 3. Check class names for legacy apps or specific controls that don't report role correctly
        string classLower = className.ToLowerInvariant();
        
        if (classLower.Contains("edit") || 
            classLower.Contains("richedit") || 
            classLower.Contains("cmd") || // Command Prompt
            classLower.Contains("console")) 
        {
            return true;
        }

        // Some modern UI frameworks use CLIENT role for custom text boxes, 
        // but we should be careful not to enable it for everything.
        // Usually, checking if it is "Focusable" happens outside, but here we assume focus event.
        
        return false;
    }

    public void UpdateAutoShowSetting(bool enabled)
    {
        _requireClickForAutoShow = enabled;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        if (_clickDetector != null)
        {
            _clickDetector.ClickDetected -= OnClickDetected;
        }
    }
}