using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace VirtualKeyboard;

/// <summary>
/// Lightweight focus tracker using SetWinEventHook and IAccessible (MSAA) with UIA fallback.
/// Implements strict algorithm for virtual keyboard auto-show logic.
/// </summary>
public class WinEventFocusTracker : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private readonly PointerInputTracker _pointerTracker;
    private NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _isDisposed = false;
    private Func<bool> _isKeyboardVisible;

    private static readonly string[] ProcessBlacklist = new[]
    {
        "explorer.exe", "searchhost.exe", "startmenuexperiencehost.exe"
    };

    private static readonly string[] ClassBlacklist = new[]
    {
        "syslistview32", "directuihwnd", "cabinetwclass", "shelldll_defview", "workerw", "progman"
    };

    private static readonly string[] EditorClassWhitelist = new[]
    {
        "scintilla", "richedit", "edit", "akeleditwclass", "atlaxwin", "vscodecontentcontrol"
    };

    private static readonly string[] ChromeRenderClasses = new[]
    {
        "chrome_renderwidgethosthwnd", "chrome_widgetwin_", "intermediate d3d window"
    };

    private static readonly string[] WebBrowserClasses = new[]
    {
        "mozillawindowclass",
        "chrome_widgetwin_",
        "applicationframewindow"
    };

    private static readonly int[] SystemControlRoles = new[]
    {
        NativeMethods.ROLE_SYSTEM_PUSHBUTTON, NativeMethods.ROLE_SYSTEM_MENUITEM,
        NativeMethods.ROLE_SYSTEM_MENUBAR, NativeMethods.ROLE_SYSTEM_MENUPOPUP,
        NativeMethods.ROLE_SYSTEM_TITLEBAR, NativeMethods.ROLE_SYSTEM_WINDOW
    };

    private static readonly string[] SystemControlNames = new[]
    {
        "close", "закрыть", "zamknij", "minimize", "maximize", "restore"
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
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_hookHandle == IntPtr.Zero)
        {
            Logger.Error("⚠ Failed to install WinEvent hook");
        }
        else
        {
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS)");
        }

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

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd == _keyboardWindowHandle) return;
        if (_isKeyboardVisible?.Invoke() == true)
        {
            Logger.Debug("⏸️ Keyboard already visible - skipping");
            return;
        }

        Logger.Debug("┌─────────────────────────────────────────────────");
        Logger.Debug($"🎯 FOCUS EVENT: HWND={hwnd:X}, idObject={idObject}, idChild={idChild}");

        try
        {
            var elementInfo = GetFocusedElementInfo(hwnd, idObject, idChild);

            if (elementInfo == null)
            {
                Logger.Debug("⚠ STEP 1 FAILED: Could not get element info");
                LogSeparator();
                return;
            }

            if (!elementInfo.IsTextInput)
            {
                Logger.Debug($"⚠ STEP 1 FAILED: Not a text input (Role={elementInfo.Role}, Class={elementInfo.ClassName})");
                LogSeparator();
                NonTextInputFocused?.Invoke(this, new FocusEventArgs
                {
                    WindowHandle = hwnd,
                    ControlType = elementInfo.Role,
                    ClassName = elementInfo.ClassName
                });
                return;
            }

            Logger.Info($"✅ STEP 1 PASSED: Element is text input");
            LogElementInfo(elementInfo);

            var inputSource = GetInputMessageSource();
            Logger.Debug($"🔥 INPUT SOURCE: DeviceType={inputSource.deviceType}, Origin={inputSource.originId}");

            bool shouldShowKeyboard = ShouldShowKeyboard(inputSource, elementInfo);

            if (shouldShowKeyboard)
            {
                Logger.Info("🎉 DECISION: SHOW KEYBOARD");
                LogSeparator();
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
                LogSeparator();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("⚠ Error in WinEventProc", ex);
            LogSeparator();
        }
    }

    private bool ShouldShowKeyboard(NativeMethods.INPUT_MESSAGE_SOURCE inputSource, ElementInfo elementInfo)
    {
        var origin = inputSource.originId;
        var device = inputSource.deviceType;

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

        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_INJECTED)
        {
            Logger.Debug("⚠ STEP 2 FAILED: Programmatic input (IMO_INJECTED)");
            return false;
        }

        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_SYSTEM)
        {
            Logger.Debug("⚠ STEP 2 FAILED: System-generated input (IMO_SYSTEM)");
            return false;
        }

        if (origin == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE)
        {
            Logger.Debug("⚠️ Input source UNAVAILABLE - performing additional validation");
            return ValidateWithPointerTracker(elementInfo);
        }

        Logger.Debug($"⚠ STEP 2 FAILED: Unknown origin ID: {origin}");
        return false;
    }

    private bool ValidateWithPointerTracker(ElementInfo elementInfo)
    {
        if (_pointerTracker == null)
        {
            Logger.Debug("   ⚠ Validation FAILED: No pointer tracker available");
            return false;
        }

        var lastClick = _pointerTracker.GetLastPointerClick();
        if (lastClick == null)
        {
            Logger.Debug("   ⚠ Validation FAILED: No recent pointer click recorded");
            return false;
        }

        LogLastClickInfo(lastClick);

        if (!lastClick.IsPointerInput)
        {
            Logger.Debug($"   ⚠ Validation FAILED: Last click was not pointer input");
            return false;
        }
        Logger.Debug("   ✅ Check 1 passed: Last click was pointer input");

        if (!_pointerTracker.IsLastInputPointerClick())
        {
            Logger.Debug("   ⚠ Validation FAILED: Pointer click is not the most recent input");
            return false;
        }
        Logger.Debug("   ✅ Check 2 passed: Pointer click is the most recent input");

        if (!AreWindowsRelated(lastClick.WindowHandle, elementInfo.WindowHandle))
        {
            Logger.Debug($"   ⚠ Validation FAILED: Click HWND ({lastClick.WindowHandle:X}) not related to focus HWND ({elementInfo.WindowHandle:X})");
            return false;
        }
        Logger.Debug($"   ✅ Check 3 passed: Click HWND related to focus HWND");

        if (lastClick.WindowHandle != elementInfo.WindowHandle)
        {
            bool isDirectRelation = IsParentOf(lastClick.WindowHandle, elementInfo.WindowHandle) ||
                                   IsParentOf(elementInfo.WindowHandle, lastClick.WindowHandle);

            if (!isDirectRelation)
            {
                Logger.Debug($"   ⚠ Validation FAILED: Click and focus windows are siblings/cousins (not direct parent/child)");
                Logger.Debug($"      This prevents false positives from dialogs/popups over text fields");
                return false;
            }

            Logger.Debug($"   ✅ Check 3.5 passed: Direct parent/child relationship confirmed");
        }

        // CRITICAL: Relax bounds check for web browsers (contenteditable often has incorrect bounds)
        bool isWebBrowser = IsWebBrowserClass(elementInfo.ClassName?.ToLowerInvariant() ?? "");
        
        if (!elementInfo.Bounds.IsEmpty && !elementInfo.Bounds.Contains(lastClick.Position))
        {
            if (isWebBrowser)
            {
                // For web browsers, use relaxed bounds check (allow clicks nearby)
                const int tolerance = 200; // pixels
                Rectangle expandedBounds = new Rectangle(
                    elementInfo.Bounds.X - tolerance,
                    elementInfo.Bounds.Y - tolerance,
                    elementInfo.Bounds.Width + (tolerance * 2),
                    elementInfo.Bounds.Height + (tolerance * 2)
                );

                if (expandedBounds.Contains(lastClick.Position))
                {
                    Logger.Debug($"   ⚠️ Click outside strict bounds but within tolerance for web browser");
                    Logger.Debug($"   ✅ Check 4 relaxed: Click within expanded bounds (tolerance: {tolerance}px)");
                }
                else
                {
                    Logger.Debug($"   ⚠ Validation FAILED: Click ({lastClick.Position.X}, {lastClick.Position.Y}) outside expanded bounds");
                    Logger.Debug($"      Element bounds: ({elementInfo.Bounds.X}, {elementInfo.Bounds.Y}, {elementInfo.Bounds.Width}x{elementInfo.Bounds.Height})");
                    return false;
                }
            }
            else
            {
                Logger.Debug($"   ⚠ Validation FAILED: Click ({lastClick.Position.X}, {lastClick.Position.Y}) outside element bounds");
                Logger.Debug($"      Element bounds: ({elementInfo.Bounds.X}, {elementInfo.Bounds.Y}, {elementInfo.Bounds.Width}x{elementInfo.Bounds.Height})");
                return false;
            }
        }
        else
        {
            Logger.Debug($"   ✅ Check 4 passed: Click inside element bounds");
        }

        Logger.Debug("   ✅ ALL VALIDATION CHECKS PASSED");
        return true;
    }

    private bool IsParentOf(IntPtr potentialParent, IntPtr potentialChild)
    {
        if (potentialParent == IntPtr.Zero || potentialChild == IntPtr.Zero)
            return false;

        IntPtr parent = potentialChild;
        const int maxDepth = 30;

        for (int i = 0; i < maxDepth; i++)
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

    private ElementInfo GetFocusedElementInfo(IntPtr hwnd, int idObject, int idChild)
    {
        var msaaInfo = TryGetElementInfoViaMSAA(hwnd, idObject, idChild);
        if (msaaInfo != null)
        {
            Logger.Debug("   📋 Element info retrieved via MSAA");
            return msaaInfo;
        }

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
                return BuildElementInfo(acc, childId, hwnd, "MSAA");
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
            string className = GetClassName(hwnd);
            if (string.IsNullOrEmpty(className) || IsBlacklistedClassName(className))
                return null;

            uint pid = GetProcessId(hwnd);
            if (IsBlacklistedProcess(pid))
                return null;

            string classLower = className.ToLowerInvariant();
            bool isTextInput = IsEditorClass(classLower) || classLower.Contains("edit");

            if (!isTextInput)
                return null;

            Rectangle bounds = GetWindowBounds(hwnd);

            return new ElementInfo
            {
                WindowHandle = hwnd,
                Role = 0,
                ClassName = className,
                Name = "",
                Bounds = bounds,
                IsPassword = false,
                ProcessId = pid,
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

    private ElementInfo BuildElementInfo(NativeMethods.IAccessible acc, object childId, IntPtr hwnd, string detectionMethod)
    {
        uint pid = GetProcessId(hwnd);
        if (IsBlacklistedProcess(pid))
        {
            Logger.Debug($"   🚫 Blacklisted process (PID: {pid})");
            return null;
        }

        string className = GetClassName(hwnd);
        if (IsBlacklistedClassName(className))
        {
            Logger.Debug($"   🚫 Blacklisted window class: {className}");
            return null;
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
            IsTextInput = isTextInput,
            DetectionMethod = detectionMethod
        };
    }

    private bool IsEditableTextInput(int role, string className, int state, NativeMethods.IAccessible acc, object childId)
    {
        bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
        bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
        bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

        if (isUnavailable) return false;

        string classLower = className?.ToLowerInvariant() ?? "";
        bool isWebBrowser = IsWebBrowserClass(classLower);

        if (role == NativeMethods.ROLE_SYSTEM_CARET)
        {
            Logger.Debug($"   ✅ CARET (insertion point) detected");
            return true;
        }

        if (IsEditorClass(classLower))
        {
            if (isReadonly)
            {
                Logger.Debug($"   ⚠ Editor class '{className}' but readonly");
                return false;
            }
            Logger.Debug($"   ✅ Recognized editor class: {className}");
            return true;
        }

        if (IsChromeRenderClass(classLower))
            return ValidateChromeRenderWidget(acc, childId, role, isFocusable);

        if (!isFocusable)
        {
            Logger.Debug($"   ⚠ Element is not focusable (Role: {role})");
            return false;
        }

        return ValidateByRole(role, state, className, acc, childId, isReadonly, isWebBrowser);
    }

    private bool ValidateChromeRenderWidget(NativeMethods.IAccessible acc, object childId, int role, bool isFocusable)
    {
        if (!isFocusable) return false;

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

        return false;
    }

    private bool ValidateByRole(int role, int state, string className, NativeMethods.IAccessible acc, object childId, bool isReadonly, bool isWebBrowser)
    {
        string classLower = className?.ToLowerInvariant() ?? "";

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
            return true;

        switch (role)
        {
            case NativeMethods.ROLE_SYSTEM_TEXT:
                return ValidateTextRole(acc, childId, isReadonly);

            case NativeMethods.ROLE_SYSTEM_DOCUMENT:
                return ValidateDocumentRole(isReadonly, isWebBrowser);

            case NativeMethods.ROLE_SYSTEM_CLIENT:
                return ValidateClientRole(acc, childId, state, isReadonly, isWebBrowser);

            case NativeMethods.ROLE_SYSTEM_PANE:
                return ValidatePaneRole(acc, childId, state, isReadonly, isWebBrowser);

            case NativeMethods.ROLE_SYSTEM_COMBOBOX:
                return ValidateComboboxRole(acc, childId, isReadonly);
        }

        return false;
    }

    private bool ValidateTextRole(NativeMethods.IAccessible acc, object childId, bool isReadonly)
    {
        if (isReadonly)
        {
            Logger.Debug($"   ⚠ ROLE_SYSTEM_TEXT but readonly");
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
            Logger.Debug($"   ⚠ ROLE_SYSTEM_TEXT without value interface");
            return false;
        }
    }

    private bool ValidateDocumentRole(bool isReadonly, bool isWebBrowser)
    {
        // CRITICAL: Web browsers often incorrectly report contenteditable as readonly
        // Trust focusable state over readonly flag for web content
        if (isWebBrowser)
        {
            Logger.Debug($"   ✅ DOCUMENT role in web browser (ignoring readonly flag, likely contenteditable)");
            return true;
        }

        if (!isReadonly)
        {
            Logger.Debug($"   ✅ DOCUMENT role (editable)");
            return true;
        }

        Logger.Debug($"   ⚠ DOCUMENT role but readonly");
        return false;
    }

    private bool ValidateClientRole(NativeMethods.IAccessible acc, object childId, int state, bool isReadonly, bool isWebBrowser)
    {
        if (isWebBrowser)
        {
            bool isFocused = (state & NativeMethods.STATE_SYSTEM_FOCUSED) != 0;
            
            if (isFocused || !isReadonly)
            {
                Logger.Debug($"   ✅ CLIENT role in web browser (focused={isFocused}, readonly={isReadonly})");
                return true;
            }
        }

        if (isReadonly)
        {
            Logger.Debug($"   ⚠ CLIENT role but readonly");
            return false;
        }

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

        Logger.Debug($"   ⚠ CLIENT role without value interface");
        return false;
    }

    private bool ValidatePaneRole(NativeMethods.IAccessible acc, object childId, int state, bool isReadonly, bool isWebBrowser)
    {
        // CRITICAL FIX: ChatGPT and other contenteditable use PANE role
        if (isWebBrowser)
        {
            // Web browsers: trust focusable + not explicitly unavailable
            bool isFocused = (state & NativeMethods.STATE_SYSTEM_FOCUSED) != 0;
            bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
            
            // Ignore readonly flag for web content (often incorrect for contenteditable)
            if (isFocused || isFocusable)
            {
                Logger.Debug($"   ✅ PANE role in web browser (focused={isFocused}, focusable={isFocusable}, ignoring readonly)");
                return true;
            }

            Logger.Debug($"   ⚠ PANE role in web browser but not focused/focusable");
            return false;
        }

        // Non-browser PANE validation
        if (isReadonly)
        {
            Logger.Debug($"   ⚠ PANE role but readonly");
            return false;
        }

        try
        {
            string value = acc.get_accValue(childId);
            if (value != null)
            {
                Logger.Debug($"   ✅ PANE role with value interface");
                return true;
            }
        }
        catch { }

        Logger.Debug($"   ⚠ PANE role without value interface");
        return false;
    }

    private bool ValidateComboboxRole(NativeMethods.IAccessible acc, object childId, bool isReadonly)
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

    private bool IsWebBrowserClass(string classLower)
    {
        if (string.IsNullOrEmpty(classLower)) return false;
        return WebBrowserClasses.Any(browser => classLower.Contains(browser));
    }

    private bool IsBlacklistedProcess(uint pid)
    {
        if (pid == 0) return false;

        try
        {
            IntPtr hProcess = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ,
                false, pid);

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
        catch { }

        return false;
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

            if (SystemControlRoles.Contains(role))
            {
                Logger.Debug($"   🚫 System control role detected: {role}");
                return true;
            }

            string name = "";
            try { name = acc.get_accName(childId); } catch { }

            if (!string.IsNullOrEmpty(name))
            {
                string nameLower = name.ToLowerInvariant();
                if (SystemControlNames.Any(ctrl => nameLower.Contains(ctrl)))
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

                    const int closeButtonAreaWidth = 150;
                    const int closeButtonAreaHeight = 50;
                    bool isTopRightCorner = relX > (windowWidth - closeButtonAreaWidth) && relY < closeButtonAreaHeight;

                    if (isTopRightCorner)
                    {
                        Logger.Debug($"   🚫 Click in top-right corner detected (close button area)");
                        Logger.Debug($"      Window: ({rect.Left}, {rect.Top}, {windowWidth}x{rect.Bottom - rect.Top})");
                        Logger.Debug($"      Relative click: ({relX}, {relY})");
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

        if (_isKeyboardVisible?.Invoke() == true)
        {
            Logger.Debug("⏸️ Direct click ignored: Keyboard already visible");
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

                Logger.Debug("┌─────────────────────────────────────────────────");
                Logger.Debug($"🔍 DIRECT CLICK CHECK: HWND={hwnd:X}");

                var elementInfo = BuildElementInfo(acc, childId, hwnd, "DirectClick");

                if (elementInfo != null && elementInfo.IsTextInput)
                {
                    Logger.Info($"✅ Direct click on text input - Role: {elementInfo.Role}, Class: {elementInfo.ClassName}");

                    if (!ValidateDirectClick(clickInfo, elementInfo))
                    {
                        Marshal.ReleaseComObject(acc);
                        LogSeparator();
                        return;
                    }

                    if (!elementInfo.Bounds.Contains(clickInfo.Position))
                    {
                        Logger.Debug($"⚠ Click outside element bounds");
                        Marshal.ReleaseComObject(acc);
                        LogSeparator();
                        return;
                    }

                    Logger.Info("🎉 DECISION: SHOW KEYBOARD (direct click on already-focused element)");
                    LogSeparator();

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
                    Logger.Debug($"⚠ Not a text input - Role: {elementInfo?.Role ?? 0}");
                    LogSeparator();
                }

                Marshal.ReleaseComObject(acc);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking object at hardware click point", ex);
        }
    }

    private bool ValidateDirectClick(PointerClickInfo clickInfo, ElementInfo elementInfo)
    {
        if (clickInfo.WindowHandle == elementInfo.WindowHandle)
            return true;

        Logger.Debug($"⚠️ Warning: Click HWND ({clickInfo.WindowHandle:X}) != Element HWND ({elementInfo.WindowHandle:X})");
        Logger.Debug($"   This might indicate a popup/dialog over the text field");

        bool isDirectRelation = IsParentOf(clickInfo.WindowHandle, elementInfo.WindowHandle) ||
                               IsParentOf(elementInfo.WindowHandle, clickInfo.WindowHandle);

        if (!isDirectRelation)
        {
            Logger.Debug($"⚠ Not direct parent/child - likely a dialog/popup over text field");
            return false;
        }

        Logger.Debug($"✅ Direct parent/child relationship confirmed");
        return true;
    }

    private string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        StringBuilder sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private uint GetProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    private Rectangle GetWindowBounds(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
        {
            return new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        return Rectangle.Empty;
    }

    private void LogElementInfo(ElementInfo info)
    {
        Logger.Debug($"   Role: {info.Role}");
        Logger.Debug($"   Class: {info.ClassName}");
        Logger.Debug($"   Name: {info.Name}");
        Logger.Debug($"   Bounds: ({info.Bounds.X}, {info.Bounds.Y}, {info.Bounds.Width}x{info.Bounds.Height})");
    }

    private void LogLastClickInfo(PointerClickInfo clickInfo)
    {
        Logger.Debug($"   🔍 Last pointer click: ({clickInfo.Position.X}, {clickInfo.Position.Y})");
        Logger.Debug($"      Device: {clickInfo.DeviceType}");
        Logger.Debug($"      HWND: {clickInfo.WindowHandle:X}");
        Logger.Debug($"      Time: {clickInfo.Timestamp:HH:mm:ss.fff}");
    }

    private void LogSeparator()
    {
        Logger.Debug("└─────────────────────────────────────────────────");
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