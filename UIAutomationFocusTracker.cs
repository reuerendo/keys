using System;
using System.Runtime.InteropServices;
using System.Drawing;
using System.Threading.Tasks;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace VirtualKeyboard;

/// <summary>
/// Tracks focus changes using FlaUI library (wrapper over UI Automation)
/// Now with mouse click detection to distinguish user clicks from programmatic focus changes
/// </summary>
public class UIAutomationFocusTracker : IDisposable
{
    #region P/Invoke

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #endregion

    private UIA3Automation _automation;
    private IDisposable _focusHandler;
    private readonly IntPtr _keyboardWindowHandle;
    private readonly object _lockObject = new object();
    private bool _isDisposed = false;
    private bool _isInitialized = false;
    
    // Mouse click detector for filtering programmatic focus changes
    private MouseClickDetector _clickDetector;
    private bool _requireClickForAutoShow;
    
    // Flag to track if keyboard is visible (injected from outside)
    private Func<bool> _isKeyboardVisible;
    
    // Debounce for click checks
    private DateTime _lastClickCheckTime = DateTime.MinValue;
    private const int CLICK_CHECK_DEBOUNCE_MS = 200;

    public event EventHandler<TextInputFocusEventArgs> TextInputFocused;
    public event EventHandler<FocusEventArgs> NonTextInputFocused;

    public UIAutomationFocusTracker(IntPtr keyboardWindowHandle, bool requireClickForAutoShow = true)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _requireClickForAutoShow = requireClickForAutoShow;
        
        try
        {
            Logger.Info("Initializing UI Automation focus tracker with FlaUI...");
            
            if (_requireClickForAutoShow)
            {
                _clickDetector = new MouseClickDetector();
                // Subscribe to click events to check focused element on every click
                _clickDetector.ClickDetected += OnClickDetected;
                Logger.Info("🖱️ Click detection enabled - auto-show only on user clicks");
            }
            else
            {
                Logger.Info("⚠️ Click detection disabled - auto-show on any focus change");
            }
            
            _automation = new UIA3Automation();
            
            if (_automation == null)
            {
                Logger.Error("Failed to create UIA3Automation instance");
                return;
            }

            _focusHandler = _automation.RegisterFocusChangedEvent(element =>
            {
                OnFocusChanged(element);
            });
            
            _isInitialized = true;
            Logger.Info("✅ UI Automation focus tracker initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation focus tracker", ex);
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Set keyboard visibility checker (to avoid unnecessary checks)
    /// </summary>
    public void SetKeyboardVisibilityChecker(Func<bool> isVisible)
    {
        _isKeyboardVisible = isVisible;
    }

    /// <summary>
    /// Handle mouse click - check if clicked element is a text input
    /// This handles the case when user clicks on an already-focused text field
    /// IMPORTANT: Must be fast! UI Automation calls are async to not block mouse hook
    /// </summary>
    private void OnClickDetected(object sender, Point clickPosition)
    {
        if (!_isInitialized || !_requireClickForAutoShow)
            return;

        // Fast check: if keyboard is already visible, skip
        if (_isKeyboardVisible != null && _isKeyboardVisible())
        {
            return;
        }

        // Debounce: avoid multiple rapid checks
        var now = DateTime.UtcNow;
        lock (_lockObject)
        {
            var timeSinceLastCheck = (now - _lastClickCheckTime).TotalMilliseconds;
            if (timeSinceLastCheck < CLICK_CHECK_DEBOUNCE_MS)
            {
                return;
            }
            _lastClickCheckTime = now;
        }

        // Run check asynchronously to not block mouse hook
        Task.Run(() => CheckFocusedElementAsync(clickPosition));
    }

    /// <summary>
    /// Async check of focused element (doesn't block mouse hook)
    /// </summary>
    private async Task CheckFocusedElementAsync(Point clickPosition)
    {
        try
        {
            // Small delay to let focus stabilize
            await Task.Delay(10);

            // Double-check keyboard visibility after delay
            if (_isKeyboardVisible != null && _isKeyboardVisible())
            {
                return;
            }

            AutomationElement focusedElement = null;
            
            // Try to get focused element with timeout
            try
            {
                focusedElement = await Task.Run(() => _automation.FocusedElement());
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get focused element: {ex.Message}");
                return;
            }
            
            if (focusedElement == null)
                return;

            var hwnd = focusedElement.Properties.NativeWindowHandle.ValueOrDefault;
            
            // Ignore clicks on keyboard window itself
            if (hwnd == _keyboardWindowHandle)
                return;

            var controlType = focusedElement.Properties.ControlType.ValueOrDefault;
            string className = focusedElement.Properties.ClassName.ValueOrDefault ?? "";
            bool isKeyboardFocusable = focusedElement.Properties.IsKeyboardFocusable.ValueOrDefault;

            // Check if the focused element is a text input
            if (IsTextInputControl(controlType, className, isKeyboardFocusable))
            {
                // Check if click was inside the element bounds
                var boundingRectangle = focusedElement.Properties.BoundingRectangle.ValueOrDefault;
                
                if (!boundingRectangle.IsEmpty)
                {
                    var bounds = new Rectangle(
                        (int)boundingRectangle.X,
                        (int)boundingRectangle.Y,
                        (int)boundingRectangle.Width,
                        (int)boundingRectangle.Height
                    );

                    if (bounds.Contains(clickPosition))
                    {
                        Logger.Info($"🎯 Click detected in already-focused text input - Type: {controlType}, Class: '{className}'");
                        
                        // Get additional properties
                        string name = focusedElement.Properties.Name.ValueOrDefault ?? "";
                        bool isPassword = false;
                        try { isPassword = focusedElement.Properties.IsPassword.ValueOrDefault; } catch {}

                        uint processId = 0;
                        if (hwnd != IntPtr.Zero)
                            GetWindowThreadProcessId(hwnd, out processId);

                        int controlTypeId = (int)controlType;

                        // Fire the TextInputFocused event
                        var args = new TextInputFocusEventArgs
                        {
                            WindowHandle = hwnd,
                            ControlType = controlTypeId,
                            ClassName = className,
                            Name = name,
                            IsPassword = isPassword,
                            ProcessId = processId
                        };
                        
                        TextInputFocused?.Invoke(this, args);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in async focus check: {ex.Message}");
        }
    }

    private void OnFocusChanged(AutomationElement sender)
    {
        if (sender == null || !_isInitialized)
            return;

        lock (_lockObject)
        {
            try
            {
                var hwnd = sender.Properties.NativeWindowHandle.ValueOrDefault;
                
                if (hwnd == _keyboardWindowHandle)
                    return;

                var controlType = sender.Properties.ControlType.ValueOrDefault;
                string className = sender.Properties.ClassName.ValueOrDefault ?? "";
                string name = sender.Properties.Name.ValueOrDefault ?? "";
                
                bool isKeyboardFocusable = sender.Properties.IsKeyboardFocusable.ValueOrDefault;
                
                uint processId = 0;
                if (hwnd != IntPtr.Zero)
                    GetWindowThreadProcessId(hwnd, out processId);

                int controlTypeId = (int)controlType;

                if (IsTextInputControl(controlType, className, isKeyboardFocusable))
                {
                    bool shouldTrigger = true;
                    
                    if (_requireClickForAutoShow && _clickDetector != null)
                    {
                        shouldTrigger = IsClickInitiatedFocus(sender);
                    }
                    
                    if (shouldTrigger)
                    {
                        // Check IsPassword only if we decided to show
                        bool isPassword = false;
                        try { isPassword = sender.Properties.IsPassword.ValueOrDefault; } catch {}

                        Logger.Info($"✅ Text input focused (user click) - Type: {controlType}, Class: '{className}', Name: '{name}'");
                        
                        var args = new TextInputFocusEventArgs
                        {
                            WindowHandle = hwnd,
                            ControlType = controlTypeId,
                            ClassName = className,
                            Name = name,
                            IsPassword = isPassword,
                            ProcessId = processId
                        };
                        
                        TextInputFocused?.Invoke(this, args);
                    }
                    else
                    {
                        Logger.Debug($"⭐️ Text input focused (programmatic/out-of-bounds) - IGNORED - Type: {controlType}, Class: '{className}'");
                    }
                }
                else
                {
                    var args = new FocusEventArgs
                    {
                        WindowHandle = hwnd,
                        ControlType = controlTypeId,
                        ClassName = className
                    };
                    
                    NonTextInputFocused?.Invoke(this, args);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error handling focus changed event", ex);
            }
        }
    }

    private bool IsClickInitiatedFocus(AutomationElement element)
    {
        try
        {
            if (!_clickDetector.WasRecentClick())
            {
                Logger.Debug("❌ No recent click detected");
                return false;
            }

            var boundingRectangle = element.Properties.BoundingRectangle.ValueOrDefault;
            if (boundingRectangle.IsEmpty) return true; 

            var bounds = new Rectangle(
                (int)boundingRectangle.X,
                (int)boundingRectangle.Y,
                (int)boundingRectangle.Width,
                (int)boundingRectangle.Height
            );

            bool wasClickInside = _clickDetector.WasRecentClickInBounds(bounds);
            
            if (wasClickInside)
                Logger.Debug($"✅ Click detected INSIDE bounds ({bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height})");
            else
                Logger.Debug($"❌ Click detected OUTSIDE bounds ({bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height})");

            return wasClickInside;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking click-initiated focus", ex);
            return true;
        }
    }

    /// <summary>
    /// Improved logic for determining text input controls
    /// </summary>
    private bool IsTextInputControl(ControlType controlType, string className, bool isKeyboardFocusable)
    {
        if (!isKeyboardFocusable)
            return false;

        // 1. Standard text input fields (TextBox, Edit)
        if (controlType == ControlType.Edit)
            return true;

        string classLower = className?.ToLowerInvariant() ?? "";

        // 2. ControlType.Document (Browsers vs Word)
        if (controlType == ControlType.Document)
        {
            // Empty class usually means browser web content
            if (string.IsNullOrEmpty(classLower))
                return false;

            // Browser-related classes - ignore Document
            if (classLower.Contains("chrome") || 
                classLower.Contains("mozilla") || 
                classLower.Contains("edge") ||
                classLower.Contains("webkit"))
            {
                return false;
            }

            // For others (e.g. Word) - allow
            return true;
        }

        // 3. Specific window classes that behave as text fields
        if (classLower.Contains("edit") ||
            classLower.Contains("richedit") ||
            classLower.Contains("scintilla") ||
            classLower.Contains("cmd") ||
            classLower == "consolewindowclass")
        {
            return true;
        }

        return false;
    }

    public void SetClickRequirement(bool requireClick)
    {
        lock (_lockObject)
        {
            if (_requireClickForAutoShow == requireClick) return;
            _requireClickForAutoShow = requireClick;

            if (requireClick && _clickDetector == null)
            {
                _clickDetector = new MouseClickDetector();
                _clickDetector.ClickDetected += OnClickDetected;
                Logger.Info("Click detection enabled");
            }
            else if (!requireClick && _clickDetector != null)
            {
                _clickDetector.ClickDetected -= OnClickDetected;
                _clickDetector.Dispose();
                _clickDetector = null;
                Logger.Info("Click detection disabled");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_clickDetector != null)
            {
                _clickDetector.ClickDetected -= OnClickDetected;
                _clickDetector.Dispose();
            }
            
            if (_isInitialized && _focusHandler != null) 
                _focusHandler.Dispose();
            
            _automation?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing UI Automation focus tracker", ex);
        }
        GC.SuppressFinalize(this);
    }

    ~UIAutomationFocusTracker() => Dispose();
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