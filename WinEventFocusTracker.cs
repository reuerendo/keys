// CHANGES to WinEventFocusTracker.cs:

// 1. UPDATE the WinEventProc method - remove strict bounds checking for Chrome:

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
            // NEW: For Chrome/Edge, bounds checking is unreliable due to multi-process architecture
            // Skip strict bounds check for Chrome render widgets
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
                        Logger.Debug($"Focus event: Click outside bounds. Ignoring. Bounds: ({l}, {t}, {w}x{h})");
                        Marshal.ReleaseComObject(acc);
                        return;
                    }
                    
                    Logger.Debug($"✅ Focus event: Click inside bounds ({l}, {t}, {w}x{h})");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Could not verify bounds: {ex.Message}");
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

// 2. ADD new helper method to detect Chrome windows:

/// <summary>
/// Check if window is Chrome/Edge based on class name
/// </summary>
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

// 3. UPDATE IsEditableTextInput to better handle Chrome:

private bool IsEditableTextInput(int role, string className, int state, NativeMethods.IAccessible acc, object childId)
{
    bool isReadonly = (state & NativeMethods.STATE_SYSTEM_READONLY) != 0;
    bool isFocusable = (state & NativeMethods.STATE_SYSTEM_FOCUSABLE) != 0;
    bool isUnavailable = (state & NativeMethods.STATE_SYSTEM_UNAVAILABLE) != 0;

    if (isUnavailable) return false;

    string classLower = className?.ToLowerInvariant() ?? "";

    // Known text editors - always accept if not readonly
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

    // Chrome/Electron widgets - ENHANCED DETECTION
    if (IsChromeRenderClass(classLower))
    {
        Logger.Debug($"Detected Chrome render class: {className}, Role: {role}, Focusable: {isFocusable}");
        
        if (isFocusable)
        {
            // Try value interface
            try
            {
                string value = acc.get_accValue(childId);
                Logger.Debug($"✅ Chrome widget has value interface - accepting");
                return true;
            }
            catch { }

            // Check for input-related roles
            const int ROLE_SYSTEM_PANE = 0x10;
            if (role == ROLE_SYSTEM_PANE || 
                role == NativeMethods.ROLE_SYSTEM_CLIENT ||
                role == NativeMethods.ROLE_SYSTEM_TEXT ||
                role == NativeMethods.ROLE_SYSTEM_DOCUMENT)
            {
                Logger.Debug($"✅ Chrome widget with input role ({role}) - accepting");
                return true;
            }
            
            // For Chrome, even generic focusable elements might be inputs
            // Check if it has reasonable size (not a tiny icon)
            try
            {
                acc.accLocation(out int l, out int t, out int w, out int h, childId);
                if (w > 50 && h > 20)  // Reasonable input field size
                {
                    Logger.Debug($"✅ Chrome focusable element with reasonable size ({w}x{h}) - accepting");
                    return true;
                }
            }
            catch { }
        }
        
        Logger.Debug($"Chrome render widget but not detected as input");
        return false;
    }

    // Standard checks for non-Chrome windows
    if (!isFocusable)
    {
        Logger.Debug($"Element not focusable (Role: {role})");
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
    if (classLower.Contains("console") || classLower.Contains("terminal"))
    {
        return true;
    }

    // ROLE_SYSTEM_TEXT
    if (role == NativeMethods.ROLE_SYSTEM_TEXT)
    {
        if (isReadonly) return false;

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

    // Document role
    if (role == NativeMethods.ROLE_SYSTEM_DOCUMENT && !isReadonly)
    {
        return true;
    }

    // CLIENT role
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

    // COMBOBOX
    const int ROLE_SYSTEM_COMBOBOX = 0x2E;
    if (role == ROLE_SYSTEM_COMBOBOX)
    {
        Logger.Debug($"COMBOBOX detected");
        
        try
        {
            string value = acc.get_accValue(childId);
            Logger.Debug($"COMBOBOX has value interface - accepting");
            return true;
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