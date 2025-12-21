using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA) with UIA fallback.
/// Implements strict algorithm for virtual keyboard auto-show logic.
/// FIXED: Smart Explorer.exe filtering - allows address bar and search box
/// </summary>
public class WinEventFocusTracker : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private readonly PointerInputTracker _pointerTracker;
    private NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _isDisposed = false;
    
    private Func<bool> _isKeyboardVisible;

    // Process blacklist - REMOVED explorer.exe to allow address bar/search
    // Only blocking true system processes that never have text input
    private static readonly string[] ProcessBlacklist = new[]
    {
        "searchhost.exe",
        "startmenuexperiencehost.exe"
    };

    // Class blacklist - system UI elements that should never show keyboard
    // These are the actual file list views, desktop icons, etc.
    private static readonly string[] ClassBlacklist = new[]
    {
        "syslistview32",        // File list view in Explorer
        "shelldll_defview",     // Desktop/folder view
        "workerw",              // Desktop worker window
        "progman",              // Program Manager (desktop)
        "shell_traywnd",        // Taskbar
        "tasklistthumbnailwnd"  // Task switcher
    };

    // Explorer-specific classes that SHOULD be allowed (address bar, search box)
    private static readonly string[] ExplorerAllowedClasses = new[]
    {
        "addressbandroot",      // Address bar container
        "breadcrumbparent",     // Breadcrumb navigation
        "searchbox",            // Search box
        "universalsearchband",  // Universal search
        "directuihwnd"          // Direct UI (may contain address bar)
    };

    // Known text editor classes
    private static readonly string[] EditorClassWhitelist = new[]
    {
        "scintilla",
        "richedit",
        "edit",
        "akeleditwclass",
        "atlaxwin",
        "vscodecontentcontrol"
    };

    // Chrome/Electron render classes
    private static readonly string[] ChromeRenderClasses = new[]
    {
        "chrome_renderwidgethosthwnd",
        "chrome_widgetwin_",
        "intermediate d3d window"
    };

    public event EventHandler<TextInputFocusEventArgs> TextInputFocused;
    public event EventHandler<FocusEventArgs> NonTextInputFocused;

    public WinEventFocusTracker(IntPtr keyboardWindowHandle, PointerInputTracker pointerTracker)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _pointerTracker = pointerTracker;

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
            Logger.Error("❌ Failed to install WinEvent hook");
        }
        else
        {
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS)");
        }

        // Subscribe to hardware click detector for "Already Focused" scenarios
        if (_pointerTracker != null)
        {
            _pointerTracker.HardwareClickDetected += OnHardwareClickDetected;
            Logger.Info("✅ Direct click handler registered (for already-focused elements)");
        }
    }

    public void SetKeyboardVisibilityChecker(Func<bool> isVisible)
    {
        _isKeyboardVisible = isVisible;
    }

    /// <summary>
    /// Entry point: called when focus changes
    /// </summary>
    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed) return;
        
        // Ignore focus events from keyboard window
        if (hwnd == _keyboardWindowHandle)
        {
            Logger.Debug("🔒 Focus event from keyboard window - ignoring");
            return;
        }

        // Skip if keyboard already visible
        if (_isKeyboardVisible != null && _isKeyboardVisible())
        {
            Logger.Debug("⭕ Keyboard already visible - skipping");
            return;
        }

        Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Logger.Debug($"🎯 FOCUS EVENT: HWND={hwnd:X}, idObject={idObject}, idChild={idChild}");

        try
        {
            // STEP 1: Check if focused element is a text input
            var elementInfo = GetFocusedElementInfo(hwnd, idObject, idChild);
            
            if (elementInfo == null)
            {
                Logger.Debug("❌ STEP 1 FAILED: Could not get element info");
                Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                return;
            }

            if (!elementInfo.IsTextInput)
            {
                Logger.Debug($"❌ STEP 1 FAILED: Not a text input (Role={elementInfo.Role}, Class={elementInfo.ClassName})");
                Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                
                NonTextInputFocused?.Invoke(this, new FocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = elementInfo.Role,
                    ClassName = elementInfo.ClassName
                });
                return;
            }

            Logger.Info($"✅ STEP 1 PASSED: Element is text input");
            Logger.Debug($"   Role: {elementInfo.Role}");
            Logger.Debug($"   Class: {elementInfo.ClassName}");
            Logger.Debug($"   Name: {elementInfo.Name}");
            Logger.Debug($"   Process: {elementInfo.ProcessName}");
            Logger.Debug($"   Bounds: ({elementInfo.Bounds.X}, {elementInfo.Bounds.Y}, {elementInfo.Bounds.Width}x{elementInfo.Bounds.Height})");

            // STEP 2: Determine input source
            var inputSource = GetInputMessageSource();
            
            Logger.Debug($"🔥 INPUT SOURCE: DeviceType={inputSource.deviceType}, Origin={inputSource.originId}");

            // STEP 3: Decision logic
            bool shouldShowKeyboard = ShouldShowKeyboard(inputSource, elementInfo);

            if (shouldShowKeyboard)
            {
                Logger.Info("🎉 DECISION: SHOW KEYBOARD");
                Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                
                TextInputFocused?.Invoke(this, new TextInputFocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = elementInfo.Role,
                    ClassName = elementInfo.ClassName,
                    Name = elementInfo.Name,
                    IsPassword = elementInfo.IsPassword,
                    ProcessId = elementInfo.ProcessId
                });
            }
            else
            {
                Logger.Info("🚫 DECISION: DO NOT SHOW KEYBOARD");
                Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("❌ Error in WinEventProc", ex);
            Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        }
    }

    /// <summary>
    /// STEP 3: Main decision logic
    /// </summary>
    private bool ShouldShowKeyboard(NativeMethods.INPUT_MESSAGE_SOURCE inputSource, ElementInfo elementInfo)
    {
        var origin = inputSource.originId;
        var device = inputSource.deviceType;

        // CASE 4.1: Hardware input (mouse/touch/pen)
        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_HARDWARE)
        {
            if (device == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_MOUSE ||
                device == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCH ||
                device == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_PEN ||
                device == NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_TOUCHPAD)
            {
                Logger.Debug($"✅ STEP 2 PASSED: Hardware input from {device}");
                return true;
            }
            
            Logger.Debug($"⚠️ STEP 2: Hardware origin but unsupported device: {device}");
            return false;
        }

        // CASE 5: Programmatic focus (injected/system)
        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_INJECTED)
        {
            Logger.Debug("❌ STEP 2 FAILED: Programmatic input (IMO_INJECTED) - SendInput/SetFocus/etc");
            return false;
        }

        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_SYSTEM)
        {
            Logger.Debug("❌ STEP 2 FAILED: System-generated input (IMO_SYSTEM)");
            return false;
        }

        // CASE 4.2: Source unavailable - additional validation required
        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE)
        {
            Logger.Debug("⚠️ Input source UNAVAILABLE - performing additional validation");
            return ValidateWithPointerTracker(elementInfo);
        }

        Logger.Debug($"❌ STEP 2 FAILED: Unknown origin ID: {origin}");
        return false;
    }

    /// <summary>
    /// CASE 4.2: Additional validation when input source is unavailable
    /// </summary>
    private bool ValidateWithPointerTracker(ElementInfo elementInfo)
    {
        if (_pointerTracker == null)
        {
            Logger.Debug("   ❌ Validation FAILED: No pointer tracker available");
            return false;
        }

        var lastClick = _pointerTracker.GetLastPointerClick();
        
        if (lastClick == null)
        {
            Logger.Debug("   ❌ Validation FAILED: No recent pointer click recorded");
            return false;
        }

        Logger.Debug($"   🔍 Last pointer click: ({lastClick.Position.X}, {lastClick.Position.Y})");
        Logger.Debug($"      Device: {lastClick.DeviceType}");
        Logger.Debug($"      HWND: {lastClick.WindowHandle:X}");
        Logger.Debug($"      Time: {lastClick.Timestamp:HH:mm:ss.fff}");

        // Check 1: Was it a pointer input (mouse/touch/pen)?
        if (!lastClick.IsPointerInput)
        {
            Logger.Debug($"   ❌ Validation FAILED: Last click was not pointer input");
            return false;
        }
        Logger.Debug("   ✅ Check 1 passed: Last click was pointer input");

        // Check 2: Is this the most recent input?
        if (!_pointerTracker.IsLastInputPointerClick())
        {
            Logger.Debug("   ❌ Validation FAILED: Pointer click is not the most recent input");
            return false;
        }
        Logger.Debug("   ✅ Check 2 passed: Pointer click is the most recent input");

        // Check 3: Are windows related (same or parent/child)?
        if (!AreWindowsRelated(lastClick.WindowHandle, elementInfo.WindowHandle))
        {
            Logger.Debug($"   ❌ Validation FAILED: Click HWND ({lastClick.WindowHandle:X}) not related to focus HWND ({elementInfo.WindowHandle:X})");
            return false;
        }
        Logger.Debug($"   ✅ Check 3 passed: Click HWND related to focus HWND");

        // Check 3.5: CRITICAL Z-ORDER CHECK
        if (lastClick.WindowHandle != elementInfo.WindowHandle)
        {
            bool isDirectRelation = IsParentOf(lastClick.WindowHandle, elementInfo.WindowHandle) ||
                                   IsParentOf(elementInfo.WindowHandle, lastClick.WindowHandle);
            
            if (!isDirectRelation)
            {
                Logger.Debug($"   ❌ Validation FAILED: Click and focus windows are siblings/cousins (not direct parent/child)");
                Logger.Debug($"      This prevents false positives from dialogs/popups over text fields");
                return false;
            }
            
            Logger.Debug($"   ✅ Check 3.5 passed: Direct parent/child relationship confirmed");
        }

        // Check 4: Is click inside element bounds?
        if (!elementInfo.Bounds.Contains(lastClick.Position))
        {
            Logger.Debug($"   ❌ Validation FAILED: Click ({lastClick.Position.X}, {lastClick.Position.Y}) outside element bounds");
            Logger.Debug($"      Element bounds: ({elementInfo.Bounds.X}, {elementInfo.Bounds.Y}, {elementInfo.Bounds.Width}x{elementInfo.Bounds.Height})");
            return false;
        }
        Logger.Debug($"   ✅ Check 4 passed: Click inside element bounds");

        Logger.Debug("   ✅ ALL VALIDATION CHECKS PASSED");
        return true;
    }

    private bool IsParentOf(IntPtr potentialParent, IntPtr potentialChild)
    {
        if (potentialParent == IntPtr.Zero || potentialChild == IntPtr.Zero)
            return false;

        IntPtr parent = potentialChild;
        
        for (int i = 0; i < 30; i++)
        {
            parent = NativeMethods.GetParent(parent);
            if (parent == IntPtr.Zero) break;
            if (parent == potentialParent) return true;
        }
        
        return false;
    }

    private bool AreWindowsRelated(IntPtr hwnd1, IntPtr hwnd2)
    {
        if (hwnd1 == hwnd2)
        {
            Logger.Debug($"      Same HWND - related");
            return true;
        }
        
        if (hwnd1 == IntPtr.Zero || hwnd2 == IntPtr.Zero)
        {
            Logger.Debug($"      One HWND is zero - not related");
            return false;
        }

        IntPtr root1 = NativeMethods.GetAncestor(hwnd1, NativeMethods.GA_ROOTOWNER);
        IntPtr root2 = NativeMethods.GetAncestor(hwnd2, NativeMethods.GA_ROOTOWNER);
        
        if (root1 == IntPtr.Zero) root1 = hwnd1;
        if (root2 == IntPtr.Zero) root2 = hwnd2;
        
        Logger.Debug($"      Click HWND {hwnd1:X} -> Root owner: {root1:X}");
        Logger.Debug($"      Focus HWND {hwnd2:X} -> Root owner: {root2:X}");

        if (root1 == root2)
        {
            Logger.Debug($"      Same root owner - related");
            return true;
        }

        Logger.Debug($"      Different root owners - not related");
        return false;
    }

    private NativeMethods.INPUT_MESSAGE_SOURCE GetInputMessageSource()
    {
        try
        {
            bool success = NativeMethods.GetCurrentInputMessageSource(out NativeMethods.INPUT_MESSAGE_SOURCE source);
            
            if (!success)
            {
                Logger.Debug("⚠️ GetCurrentInputMessageSource API failed");
                return new NativeMethods.INPUT_MESSAGE_SOURCE
                {
                    deviceType = NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_UNAVAILABLE,
                    originId = NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE
                };
            }

            return source;
        }
        catch (Exception ex)
        {
            Logger.Error("Error calling GetCurrentInputMessageSource", ex);
            return new NativeMethods.INPUT_MESSAGE_SOURCE
            {
                deviceType = NativeMethods.INPUT_MESSAGE_DEVICE_TYPE.IMDT_UNAVAILABLE,
                originId = NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE
            };
        }
    }

    /// <summary>
    /// STEP 1: Get focused element information and check if it's a text input
    /// </summary>
    private ElementInfo GetFocusedElementInfo(IntPtr hwnd, int idObject, int idChild)
    {
        // Try MSAA first
        var msaaInfo = TryGetElementInfoViaMSAA(hwnd, idObject, idChild);
        if (msaaInfo != null)
        {
            Logger.Debug("   📋 Element info retrieved via MSAA");
            return msaaInfo;
        }

        // Try UIA fallback
        var uiaInfo = TryGetElementInfoViaUIA(hwnd);
        if (uiaInfo != null)
        {
            Logger.Debug("   📋 Element info retrieved via UIA (fallback)");
            return uiaInfo;
        }

        return null;
    }

    private ElementInfo TryGetElementInfoViaMSAA(IntPtr hwnd, int idObject, int idChild)
    {
        try
        {
            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out NativeMethods.IAccessible acc, out object childId);
            
            if (hr < 0 || acc == null)
                return null;

            try
            {
                // Get process info
                uint pid = 0;
                string processName = "";
                
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                    processName = GetProcessName(pid);
                    
                    // Check if process is blacklisted
                    if (IsBlacklistedProcess(processName))
                    {
                        Logger.Debug($"   🚫 Blacklisted process: {processName}");
                        return null;
                    }
                }

                // Get class name
                string className = "";
                if (hwnd != IntPtr.Zero)
                {
                    StringBuilder sb = new StringBuilder(256);
                    NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                    className = sb.ToString();
                }

                // NEW: Smart filtering for Explorer.exe
                if (processName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if this is a blacklisted Explorer class (file list, desktop, etc.)
                    if (IsBlacklistedClassName(className))
                    {
                        Logger.Debug($"   🚫 Blacklisted Explorer class: {className}");
                        return null;
                    }
                    
                    // If it's an allowed Explorer class, proceed
                    if (IsExplorerAllowedClass(className))
                    {
                        Logger.Debug($"   ✅ Allowed Explorer class: {className} (address bar/search)");
                    }
                }
                else
                {
                    // For non-Explorer processes, use standard blacklist
                    if (IsBlacklistedClassName(className))
                    {
                        Logger.Debug($"   🚫 Blacklisted window class: {className}");
                        return null;
                    }
                }

                // Get role, state, name
                object roleObj = acc.get_accRole(childId);
                int role = (roleObj is int r) ? r : 0;
                
                object stateObj = acc.get_accState(childId);
                int state = (stateObj is int s) ? s : 0;
                
                string name = "";
                try { name = acc.get_accName(childId); } catch { }
                
                if (name != null && name.Length > 100)
                    name = name.Substring(0, 100) + "...";

                // Get bounds
                Rectangle bounds = Rectangle.Empty;
                try
                {
                    acc.accLocation(out int l, out int t, out int w, out int h, childId);
                    bounds = new Rectangle(l, t, w, h);
                }
                catch { }

                bool isProtected = (state & NativeMethods.STATE_SYSTEM_PROTECTED) != 0;
                bool isTextInput = IsEditableTextInput(role, className, state, acc, childId);

                return new ElementInfo
                {
                    WindowHandle = hwnd,
                    Role = role,
                    ClassName = className,
                    Name = name,
                    Bounds = bounds,
                    IsPassword = isProtected,
                    ProcessId = pid,
                    ProcessName = processName,
                    IsTextInput = isTextInput,
                    DetectionMethod = "MSAA"
                };
            }
            finally
            {
                Marshal.ReleaseComObject(acc);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"   ⚠️ MSAA detection failed: {ex.Message}");
            return null;
        }
    }

    private ElementInfo TryGetElementInfoViaUIA(IntPtr hwnd)
    {
        try
        {
            // Get class name
            string className = "";
            if (hwnd != IntPtr.Zero)
            {
                StringBuilder sb = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                className = sb.ToString();
            }

            // Get process info
            uint pid = 0;
            string processName = "";
            
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                processName = GetProcessName(pid);
                
                if (IsBlacklistedProcess(processName))
                    return null;
            }

            // Smart Explorer filtering
            if (processName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (IsBlacklistedClassName(className))
                    return null;
            }
            else
            {
                if (IsBlacklistedClassName(className))
                    return null;
            }

            // Basic UIA check
            string classLower = className?.ToLowerInvariant() ?? "";
            bool isTextInput = IsEditorClass(classLower) || classLower.Contains("edit");

            // Get window bounds
            Rectangle bounds = Rectangle.Empty;
            if (hwnd != IntPtr.Zero)
            {
                if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
                {
                    bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                }
            }

            if (!isTextInput)
                return null;

            return new ElementInfo
            {
                WindowHandle = hwnd,
                Role = 0,
                ClassName = className,
                Name = "",
                Bounds = bounds,
                IsPassword = false,
                ProcessId = pid,
                ProcessName = processName,
                IsTextInput = isTextInput,
                DetectionMethod = "UIA"
            };
        }
        catch (Exception ex)
        {
            Logger.Debug($"   ⚠️ UIA detection failed: {ex.Message}");
            return null;
        }
    }

    private bool IsEditableTextInput(int role, string className, int state, NativeMethods.IAccessible acc, object childId)
    {
        bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
        bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
        bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

        if (isUnavailable) return false;

        string classLower = className?.ToLowerInvariant() ?? "";

        // CARET - insertion point
        if (role == NativeMethods.ROLE_SYSTEM_CARET)
        {
            Logger.Debug($"   ✅ CARET (insertion point) detected");
            return true;
        }

        // Known editor classes
        if (IsEditorClass(classLower))
        {
            if (isReadonly)
            {
                Logger.Debug($"   ❌ Editor class '{className}' but readonly");
                return false;
            }
            
            Logger.Debug($"   ✅ Recognized editor class: {className}");
            return true;
        }

        // Chrome/Electron apps
        if (IsChromeRenderClass(classLower))
        {
            if (isFocusable)
            {
                try
                {
                    string value = acc.get_accValue(childId);
                    Logger.Debug($"   ✅ Chrome render widget with value interface");
                    return true;
                }
                catch { }

                if (role == NativeMethods.ROLE_SYSTEM_PANE || role == NativeMethods.ROLE_SYSTEM_CLIENT)
                {
                    Logger.Debug($"   ✅ Chrome render widget with focusable pane/client");
                    return true;
                }
            }
            
            return false;
        }

        if (!isFocusable)
        {
            Logger.Debug($"   ❌ Element is not focusable (Role: {role})");
            return false;
        }

        // Edit controls
        if (classLower.Contains("edit") && !isReadonly)
        {
            try
            {
                string value = acc.get_accValue(childId);
                return true;
            }
            catch { }
        }

        // Console/Terminal
        if (classLower.Contains("console") || classLower.Contains("cmd") || classLower.Contains("terminal"))
            return true;

        // ROLE_SYSTEM_TEXT
        if (role == NativeMethods.ROLE_SYSTEM_TEXT)
        {
            if (isReadonly)
            {
                Logger.Debug($"   ❌ ROLE_SYSTEM_TEXT but readonly");
                return false;
            }

            try
            {
                string value = acc.get_accValue(childId);
                Logger.Debug($"   ✅ ROLE_SYSTEM_TEXT with value interface");
                return true;
            }
            catch
            {
                Logger.Debug($"   ❌ ROLE_SYSTEM_TEXT without value interface");
                return false;
            }
        }

        // DOCUMENT
        if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT && !isReadonly)
            return true;

        // CLIENT
        if (role == NativeMethods.ROLE_SYSTEM_CLIENT)
        {
            if (isReadonly) return false;

            try
            {
                string name = acc.get_accName(childId);
                if (!string.IsNullOrEmpty(name) && name.Length > 20)
                {
                    Logger.Debug($"   ✅ CLIENT role with text content (length: {name.Length})");
                    return true;
                }
            }
            catch { }

            try
            {
                string value = acc.get_accValue(childId);
                if (value != null)
                {
                    Logger.Debug($"   ✅ CLIENT role with value interface");
                    return true;
                }
            }
            catch { }

            return false;
        }

        // COMBOBOX
        if (role == NativeMethods.ROLE_SYSTEM_COMBOBOX)
        {
            try
            {
                string value = acc.get_accValue(childId);
                Logger.Debug($"   ✅ COMBOBOX with value interface");
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
                                        Logger.Debug($"   ✅ COMBOBOX has editable text child");
                                        Marshal.ReleaseComObject(childAcc);
                                        return true;
                                    }
                                    
                                    Marshal.ReleaseComObject(childAcc);
                                }
                                catch
                                {
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
                Logger.Debug($"   ✅ COMBOBOX is focusable and not readonly");
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

    /// <summary>
    /// NEW: Check if Explorer class is explicitly allowed (address bar, search box)
    /// </summary>
    private bool IsExplorerAllowedClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        string classLower = className.ToLowerInvariant();
        return ExplorerAllowedClasses.Any(allowed => classLower.Contains(allowed.ToLowerInvariant()));
    }

    /// <summary>
    /// Get process name from PID
    /// </summary>
    private string GetProcessName(uint pid)
    {
        if (pid == 0) return "";

        try
        {
            IntPtr hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
                false,
                pid);

            if (hProcess == IntPtr.Zero) return "";

            try
            {
                StringBuilder processName = new StringBuilder(1024);
                uint size = NativeMethods.GetModuleFileNameEx(hProcess, IntPtr.Zero, processName, processName.Capacity);

                if (size > 0)
                {
                    string fullPath = processName.ToString();
                    return System.IO.Path.GetFileName(fullPath);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProcess);
            }
        }
        catch { }

        return "";
    }

    private bool IsBlacklistedProcess(string processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return ProcessBlacklist.Any(blocked => 
            processName.Equals(blocked, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsBlacklistedClassName(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        string classLower = className.ToLowerInvariant();
        return ClassBlacklist.Any(blocked => classLower.Contains(blocked.ToLowerInvariant()));
    }

    private bool IsSystemControl(NativeMethods.IAccessible acc, object childId, PointerClickInfo clickInfo)
    {
        try
        {
            object roleObj = acc.get_accRole(childId);
            int role = (roleObj is int r) ? r : 0;

            if (role == NativeMethods.ROLE_SYSTEM_PUSHBUTTON ||
                role == NativeMethods.ROLE_SYSTEM_MENUITEM ||
                role == NativeMethods.ROLE_SYSTEM_MENUBAR ||
                role == NativeMethods.ROLE_SYSTEM_MENUPOPUP ||
                role == NativeMethods.ROLE_SYSTEM_TITLEBAR ||
                role == NativeMethods.ROLE_SYSTEM_WINDOW)
            {
                Logger.Debug($"   🚫 System control role detected: {role}");
                return true;
            }

            string name = "";
            try { name = acc.get_accName(childId); } catch { }

            if (!string.IsNullOrEmpty(name))
            {
                string nameLower = name.ToLowerInvariant();
                
                if (nameLower.Contains("close") || 
                    nameLower.Contains("закрыть") ||
                    nameLower.Contains("zamknij") ||
                    nameLower.Contains("minimize") ||
                    nameLower.Contains("maximize") ||
                    nameLower.Contains("restore"))
                {
                    Logger.Debug($"   🚫 System window control detected: '{name}'");
                    return true;
                }
            }

            if (clickInfo.WindowHandle != IntPtr.Zero)
            {
                if (NativeMethods.GetWindowRect(clickInfo.WindowHandle, out NativeMethods.RECT rect))
                {
                    int windowWidth = rect.Right - rect.Left;
                    int relX = clickInfo.Position.X - rect.Left;
                    int relY = clickInfo.Position.Y - rect.Top;
                    
                    bool isTopRightCorner = relX > (windowWidth - 150) && relY < 50;
                    
                    if (isTopRightCorner)
                    {
                        Logger.Debug($"   🚫 Click in top-right corner detected (close button area)");
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"   ⚠️ Error checking system control: {ex.Message}");
            return false;
        }
    }

    private void OnHardwareClickDetected(object sender, PointerClickInfo clickInfo)
    {
        if (_isDisposed) return;

        if (_isKeyboardVisible != null && _isKeyboardVisible())
        {
            Logger.Debug("⭕ Direct click ignored: Keyboard already visible");
            return;
        }

        Logger.Debug($"🔍 Direct click detected: ({clickInfo.Position.X}, {clickInfo.Position.Y}) HWND={clickInfo.WindowHandle:X}");

        try
        {
            NativeMethods.POINT pt = new NativeMethods.POINT { X = clickInfo.Position.X, Y = clickInfo.Position.Y };
            
            int hr = NativeMethods.AccessibleObjectFromPoint(pt, out NativeMethods.IAccessible acc, out object childId);

            if (hr >= 0 && acc != null)
            {
                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    hwnd = NativeMethods.WindowFromAccessibleObject(acc);
                }
                catch { }

                if (IsSystemControl(acc, childId, clickInfo))
                {
                    Logger.Debug("   🚫 System control detected (button/close/menu) - ignoring");
                    Marshal.ReleaseComObject(acc);
                    return;
                }

                if (hwnd == IntPtr.Zero)
                {
                    Logger.Debug($"   ⚠️ WindowFromAccessibleObject returned 0 - using click HWND: {clickInfo.WindowHandle:X}");
                    hwnd = clickInfo.WindowHandle;
                }

                if (hwnd == _keyboardWindowHandle)
                {
                    Marshal.ReleaseComObject(acc);
                    return;
                }

                Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                Logger.Debug($"🔍 DIRECT CLICK CHECK: HWND={hwnd:X}");

                var elementInfo = GetElementInfoFromAccessible(acc, childId, hwnd);
                
                if (elementInfo != null && elementInfo.IsTextInput)
                {
                    Logger.Info($"✅ Direct click on text input - Role: {elementInfo.Role}, Class: {elementInfo.ClassName}");
                    
                    if (clickInfo.WindowHandle != hwnd && clickInfo.WindowHandle != elementInfo.WindowHandle)
                    {
                        Logger.Debug($"⚠️ Warning: Click HWND ({clickInfo.WindowHandle:X}) != Element HWND ({elementInfo.WindowHandle:X})");
                        
                        bool isDirectRelation = IsParentOf(clickInfo.WindowHandle, elementInfo.WindowHandle) ||
                                               IsParentOf(elementInfo.WindowHandle, clickInfo.WindowHandle);
                        
                        if (!isDirectRelation)
                        {
                            Logger.Debug($"❌ Not direct parent/child - likely a dialog/popup over text field");
                            Marshal.ReleaseComObject(acc);
                            Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                            return;
                        }
                        
                        Logger.Debug($"✅ Direct parent/child relationship confirmed");
                    }
                    
                    if (!elementInfo.Bounds.Contains(clickInfo.Position))
                    {
                        Logger.Debug($"❌ Click outside element bounds");
                        Marshal.ReleaseComObject(acc);
                        Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                        return;
                    }

                    Logger.Info("🎉 DECISION: SHOW KEYBOARD (direct click on already-focused element)");
                    Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    
                    TextInputFocused?.Invoke(this, new TextInputFocusEventArgs
                    {
                        WindowHandle = hwnd,
                        ControlType = elementInfo.Role,
                        ClassName = elementInfo.ClassName,
                        Name = elementInfo.Name,
                        IsPassword = elementInfo.IsPassword,
                        ProcessId = elementInfo.ProcessId
                    });
                }
                else
                {
                    Logger.Debug($"❌ Not a text input - Role: {elementInfo?.Role ?? 0}");
                    Logger.Debug("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                }

                Marshal.ReleaseComObject(acc);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking object at hardware click point", ex);
        }
    }

    private ElementInfo GetElementInfoFromAccessible(NativeMethods.IAccessible acc, object childId, IntPtr hwnd)
    {
        try
        {
            uint pid = 0;
            string processName = "";
            
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                processName = GetProcessName(pid);
                
                if (IsBlacklistedProcess(processName))
                {
                    Logger.Debug($"   🚫 Blacklisted process: {processName}");
                    return null;
                }
            }

            string className = "";
            if (hwnd != IntPtr.Zero)
            {
                StringBuilder sb = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
                className = sb.ToString();
            }

            // Smart Explorer filtering
            if (processName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (IsBlacklistedClassName(className))
                {
                    Logger.Debug($"   🚫 Blacklisted Explorer class: {className}");
                    return null;
                }
            }
            else
            {
                if (IsBlacklistedClassName(className))
                {
                    Logger.Debug($"   🚫 Blacklisted window class: {className}");
                    return null;
                }
            }

            object roleObj = acc.get_accRole(childId);
            int role = (roleObj is int r) ? r : 0;
            
            object stateObj = acc.get_accState(childId);
            int state = (stateObj is int s) ? s : 0;
            
            string name = "";
            try { name = acc.get_accName(childId); } catch { }
            
            if (name != null && name.Length > 100)
                name = name.Substring(0, 100) + "...";

            Rectangle bounds = Rectangle.Empty;
            try
            {
                acc.accLocation(out int l, out int t, out int w, out int h, childId);
                bounds = new Rectangle(l, t, w, h);
            }
            catch { }

            bool isProtected = (state & NativeMethods.STATE_SYSTEM_PROTECTED) != 0;
            bool isTextInput = IsEditableTextInput(role, className, state, acc, childId);

            return new ElementInfo
            {
                WindowHandle = hwnd,
                Role = role,
                ClassName = className,
                Name = name,
                Bounds = bounds,
                IsPassword = isProtected,
                ProcessId = pid,
                ProcessName = processName,
                IsTextInput = isTextInput,
                DetectionMethod = "DirectClick"
            };
        }
        catch (Exception ex)
        {
            Logger.Debug($"   ⚠️ Error getting element info: {ex.Message}");
            return null;
        }
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

        if (_pointerTracker != null)
        {
            _pointerTracker.HardwareClickDetected -= OnHardwareClickDetected;
        }

        Logger.Info("WinEventFocusTracker disposed");
    }
}

internal class ElementInfo
{
    public IntPtr WindowHandle { get; set; }
    public int Role { get; set; }
    public string ClassName { get; set; }
    public string Name { get; set; }
    public Rectangle Bounds { get; set; }
    public bool IsPassword { get; set; }
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; }
    public bool IsTextInput { get; set; }
    public string DetectionMethod { get; set; }
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