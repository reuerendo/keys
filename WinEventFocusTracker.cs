using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA).
/// Replaces the heavy UI Automation approach while maintaining strict click-to-show logic.
/// FIXED: Enhanced support for Chrome/Electron apps (Signal, Discord, VS Code, Edge)
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
        "explorer.exe",      // Windows Explorer
        "searchhost.exe",    // Windows Search
        "startmenuexperiencehost.exe"  // Start Menu
    };

    // Blacklist of window classes that should be excluded
    private static readonly string[] ClassBlacklist = new[]
    {
        "syslistview32",     // List views (Explorer file list)
        "directuihwnd",      // Explorer file dialogs
        "cabinetwclass",     // Explorer windows
        "workerw",           // Desktop worker
        "progman"            // Program Manager
    };

    // Known text editor classes that should always trigger auto-show
    private static readonly string[] EditorClassWhitelist = new[]
    {
        "scintilla",              // Notepad++, Sublime Text, etc.
        "richedit",               // Rich Edit controls
        "edit",                   // Standard edit controls
        "akeleditwclass",         // AkelPad editor
        "atlaxwin",               // Various editors
        "vscodecontentcontrol"    // VS Code (if using native HWND)
    };

    // Known Chrome/Electron render widget classes
    private static readonly string[] ChromeRenderClasses = new[]
    {
        "chrome_renderwidgethosthwnd",  // Chrome/Electron apps
        "chrome_widgetwin_",             // Chrome widgets
        "intermediate d3d window"        // Hardware accelerated Chrome
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
            // CRITICAL: If we require a click, check if click was recent
            if (_requireClickForAutoShow && _clickDetector != null)
            {
                if (!_clickDetector.WasRecentClick())
                {
                    Logger.Debug("Focus changed, but no recent click detected. Ignoring.");
                    return;
                }
            }

            // Get the IAccessible object from the event
            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out NativeMethods.IAccessible acc, out object childId);
            
            if (hr >= 0 && acc != null)
            {
                // CRITICAL: For focus events, verify click was inside element bounds
                bool clickInsideBounds = false;
                
                if (_requireClickForAutoShow && _clickDetector != null)
                {
                    try
                    {
                        acc.accLocation(out int l, out int t, out int w, out int h, childId);
                        Rectangle bounds = new Rectangle(l, t, w, h);
                        clickInsideBounds = _clickDetector.WasRecentClickInBounds(bounds);
                        
                        if (!clickInsideBounds)
                        {
                            // FIXED: For collapsed/minimized states, bounds might be wrong - skip check
                            object stateObj = acc.get_accState(childId);
                            int state = (stateObj is int s) ? s : 0;
                            
                            if ((state & NativeMethods.STATE_SYSTEM_COLLAPSED) != 0)
                            {
                                Logger.Debug($"⚠️ Element is collapsed, ignoring bounds check. Bounds: ({l}, {t}, {w}x{h})");
                                clickInsideBounds = true;
                            }
                            else
                            {
                                Logger.Debug($"Focus event: Click was OUTSIDE element bounds. Ignoring. Bounds: ({l}, {t}, {w}x{h})");
                                Marshal.ReleaseComObject(acc);
                                return;
                            }
                        }
                        else
                        {
                            Logger.Debug($"✅ Focus event: Click was INSIDE element bounds ({l}, {t}, {w}x{h})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Could not verify bounds for focus event: {ex.Message}");
                        // For some controls (combobox, editors, Chrome apps), bounds check might fail - continue anyway
                        clickInsideBounds = true;
                    }
                }
                else
                {
                    clickInsideBounds = true;
                }
                
                if (clickInsideBounds)
                {
                    ProcessAccessibleObject(acc, childId, hwnd, isDirectClick: false);
                }
                
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
                int hr = NativeMethods.AccessibleObjectFromPoint(pt, out NativeMethods.IAccessible acc, out object childId);

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
    private void ProcessAccessibleObject(NativeMethods.IAccessible acc, object childId, IntPtr hwnd, bool isDirectClick)
    {
        try
        {
            // 1. Get Process ID and check blacklist
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

            // 2. Get ClassName and check blacklist/whitelist
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

            // 3. Check Role
            object roleObj = acc.get_accRole(childId);
            int role = (roleObj is int r) ? r : 0;
            
            // 4. Get State
            object stateObj = acc.get_accState(childId);
            int state = (stateObj is int s) ? s : 0;
            
            bool isProtected = (state & NativeMethods.STATE_SYSTEM_PROTECTED) != 0;
            bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
            bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
            bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

            // FIXED: Enhanced debug logging for Chrome apps
            if (IsChromeRenderClass(className?.ToLowerInvariant() ?? ""))
            {
                Logger.Debug($"🔍 Chrome App Element: Role={role}, State=0x{state:X}, " +
                           $"Focusable={isFocusable}, Readonly={isReadonly}, " +
                           $"Protected={isProtected}, Class={className}");
            }

            // 5. Determine if this is a text input
            bool isText = IsEditableTextInput(role, className, state, acc, childId);

            if (isText)
            {
                // Double check bounds if it was a direct click (ensure we actually clicked INSIDE)
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
                        // Bounds check failed - for certain controls this is OK, continue
                        Logger.Debug("Bounds check failed, but element is text input - continuing");
                    }
                }
                
                string name = "";
                try { name = acc.get_accName(childId); } catch { }

                // Truncate very long names (like full file content in Scintilla)
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
            // COM exceptions can happen if object dies quickly
            Logger.Debug($"Error processing accessible object: {ex.Message}");
        }
    }

    /// <summary>
    /// Enhanced logic to determine if element is an editable text input
    /// FIXED: Added support for ROLE_SYSTEM_GROUPING (Signal, Discord)
    /// </summary>
    private bool IsEditableTextInput(int role, string className, int state, NativeMethods.IAccessible acc, object childId)
    {
        // CRITICAL: Exclude readonly and unavailable elements
        bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
        bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
        bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

        if (isUnavailable)
        {
            return false;
        }

        string classLower = className?.ToLowerInvariant() ?? "";

        // ===== WHITELIST: Known text editor classes =====
        // These classes are ALWAYS text inputs when focused
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

        // ===== CHROME/ELECTRON APPS (Signal, Discord, VS Code, etc.) =====
        // FIXED: Enhanced detection with GROUPING role support
        if (IsChromeRenderClass(classLower))
        {
            // Chrome render widgets need special handling
            // They often don't expose proper IAccessible, but we can infer from context
            
            if (isFocusable)
            {
                // Try to get value - if it has value interface, likely an input
                try
                {
                    string value = acc.get_accValue(childId);
                    Logger.Debug($"✅ Chrome render widget has value interface - accepting");
                    return true;
                }
                catch
                {
                    // No value interface
                }

                // FIXED: Check for GROUPING role (Signal, Discord message boxes)
                if (role == NativeMethods.ROLE_SYSTEM_GROUPING)
                {
                    Logger.Debug($"✅ Chrome render GROUPING (Signal/Discord) - accepting");
                    return true;
                }

                // Check for specific roles that might indicate input areas
                if (role == NativeMethods.ROLE_SYSTEM_PANE || role == NativeMethods.ROLE_SYSTEM_CLIENT)
                {
                    // For Chrome/Electron, panes and client areas that are focusable
                    // are often text input areas (like Signal message box)
                    Logger.Debug($"✅ Chrome render widget with focusable pane/client - accepting");
                    return true;
                }
            }
            
            Logger.Debug($"Chrome render widget but not detected as input (Role: {role})");
            return false;
        }

        // ===== STANDARD CHECKS =====
        
        // Non-focusable elements are NOT text inputs
        // This filters out static text in lists (like Explorer file names)
        if (!isFocusable)
        {
            Logger.Debug($"Element is not focusable (Role: {role})");
            return false;
        }

        // 1. Explicit Edit Controls (Win32 edit boxes)
        if (classLower.Contains("edit") && !isReadonly)
        {
            try
            {
                string value = acc.get_accValue(childId);
                return true;
            }
            catch { }
        }

        // 2. Console/Terminal windows
        if (classLower.Contains("console") || classLower.Contains("cmd") || classLower.Contains("terminal"))
        {
            return true;
        }

        // 3. ROLE_SYSTEM_TEXT - MUST be editable and focusable
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

        // 4. Document Role (Word, Browsers) - only if editable
        if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT && !isReadonly)
        {
            return true;
        }

        // 5. CLIENT Role - for editors like Scintilla (Notepad++)
        if (role == NativeMethods.ROLE_SYSTEM_CLIENT)
        {
            // Client role is generic, need additional checks
            
            // If it's readonly, definitely not an input
            if (isReadonly)
            {
                return false;
            }

            // Check if it has a name that looks like text content
            // Scintilla puts the document content in the Name property
            try
            {
                string name = acc.get_accName(childId);
                if (!string.IsNullOrEmpty(name) && name.Length > 20)
                {
                    // Has substantial text content - likely an editor
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

            Logger.Debug($"CLIENT role but no indicators of text input");
            return false;
        }

        // 6. COMBOBOX (0x2E) - Check if it has editable text child
        if (role == NativeMethods.ROLE_SYSTEM_COMBOBOX)
        {
            Logger.Debug($"COMBOBOX detected - checking for editable child");
            
            // Check if the combobox itself has value interface (editable)
            try
            {
                string value = acc.get_accValue(childId);
                Logger.Debug($"COMBOBOX has value interface - accepting");
                return true;
            }
            catch { }

            // Check if it has editable text child
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
            
            // Even if we can't check children, if it's focusable and not readonly, accept it
            if (!isReadonly)
            {
                Logger.Debug($"COMBOBOX is focusable and not readonly - accepting");
                return true;
            }
            
            return false;
        }

        // FIXED: 7. GROUPING (0x14) - Used by Chrome apps like Signal, Discord
        // This is a critical addition for modern web-based apps
        if (role == NativeMethods.ROLE_SYSTEM_GROUPING)
        {
            // Must be focusable and not readonly
            if (!isFocusable || isReadonly)
            {
                Logger.Debug($"GROUPING but not focusable or readonly");
                return false;
            }

            // Check if it has value interface
            try
            {
                string value = acc.get_accValue(childId);
                if (value != null)
                {
                    Logger.Debug($"✅ GROUPING with value interface - accepting");
                    return true;
                }
            }
            catch { }

            // For Chrome apps, focusable GROUPING is often a text input
            if (IsChromeRenderClass(classLower))
            {
                Logger.Debug($"✅ Chrome GROUPING focusable - accepting");
                return true;
            }

            // Generic focusable grouping might be a text input (give it a chance)
            Logger.Debug($"⚠️ Generic focusable GROUPING - accepting with caution");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Check if class name is a known text editor
    /// </summary>
    private bool IsEditorClass(string classLower)
    {
        if (string.IsNullOrEmpty(classLower))
            return false;

        return EditorClassWhitelist.Any(editor => classLower.Contains(editor));
    }

    /// <summary>
    /// Check if class name is a Chrome/Electron render widget
    /// </summary>
    private bool IsChromeRenderClass(string classLower)
    {
        if (string.IsNullOrEmpty(classLower))
            return false;

        return ChromeRenderClasses.Any(chrome => classLower.Contains(chrome));
    }

    /// <summary>
    /// Check if process is in blacklist
    /// </summary>
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

    /// <summary>
    /// Check if window class is in blacklist
    /// </summary>
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

// --- Event Argument Classes ---

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