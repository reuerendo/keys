using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA).
/// Uses deterministic event sequence analysis instead of time-based heuristics.
/// </summary>
public class WinEventFocusTracker : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private readonly MouseClickDetector _clickDetector;
    private NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _requireClickForAutoShow;
    private bool _isDisposed = false;
    
    private Func<bool> _isKeyboardVisible;

    // Track focus state sequence (deterministic, no time constants!)
    private IntPtr _previousFocusedWindow = IntPtr.Zero;
    private IntPtr _currentFocusedWindow = IntPtr.Zero;
    private string _previousWindowClass = "";
    private string _currentWindowClass = "";
    private bool _wasPreviouslyTextInput = false;

    // Blacklist of processes that should never trigger auto-show
    private static readonly string[] ProcessBlacklist = new[]
    {
        "explorer.exe",
        "searchhost.exe",
        "startmenuexperiencehost.exe"
    };

    // Blacklist of window classes that should be excluded
    private static readonly string[] ClassBlacklist = new[]
    {
        "syslistview32",
        "directuihwnd",
        "cabinetwclass",
        "workerw",
        "progman"
    };

    // Known text editor classes that should always trigger auto-show
    private static readonly string[] EditorClassWhitelist = new[]
    {
        "scintilla",
        "richedit",
        "edit",
        "akeleditwclass",
        "atlaxwin",
        "vscodecontentcontrol"
    };

    // Known Chrome/Electron render widget classes
    private static readonly string[] ChromeRenderClasses = new[]
    {
        "chrome_renderwidgethosthwnd",
        "chrome_widgetwin_",
        "intermediate d3d window"
    };

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
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS with Surface touch/pen support)");
        }

        if (_clickDetector != null)
        {
            _clickDetector.HardwareClickDetected += OnHardwareClickDetected;
        }
    }

    public void SetKeyboardVisibilityChecker(Func<bool> isVisible)
    {
        _isKeyboardVisible = isVisible;
    }

    /// <summary>
    /// Callback for SetWinEventHook - deterministic event sequence analysis
    /// </summary>
    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed) return;
        if (hwnd == _keyboardWindowHandle) return;

        try
        {
            // Update focus tracking state
            _previousFocusedWindow = _currentFocusedWindow;
            _previousWindowClass = _currentWindowClass;
            _currentFocusedWindow = hwnd;

            StringBuilder className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            _currentWindowClass = className.ToString();

            // CRITICAL: If we require a click, do STRICT validation
            if (_requireClickForAutoShow)
            {
                if (_clickDetector == null || !_clickDetector.WasRecentHardwareClick())
                {
                    Logger.Debug("❌ Focus event: No recent click - ignoring");
                    return;
                }
                
                if (!IsHardwareInputCausedFocus())
                {
                    Logger.Debug("❌ Focus event: Not caused by hardware input - ignoring");
                    return;
                }
                
                Logger.Debug("✅ Focus event: Both recent click AND hardware input source confirmed");
            }

            // Get the IAccessible object from the event
            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out NativeMethods.IAccessible acc, out object childId);
            
            if (hr >= 0 && acc != null)
            {
                if (_requireClickForAutoShow && _clickDetector != null)
                {
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        
                        if (!_clickDetector.WasRecentHardwareClickInBounds(bounds))
                        {
                            Logger.Debug($"❌ Click was OUTSIDE element bounds - ignoring. Bounds: ({l}, {t}, {w}x{h})");
                            Marshal.ReleaseComObject(acc);
                            return;
                        }
                        
                        Logger.Debug($"✅ Click was INSIDE element bounds ({l}, {t}, {w}x{h})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"⚠️ Could not verify bounds: {ex.Message} - continuing anyway");
                    }
                }
                
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
    /// Deterministic check: is this a programmatic focus return after closing a dialog?
    /// No time constants - pure event sequence analysis!
    /// </summary>
    private bool IsDialogReturnFocus()
    {
        // Scenario: Text field in focus → Dialog opens → Dialog closes → Focus returns to text field
        // Previous was dialog, current is same window/process as before dialog
        
        bool previousWasDialog = _previousWindowClass == "#32770";
        bool returningToSameWindow = _currentFocusedWindow == _previousFocusedWindow;
        
        // Check if we're returning to the same parent process after closing dialog
        if (previousWasDialog && _wasPreviouslyTextInput)
        {
            // Previous text input lost focus to dialog, now getting focus back
            // This is a RETURN, not a new text input session
            uint prevPid = 0, currPid = 0;
            
            if (_previousFocusedWindow != IntPtr.Zero)
                NativeMethods.GetWindowThreadProcessId(_previousFocusedWindow, out prevPid);
            
            NativeMethods.GetWindowThreadProcessId(_currentFocusedWindow, out currPid);
            
            if (prevPid == currPid)
            {
                Logger.Info($"🔄 Dialog focus return detected: previous='{_previousWindowClass}' (dialog), returning to same process");
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Check if the current focus change was caused by hardware input (no time constants!)
    /// </summary>
    private bool IsHardwareInputCausedFocus()
    {
        try
        {
            bool success = NativeMethods.GetCurrentInputMessageSource(out NativeMethods.INPUT_MESSAGE_SOURCE source);
            
            if (!success)
            {
                Logger.Debug("⚠️ GetCurrentInputMessageSource API failed - rejecting for safety");
                return false;
            }

            Logger.Debug($"🔍 Input source: DeviceType={source.deviceType}, OriginID={source.originId}");

            var originId = source.originId;
            
            // ACCEPT: Hardware input from real devices
            if (originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_HARDWARE)
            {
                var deviceType = source.deviceType;
                
                if (deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_MOUSE ||
                    deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCH ||
                    deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD ||
                    deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_PEN)
                {
                    Logger.Debug($"✅ Hardware input from {deviceType}");
                    return true;
                }
                
                Logger.Debug($"⚠️ Hardware origin but unsupported device: {deviceType}");
                return false;
            }
            
            // REJECT: Explicitly programmatic input
            if (originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_INJECTED)
            {
                Logger.Debug("🚫 INJECTED input (SendInput API) - rejecting");
                return false;
            }
            
            if (originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_SYSTEM)
            {
                Logger.Debug("🚫 SYSTEM-generated input - rejecting");
                return false;
            }
            
            // UNAVAILABLE: Check click detector for bounds verification
            // If click is INSIDE bounds, accept (direct click)
            // If click is OUTSIDE bounds, reject (programmatic after dialog close)
            if (originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE)
            {
                // Bounds check already done in WinEventProc - if we got here, click was inside bounds
                Logger.Debug($"✅ Source UNAVAILABLE but click verified by bounds check - accepting");
                return true;
            }

            Logger.Debug($"🚫 Unknown origin ID {originId} - rejecting");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Error in IsHardwareInputCausedFocus", ex);
            return false;
        }
    }

    /// <summary>
    /// Handles direct clicks to detect text fields that might ALREADY have focus
    /// </summary>
    private void OnHardwareClickDetected(object sender, Point clickPoint)
    {
        if (_isDisposed || !_requireClickForAutoShow) return;
        if (_isKeyboardVisible != null && _isKeyboardVisible()) return;

        Task.Run(() =>
        {
            try
            {
                NativeMethods.POINT pt = new NativeMethods.POINT { X = clickPoint.X, Y = clickPoint.Y };
                
                int hr = NativeMethods.AccessibleObjectFromPoint(pt, out NativeMethods.IAccessible acc, out object childId);

                if (hr >= 0 && acc != null)
                {
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        hwnd = NativeMethods.WindowFromAccessibleObject(acc);
                    }
                    catch { }

                    if (hwnd != _keyboardWindowHandle)
                    {
                         ProcessAccessibleObject(acc, childId, hwnd, isDirectClick: true);
                    }

                    Marshal.ReleaseComObject(acc);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error checking object at hardware click point", ex);
            }
        });
    }

    /// <summary>
    /// Core logic to determine if the object is a text input (deterministic, no time!)
    /// </summary>
    private void ProcessAccessibleObject(NativeMethods.IAccessible acc, object childId, IntPtr hwnd, bool isDirectClick)
    {
        try
        {
            uint pid = 0;
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                
                if (IsBlacklistedProcess(pid))
                {
                    Logger.Debug($"🚫 Ignoring focus in blacklisted process (PID: {pid})");
                    return;
                }
            }

            string className = "";
            if (hwnd != IntPtr.Zero)
            {
                StringBuilder sb = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                className = sb.ToString();
                
                if (IsBlacklistedClassName(className))
                {
                    Logger.Debug($"🚫 Ignoring focus in blacklisted window class: {className}");
                    return;
                }
            }

            object roleObj = acc.get_accRole(childId);
            int role = (roleObj is int r) ? r : 0;
            
            object stateObj = acc.get_accState(childId);
            int state = (stateObj is int s) ? s : 0;
            
            bool isProtected = (state & NativeMethods.STATE_SYSTEM_PROTECTED) != 0;
            bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
            bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
            bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

            bool isText = IsEditableTextInput(role, className, state, acc, childId);

            if (isText)
            {
                // DETERMINISTIC CHECK: Is this a focus return after dialog close?
                if (IsDialogReturnFocus())
                {
                    Logger.Info($"🚫 Ignoring auto-show - focus returned from dialog to already-focused text field");
                    _wasPreviouslyTextInput = true; // Still in text input context
                    return;
                }
                
                if (isDirectClick && _clickDetector != null)
                {
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        if (!_clickDetector.WasRecentHardwareClickInBounds(bounds))
                        {
                            Logger.Debug($"Click detected, but outside element bounds. Role: {role}");
                            return;
                        }
                    }
                    catch
                    {
                        Logger.Debug("Bounds check failed, but element is text input - continuing");
                    }
                }
                
                string name = "";
                try { name = acc.get_accName(childId); } catch { }

                if (name != null && name.Length > 100)
                {
                    name = name.Substring(0, 100) + "...";
                }

                Logger.Info($"{(isDirectClick ? "🖱️ Click" : "⚡ Focus")} on EDITABLE Text Input - Role: {role}, Class: {className}, Name: {name}");

                // Update state: we're now in a text input
                _wasPreviouslyTextInput = true;

                TextInputFocused?.Invoke(this, new TextInputFocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = role,
                    ClassName = className,
                    Name = name,
                    IsPassword = isProtected,
                    ProcessId = pid
                });
            }
            else
            {
                Logger.Debug($"Not a text input - Role: {role}, Class: {className}, Readonly={isReadonly}");
                
                // Not a text input - update state
                _wasPreviouslyTextInput = false;
                
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
            Logger.Debug($"Error processing accessible object: {ex.Message}");
        }
    }

    private bool IsEditableTextInput(int role, string className, int state, NativeMethods.IAccessible acc, object childId)
    {
        bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
        bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
        bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

        if (isUnavailable) return false;

        string classLower = className?.ToLowerInvariant() ?? "";

        const int ROLE_SYSTEM_CARET = 0x7;
        if (role == ROLE_SYSTEM_CARET)
        {
            Logger.Debug($"✅ CARET (insertion point) detected - user clicked in text field");
            return true;
        }

        if (IsEditorClass(classLower))
        {
            if (isReadonly)
            {
                Logger.Debug($"Editor class '{className}' but readonly - rejecting");
                return false;
            }
            
            Logger.Debug($"✅ Recognized editor class: {className}");
            return true;
        }

        if (IsChromeRenderClass(classLower))
        {
            if (isFocusable)
            {
                try
                {
                    string value = acc.get_accValue(childId);
                    Logger.Debug($"✅ Chrome render widget has value interface - accepting");
                    return true;
                }
                catch { }

                const int ROLE_SYSTEM_PANE = 0x10;
                if (role == ROLE_SYSTEM_PANE || role == NativeMethods.ROLE_SYSTEM_CLIENT)
                {
                    Logger.Debug($"✅ Chrome render widget with focusable pane/client - accepting");
                    return true;
                }
            }
            
            return false;
        }

        if (!isFocusable)
        {
            Logger.Debug($"Element is not focusable (Role: {role})");
            return false;
        }

        if (classLower.Contains("edit") && !isReadonly)
        {
            try
            {
                string value = acc.get_accValue(childId);
                return true;
            }
            catch { }
        }

        if (classLower.Contains("console") || classLower.Contains("cmd") || classLower.Contains("terminal"))
        {
            return true;
        }

        if (role == NativeMethods.ROLE_SYSTEM_TEXT)
        {
            if (isReadonly)
            {
                Logger.Debug($"ROLE_SYSTEM_TEXT but readonly - rejecting");
                return false;
            }

            try
            {
                string value = acc.get_accValue(childId);
                Logger.Debug($"ROLE_SYSTEM_TEXT with value interface - accepting");
                return true;
            }
            catch
            {
                Logger.Debug($"ROLE_SYSTEM_TEXT without value interface - rejecting");
                return false;
            }
        }

        if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT && !isReadonly)
        {
            return true;
        }

        if (role == NativeMethods.ROLE_SYSTEM_CLIENT)
        {
            if (isReadonly) return false;

            try
            {
                string name = acc.get_accName(childId);
                if (!string.IsNullOrEmpty(name) && name.Length > 20)
                {
                    Logger.Debug($"✅ CLIENT role with text content (length: {name.Length}) - accepting");
                    return true;
                }
            }
            catch { }

            try
            {
                string value = acc.get_accValue(childId);
                if (value != null)
                {
                    Logger.Debug($"✅ CLIENT role with value interface - accepting");
                    return true;
                }
            }
            catch { }

            return false;
        }

        const int ROLE_SYSTEM_COMBOBOX = 0x2E;
        if (role == ROLE_SYSTEM_COMBOBOX)
        {
            Logger.Debug($"COMBOBOX detected - checking for editable child");
            
            try
            {
                string value = acc.get_accValue(childId);
                Logger.Debug($"COMBOBOX has value interface - accepting");
                return true;
            }
            catch { }

            try
            {
                int childCount = acc.accChildCount;
                
                if (childCount > 0)
                {
                    for (int i = 0; i < childCount; i++)
                    {
                        try
                        {
                            object child = acc.get_accChild(i + 1);
                            if (child is NativeMethods.IAccessible childAcc)
                            {
                                try
                                {
                                    object childRoleObj = childAcc.get_accRole(0);
                                    int childRole = (childRoleObj is int cr) ? cr : 0;
                                    
                                    object childStateObj = childAcc.get_accState(0);
                                    int childState = (childStateObj is int cs) ? cs : 0;
                                    bool childReadonly = (childState & NativeMethods.STATE_SYSTEM_READONLY) != 0;
                                    
                                    if (childRole == NativeMethods.ROLE_SYSTEM_TEXT && !childReadonly)
                                    {
                                        Logger.Debug($"✅ COMBOBOX has editable text child - accepting");
                                        Marshal.ReleaseComObject(childAcc);
                                        return true;
                                    }
                                    
                                    Marshal.ReleaseComObject(childAcc);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Debug($"Error checking combobox child: {ex.Message}");
                                    if (childAcc != null)
                                        Marshal.ReleaseComObject(childAcc);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            
            if (!isReadonly)
            {
                Logger.Debug($"COMBOBOX is focusable and not readonly - accepting");
                return true;
            }
            
            return false;
        }

        return false;
    }

    private bool IsEditorClass(string classLower)
    {
        if (string.IsNullOrEmpty(classLower)) return false;
        return EditorClassWhitelist.Any(editor => classLower.Contains(editor));
    }

    private bool IsChromeRenderClass(string classLower)
    {
        if (string.IsNullOrEmpty(classLower)) return false;
        return ChromeRenderClasses.Any(chrome => classLower.Contains(chrome));
    }

    private bool IsBlacklistedProcess(uint pid)
    {
        if (pid == 0) return false;

        try
        {
            IntPtr hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
                false,
                pid);

            if (hProcess == IntPtr.Zero) return false;

            try
            {
                StringBuilder processName = new StringBuilder(1024);
                uint size = NativeMethods.GetModuleFileNameEx(hProcess, IntPtr.Zero, processName, processName.Capacity);

                if (size > 0)
                {
                    string fullPath = processName.ToString().ToLowerInvariant();
                    string fileName = System.IO.Path.GetFileName(fullPath);
                    
                    return ProcessBlacklist.Any(blocked => fileName.Contains(blocked.ToLowerInvariant()));
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error checking process blacklist: {ex.Message}");
        }

        return false;
    }

    private bool IsBlacklistedClassName(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        string classLower = className.ToLowerInvariant();
        return ClassBlacklist.Any(blocked => classLower.Contains(blocked.ToLowerInvariant()));
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
            _clickDetector.HardwareClickDetected -= OnHardwareClickDetected;
        }

        Logger.Info("WinEventFocusTracker disposed");
    }
}

public class TextInputFocusEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; set; }
    public int ControlType { get; set; }
    public string ClassName { get; set; }
    public string Name { get; set; }
    public bool IsPassword { get; set; }
    public uint ProcessId { get; set; }
}

public class FocusEventArgs : EventArgs
{
    public IntPtr WindowHandle { get; set; }
    public int ControlType { get; set; }
    public string ClassName { get; set; }
}