using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA).
/// Uses GetCurrentInputMessageSource for reliable hardware input detection in WinEvent context.
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
        // Subscribe to global focus events
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
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS with GetCurrentInputMessageSource)");
        }

        // Subscribe to hardware click detector for "Already Focused" scenarios
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
    /// Callback for SetWinEventHook - called in the context of the window that received focus
    /// </summary>
    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed) return;
        
        // Ignore events from our own keyboard window
        if (hwnd == _keyboardWindowHandle) return;

        try
        {
            // ИЗМЕНЕНИЕ: Проверяем hardware input, но НЕ отклоняем при UNAVAILABLE
            bool isHardwareInput = false;
            bool hasRecentClick = false;
            
            if (_requireClickForAutoShow)
            {
                // Проверяем источник ввода
                var inputSource = CheckInputSource();
                
                if (inputSource == InputSourceType.DefinitelyProgrammatic)
                {
                    Logger.Debug("🚫 Focus change is DEFINITELY programmatic (IMO_INJECTED/IMO_SYSTEM) - ignoring");
                    return;
                }
                
                // Проверяем наличие недавнего клика мышью
                hasRecentClick = _clickDetector?.WasRecentHardwareClick() ?? false;
                
                if (inputSource == InputSourceType.DefinitelyHardware)
                {
                    isHardwareInput = true;
                    Logger.Debug("✅ Focus change confirmed as HARDWARE-initiated by GetCurrentInputMessageSource");
                }
                else if (inputSource == InputSourceType.Unavailable && hasRecentClick)
                {
                    isHardwareInput = true;
                    Logger.Debug("✅ Focus change assumed HARDWARE (UNAVAILABLE but recent click detected)");
                }
                else if (inputSource == InputSourceType.Unavailable && !hasRecentClick)
                {
                    // UNAVAILABLE без недавнего клика - вероятно клавиатурная навигация
                    Logger.Debug("⚠️ Focus change with UNAVAILABLE source and no recent click - might be keyboard navigation, ignoring");
                    return;
                }
                
                if (!isHardwareInput)
                {
                    Logger.Debug("Focus change detected, but NOT caused by hardware input - ignoring");
                    return;
                }
            }

            // Get the IAccessible object from the event
            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out NativeMethods.IAccessible acc, out object childId);
            
            if (hr >= 0 && acc != null)
            {
                // For focus events, verify hardware click was inside element bounds
                bool clickInsideBounds = false;
                
                if (_requireClickForAutoShow && _clickDetector != null && hasRecentClick)
                {
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        clickInsideBounds = _clickDetector.WasRecentHardwareClickInBounds(bounds);
                        
                        if (!clickInsideBounds)
                        {
                            Logger.Debug($"Hardware click was OUTSIDE element bounds - ignoring. Bounds: ({l}, {t}, {w}x{h})");
                            Marshal.ReleaseComObject(acc);
                            return;
                        }
                        
                        Logger.Debug($"✅ Hardware click was INSIDE element bounds ({l}, {t}, {w}x{h})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not verify bounds: {ex.Message}");
                        // For some controls, bounds check might fail - continue anyway
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

    private enum InputSourceType
    {
        DefinitelyHardware,    // IMO_HARDWARE с MOUSE/TOUCH/TOUCHPAD
        DefinitelyProgrammatic, // IMO_INJECTED или IMO_SYSTEM
        Unavailable            // IMO_UNAVAILABLE (неопределенный)
    }

    /// <summary>
    /// Проверяет источник ввода, возвращая категорию вместо bool
    /// </summary>
    private InputSourceType CheckInputSource()
    {
        try
        {
            bool success = NativeMethods.GetCurrentInputMessageSource(out NativeMethods.INPUT_MESSAGE_SOURCE source);
            
            if (!success)
            {
                Logger.Debug("GetCurrentInputMessageSource failed");
                return InputSourceType.Unavailable;
            }

            Logger.Debug($"Input source: DeviceType={source.deviceType}, OriginID={source.originId}");

            // Определенно программный ввод - отклоняем
            if (source.originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_INJECTED)
            {
                Logger.Debug("🚫 Input is INJECTED (SendInput)");
                return InputSourceType.DefinitelyProgrammatic;
            }
            
            if (source.originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_SYSTEM)
            {
                Logger.Debug("🚫 Input is SYSTEM-generated");
                return InputSourceType.DefinitelyProgrammatic;
            }

            // Определенно аппаратный ввод
            if (source.originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_HARDWARE)
            {
                if (source.deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_MOUSE ||
                    source.deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCH ||
                    source.deviceType == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD)
                {
                    return InputSourceType.DefinitelyHardware;
                }
            }

            // UNAVAILABLE - полагаемся на MouseClickDetector
            if (source.originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE)
            {
                Logger.Debug("⚠️ Input source UNAVAILABLE - checking MouseClickDetector");
                return InputSourceType.Unavailable;
            }

            return InputSourceType.Unavailable;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking input source", ex);
            return InputSourceType.Unavailable;
        }
    }

    /// <summary>
    /// Handles direct HARDWARE clicks to detect text fields that might ALREADY have focus
    /// </summary>
    private void OnHardwareClickDetected(object sender, Point clickPoint)
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
                int hr = NativeMethods.AccessibleObjectFromPoint(pt, out NativeMethods.IAccessible acc, out object childId);

                if (hr >= 0 && acc != null)
                {
                    IntPtr hwnd = IntPtr.Zero;
                    try
                    {
                        hwnd = NativeMethods.WindowFromAccessibleObject(acc);
                    }
                    catch { }

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
                Logger.Error("Error checking object at hardware click point", ex);
            }
        });
    }

    /// <summary>
    /// Core logic to determine if the object is a text input
    /// </summary>
    private void ProcessAccessibleObject(NativeMethods.IAccessible acc, object childId, IntPtr hwnd, bool isDirectClick)
    {
        try
        {
            // Get Process ID and check blacklist
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

            // Get ClassName and check blacklist/whitelist
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

            // Check Role
            object roleObj = acc.get_accRole(childId);
            int role = (roleObj is int r) ? r : 0;
            
            // Get State
            object stateObj = acc.get_accState(childId);
            int state = (stateObj is int s) ? s : 0;
            
            bool isProtected = (state & NativeMethods.STATE_SYSTEM_PROTECTED) != 0;
            bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
            bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
            bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

            // Determine if this is a text input
            bool isText = IsEditableTextInput(role, className, state, acc, childId);

            if (isText)
            {
                // Double check bounds if it was a direct click
                if (isDirectClick && _clickDetector != null)
                {
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        if (!_clickDetector.WasRecentHardwareClickInBounds(bounds))
                        {
                            Logger.Debug($"Hardware click detected, but outside element bounds. Role: {role}");
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

                // Truncate very long names
                if (name != null && name.Length > 100)
                {
                    name = name.Substring(0, 100) + "...";
                }

                Logger.Info($"{(isDirectClick ? "🖱️ Hardware Click" : "⚡ Focus")} on EDITABLE Text Input - Role: {role}, Class: {className}, Name: {name}");

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

    /// <summary>
    /// Enhanced logic to determine if element is an editable text input
    /// </summary>
    private bool IsEditableTextInput(int role, string className, int state, NativeMethods.IAccessible acc, object childId)
    {
        bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
        bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
        bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

        if (isUnavailable) return false;

        string classLower = className?.ToLowerInvariant() ?? "";

        // Whitelist: Known text editor classes
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

        // Chrome/Electron apps handling
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

        // Non-focusable elements are NOT text inputs
        if (!isFocusable)
        {
            Logger.Debug($"Element is not focusable (Role: {role})");
            return false;
        }

        // Explicit Edit Controls
        if (classLower.Contains("edit") && !isReadonly)
        {
            try
            {
                string value = acc.get_accValue(childId);
                return true;
            }
            catch { }
        }

        // Console/Terminal windows
        if (classLower.Contains("console") || classLower.Contains("cmd") || classLower.Contains("terminal"))
        {
            return true;
        }

        // ROLE_SYSTEM_TEXT - must be editable and focusable
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

        // Document Role
        if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT && !isReadonly)
        {
            return true;
        }

        // CLIENT Role - for editors like Scintilla
        if (role == NativeMethods.ROLE_SYSTEM_CLIENT)
        {
            if (isReadonly) return false;

            // Check for text content
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

            // Check for value interface
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

        // COMBOBOX handling
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