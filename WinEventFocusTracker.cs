using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA).
/// Enhanced for Chrome/Edge with relaxed bounds checking.
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

    private static readonly string[] ProcessBlacklist = new[]
    {
        "explorer.exe",
        "searchhost.exe",
        "startmenuexperiencehost.exe"
    };

    private static readonly string[] ClassBlacklist = new[]
    {
        "syslistview32",
        "directuihwnd",
        "cabinetwclass",
        "workerw",
        "progman"
    };

    private static readonly string[] EditorClassWhitelist = new[]
    {
        "scintilla",
        "richedit",
        "edit",
        "akeleditwclass",
        "atlaxwin",
        "vscodecontentcontrol"
    };

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
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS)");
        }

        if (_clickDetector != null)
        {
            _clickDetector.ClickDetected += OnClickDetected;
        }
    }

    public void SetKeyboardVisibilityChecker(Func<bool> isVisible)
    {
        _isKeyboardVisible = isVisible;
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed) return;
        
        if (hwnd == _keyboardWindowHandle) return;

        try
        {
            if (_requireClickForAutoShow && _clickDetector != null)
            {
                if (!_clickDetector.WasRecentClick())
                {
                    Logger.Debug("Focus changed, but no recent click detected. Ignoring.");
                    return;
                }
            }

            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out NativeMethods.IAccessible acc, out object childId);
            
            if (hr >= 0 && acc != null)
            {
                // For Chrome/Edge, bounds checking is unreliable due to multi-process architecture
                bool isChromeWidget = IsChromeWindow(hwnd);
                bool clickInsideBounds = false;
                
                if (_requireClickForAutoShow && _clickDetector != null && !isChromeWidget)
                {
                    // Only check bounds for non-Chrome windows
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        clickInsideBounds = _clickDetector.WasRecentClickInBounds(bounds);
                        
                        if (!clickInsideBounds)
                        {
                            Logger.Debug($"Focus event: Click OUTSIDE element bounds. Ignoring. Bounds: ({l}, {t}, {w}x{h})");
                            Marshal.ReleaseComObject(acc);
                            return;
                        }
                        
                        Logger.Debug($"✅ Focus event: Click INSIDE element bounds ({l}, {t}, {w}x{h})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not verify bounds for focus event: {ex.Message}");
                    }
                }
                else if (isChromeWidget)
                {
                    Logger.Debug("✅ Chrome/Edge window - skipping bounds check due to multi-process architecture");
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

    private void OnClickDetected(object sender, Point clickPoint)
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
                Logger.Error("Error checking object at click point", ex);
            }
        });
    }

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
                if (isDirectClick && _clickDetector != null)
                {
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        if (!_clickDetector.WasRecentClickInBounds(bounds))
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

                Logger.Info($"{(isDirectClick ? "🖱️ Click" : "⚡ Focus")} on EDITABLE Text Input - Role: {role}, Class: {className}, Name: {name}, State: Readonly={isReadonly}, Focusable={isFocusable}");

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
                Logger.Debug($"Not a text input - Role: {role}, Class: {className}, State: Readonly={isReadonly}, Focusable={isFocusable}");
                
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

        if (isUnavailable)
        {
            return false;
        }

        string classLower = className?.ToLowerInvariant() ?? "";

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

        // Enhanced Chrome/Electron detection
        if (IsChromeRenderClass(classLower))
        {
            Logger.Debug($"Detected Chrome render class: {className}, Role: {role}, Focusable: {isFocusable}, Readonly: {isReadonly}");
            
            // CRITICAL: Chrome pages are often ROLE_SYSTEM_DOCUMENT with Readonly=True
            // Real text inputs are Readonly=False
            if (isReadonly)
            {
                Logger.Debug($"Chrome widget is readonly - rejecting (not an input field)");
                return false;
            }
            
            if (isFocusable)
            {
                // Try value interface - but must NOT be readonly
                try
                {
                    string value = acc.get_accValue(childId);
                    Logger.Debug($"✅ Chrome widget has value interface and is NOT readonly - accepting");
                    return true;
                }
                catch { }

                // Check for input-related roles (TEXT only, not DOCUMENT for Chrome)
                const int ROLE_SYSTEM_PANE = 0x10;
                if (role == ROLE_SYSTEM_PANE || 
                    role == NativeMethods.ROLE_SYSTEM_CLIENT ||
                    role == NativeMethods.ROLE_SYSTEM_TEXT)
                {
                    Logger.Debug($"✅ Chrome widget with input role ({role}) and NOT readonly - accepting");
                    return true;
                }
                
                // For Chrome DOCUMENT role - only accept if we can verify it's actually editable
                if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT)
                {
                    // Check if it has editable descendants or contentEditable
                    try
                    {
                        string name = acc.get_accName(childId);
                        // If it has a generic page name like "Новая вкладка", it's NOT an input
                        if (name != null && (name.Contains("вкладка") || name.Contains("tab") || name.Contains("page")))
                        {
                            Logger.Debug($"Chrome DOCUMENT with generic page name '{name}' - rejecting");
                            return false;
                        }
                    }
                    catch { }
                    
                    // Only accept DOCUMENT if it has value interface (contentEditable)
                    try
                    {
                        string value = acc.get_accValue(childId);
                        if (value != null)
                        {
                            Logger.Debug($"✅ Chrome DOCUMENT with value interface - accepting as contentEditable");
                            return true;
                        }
                    }
                    catch { }
                    
                    Logger.Debug($"Chrome DOCUMENT without value interface - rejecting");
                    return false;
                }
            }
            
            Logger.Debug($"Chrome render widget but not detected as input");
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
            if (isReadonly)
            {
                return false;
            }

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

            Logger.Debug($"CLIENT role but no indicators of text input");
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
        if (string.IsNullOrEmpty(classLower))
            return false;

        return EditorClassWhitelist.Any(editor => classLower.Contains(editor));
    }

    private bool IsChromeRenderClass(string classLower)
    {
        if (string.IsNullOrEmpty(classLower))
            return false;

        return ChromeRenderClasses.Any(chrome => classLower.Contains(chrome));
    }

    private bool IsChromeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        
        try
        {
            StringBuilder className = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            string classStr = className.ToString().ToLowerInvariant();
            
            return classStr.Contains("chrome_widgetwin") || 
                   classStr.Contains("chrome_renderwidget") ||
                   classStr.Contains("intermediate d3d window");
        }
        catch
        {
            return false;
        }
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

            if (hProcess == IntPtr.Zero)
                return false;

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
        if (string.IsNullOrEmpty(className))
            return false;

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
            _clickDetector.ClickDetected -= OnClickDetected;
        }
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