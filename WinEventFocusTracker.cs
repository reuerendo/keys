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
/// Supports Surface touch/pen input which is injected by drivers but detected as IMO_HARDWARE.
/// ENHANCED: Validates that click coordinates are within the focused element's bounds.
/// </summary>
public class WinEventFocusTracker : IDisposable
{
    // Структура для хранения информации о последнем клике
    private class LastClickInfo
    {
        public Point Coordinates { get; set; }
        public IntPtr WindowHandle { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsValid { get; set; }
    }

    private readonly IntPtr _keyboardWindowHandle;
    private readonly MouseClickDetector _clickDetector;
    private NativeMethods.WinEventDelegate _winEventProc;
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _requireClickForAutoShow;
    private bool _isDisposed = false;
    
    // Информация о последнем клике (без таймаута!)
    private LastClickInfo _lastClick = new LastClickInfo { IsValid = false };
    private readonly object _clickLock = new object();
    
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
        "shelldll_defview",
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
            Logger.Info("✅ WinEvent hook installed (EVENT_OBJECT_FOCUS with coordinate validation)");
        }

        // Subscribe to hardware click detector
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
            // Verify focus change was caused by hardware input
            if (_requireClickForAutoShow)
            {
                if (!IsHardwareInputCausedFocus())
                {
                    Logger.Debug("❌ Focus event: Not caused by hardware input - ignoring");
                    return;
                }
                
                Logger.Debug("✅ Focus event: Hardware input source confirmed");
            }

            // Get the IAccessible object from the event
            int hr = NativeMethods.AccessibleObjectFromEvent(hwnd, idObject, idChild, out NativeMethods.IAccessible acc, out object childId);
            
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
    /// Check if the current focus change was caused by hardware input.
    /// </summary>
    private bool IsHardwareInputCausedFocus()
    {
        try
        {
            bool success = NativeMethods.GetCurrentInputMessageSource(out NativeMethods.INPUT_MESSAGE_SOURCE source);
            
            if (!success)
            {
                Logger.Debug("⚠️ GetCurrentInputMessageSource API failed - accepting (fallback)");
                return true;
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
            
            // FALLBACK: Source unavailable
            if (originId == NativeMethods.INPUT_MESSAGE_ORIGIN_ID.IMO_UNAVAILABLE)
            {
                Logger.Debug("⚠️ Source UNAVAILABLE - accepting (hardware assumed)");
                return true;
            }

            Logger.Debug($"🚫 Unknown origin ID {originId} - rejecting");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Error in IsHardwareInputCausedFocus", ex);
            return true;
        }
    }

    /// <summary>
    /// Handles direct clicks to detect text fields that might ALREADY have focus
    /// Saves click info for later validation
    /// </summary>
    private void OnHardwareClickDetected(object sender, Point clickPoint)
    {
        if (_isDisposed || !_requireClickForAutoShow) return;

        // Save click information WITHOUT timeout - just store the latest click
        lock (_clickLock)
        {
            _lastClick.Coordinates = clickPoint;
            _lastClick.Timestamp = DateTime.UtcNow;
            _lastClick.IsValid = true;
            _lastClick.WindowHandle = IntPtr.Zero; // Will be set when we get the window
        }

        Logger.Debug($"🖱️ Hardware click saved: ({clickPoint.X}, {clickPoint.Y})");

        // Skip if keyboard is visible
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
                        
                        // Update click info with window handle
                        lock (_clickLock)
                        {
                            _lastClick.WindowHandle = hwnd;
                        }
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
    /// Core logic to determine if the object is a text input
    /// ENHANCED: Validates click coordinates against element bounds
    /// </summary>
// Fix in ProcessAccessibleObject method

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
				// ✅ FIXED: Validate coordinates for BOTH direct clicks AND focus events
				if (_requireClickForAutoShow)
				{
					if (!ValidateClickCoordinates(acc, childId, hwnd))
					{
						Logger.Info($"❌ Text input {(isDirectClick ? "clicked" : "focused")} but click was OUTSIDE element bounds - ignoring auto-show");
						return;
					}
				}
				
				string name = "";
				try { name = acc.get_accName(childId); } catch { }

				if (name != null && name.Length > 100)
				{
					name = name.Substring(0, 100) + "...";
				}

				Logger.Info($"{(isDirectClick ? "🖱️ Click" : "⚡ Focus")} on EDITABLE Text Input - Role: {role}, Class: {className}, Name: {name}");

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
    /// ✨ NEW: Validates that the last click was inside the element's bounds and in the same window
    /// NO TIMEOUT - uses the most recent valid click info
    /// </summary>
    private bool ValidateClickCoordinates(NativeMethods.IAccessible acc, object childId, IntPtr hwnd)
    {
        lock (_clickLock)
        {
            // Check if we have valid click info
            if (!_lastClick.IsValid)
            {
                Logger.Debug("⚠️ No valid click info available - accepting focus (fallback)");
                return true; // Fallback to accepting if no click info
            }

            // CRITICAL: Validate window handle matches
            if (_lastClick.WindowHandle != IntPtr.Zero && _lastClick.WindowHandle != hwnd)
            {
                Logger.Info($"❌ Click window (0x{_lastClick.WindowHandle:X}) != Focus window (0x{hwnd:X}) - REJECTING");
                return false;
            }

            try
            {
                // Get element bounds using IAccessible.accLocation
                acc.accLocation(out int left, out int top, out int width, out int height, childId);

                // Calculate bounds
                Rectangle bounds = new Rectangle(left, top, width, height);

                // Check if click point is inside bounds
                bool isInside = bounds.Contains(_lastClick.Coordinates);

                if (isInside)
                {
                    Logger.Info($"✅ Click ({_lastClick.Coordinates.X}, {_lastClick.Coordinates.Y}) IS INSIDE bounds [{left}, {top}, {width}, {height}]");
                }
                else
                {
                    Logger.Info($"❌ Click ({_lastClick.Coordinates.X}, {_lastClick.Coordinates.Y}) is OUTSIDE bounds [{left}, {top}, {width}, {height}]");
                }

                return isInside;
            }
            catch (Exception ex)
            {
                Logger.Debug($"⚠️ Could not get element bounds: {ex.Message} - accepting (fallback)");
                return true; // Fallback to accepting if we can't get bounds
            }
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

        // ROLE_SYSTEM_CARET - insertion point/cursor
        const int ROLE_SYSTEM_CARET = 0x7;
        if (role == ROLE_SYSTEM_CARET)
        {
            Logger.Debug($"✅ CARET (insertion point) detected");
            return true;
        }

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
                    Logger.Debug($"✅ Chrome render widget has value interface");
                    return true;
                }
                catch { }

                const int ROLE_SYSTEM_PANE = 0x10;
                if (role == ROLE_SYSTEM_PANE || role == NativeMethods.ROLE_SYSTEM_CLIENT)
                {
                    Logger.Debug($"✅ Chrome render widget with focusable pane/client");
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

        // ROLE_SYSTEM_TEXT
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
                Logger.Debug($"ROLE_SYSTEM_TEXT with value interface");
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

        // CLIENT Role
        if (role == NativeMethods.ROLE_SYSTEM_CLIENT)
        {
            if (isReadonly) return false;

            try
            {
                string name = acc.get_accName(childId);
                if (!string.IsNullOrEmpty(name) && name.Length > 20)
                {
                    Logger.Debug($"✅ CLIENT role with text content (length: {name.Length})");
                    return true;
                }
            }
            catch { }

            try
            {
                string value = acc.get_accValue(childId);
                if (value != null)
                {
                    Logger.Debug($"✅ CLIENT role with value interface");
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
                Logger.Debug($"COMBOBOX has value interface");
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
                                        Logger.Debug($"✅ COMBOBOX has editable text child");
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
                Logger.Debug($"COMBOBOX is focusable and not readonly");
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