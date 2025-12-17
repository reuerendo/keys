using System;
using System.Runtime.InteropServices;
using System.Drawing;
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

    public event EventHandler<TextInputFocusEventArgs> TextInputFocused;
    public event EventHandler<FocusEventArgs> NonTextInputFocused;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="keyboardWindowHandle">Handle of the virtual keyboard window</param>
    /// <param name="requireClickForAutoShow">If true, auto-show only triggers on actual mouse clicks</param>
    public UIAutomationFocusTracker(IntPtr keyboardWindowHandle, bool requireClickForAutoShow = true)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _requireClickForAutoShow = requireClickForAutoShow;
        
        try
        {
            Logger.Info("Initializing UI Automation focus tracker with FlaUI...");
            
            // Initialize mouse click detector if click filtering is enabled
            if (_requireClickForAutoShow)
            {
                _clickDetector = new MouseClickDetector();
                Logger.Info("🖱️ Click detection enabled - auto-show only on user clicks");
            }
            else
            {
                Logger.Info("⚠️ Click detection disabled - auto-show on any focus change");
            }
            
            // Create FlaUI UIA3 automation instance
            _automation = new UIA3Automation();
            
            if (_automation == null)
            {
                Logger.Error("Failed to create UIA3Automation instance");
                return;
            }

            Logger.Info("✅ FlaUI UIA3Automation instance created successfully");

            // Register focus changed event with lambda
            _focusHandler = _automation.RegisterFocusChangedEvent(element =>
            {
                OnFocusChanged(element);
            });
            
            _isInitialized = true;
            Logger.Info("✅ UI Automation focus tracker initialized successfully with FlaUI");
        }
        catch (COMException comEx)
        {
            Logger.Error($"COM error initializing FlaUI: 0x{comEx.HResult:X8} - {comEx.Message}", comEx);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation focus tracker", ex);
            _isInitialized = false;
        }
    }

    /// <summary>
    /// Handle focus changed event with click detection
    /// </summary>
    private void OnFocusChanged(AutomationElement sender)
    {
        if (sender == null || !_isInitialized)
            return;

        lock (_lockObject)
        {
            try
            {
                // Get window handle
                var hwnd = sender.Properties.NativeWindowHandle.ValueOrDefault;
                
                // Ignore keyboard's own window
                if (hwnd == _keyboardWindowHandle)
                {
                    Logger.Debug("Focus on keyboard itself - ignoring");
                    return;
                }

                // Get properties
                var controlType = sender.Properties.ControlType.ValueOrDefault;
                string className = sender.Properties.ClassName.ValueOrDefault ?? "";
                string name = sender.Properties.Name.ValueOrDefault ?? "";
                bool isPassword = sender.Properties.IsPassword.ValueOrDefault;
                bool isKeyboardFocusable = sender.Properties.IsKeyboardFocusable.ValueOrDefault;
                
                uint processId = 0;
                try
                {
                    processId = (uint)sender.Properties.ProcessId.ValueOrDefault;
                }
                catch
                {
                    if (hwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(hwnd, out processId);
                    }
                }

                int controlTypeId = (int)controlType;
                Logger.Debug($"Focus changed - Type: {controlTypeId}, Class: '{className}', Name: '{name}', Password: {isPassword}, Focusable: {isKeyboardFocusable}, PID: {processId}");

                // Check if this is a text input control
                if (IsTextInputControl(controlType, className, isKeyboardFocusable))
                {
                    // If click detection is enabled, verify this was a user click
                    bool shouldTrigger = true;
                    
                    if (_requireClickForAutoShow && _clickDetector != null)
                    {
                        shouldTrigger = IsClickInitiatedFocus(sender);
                    }
                    
                    if (shouldTrigger)
                    {
                        Logger.Info($"✅ Text input focused (user click) - Type: {controlType}, Class: '{className}', Name: '{name}', Password: {isPassword}");
                        
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
                        Logger.Debug($"⏭️ Text input focused (programmatic) - IGNORED - Type: {controlType}, Class: '{className}'");
                    }
                }
                else
                {
                    Logger.Debug($"Non-text input focused - Type: {controlType}");
                    
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

    /// <summary>
    /// Check if focus change was initiated by a user click
    /// </summary>
    private bool IsClickInitiatedFocus(AutomationElement element)
    {
        try
        {
            // Check if there was a recent click
            if (!_clickDetector.WasRecentClick())
            {
                Logger.Debug("❌ No recent click detected - focus change is programmatic");
                return false;
            }

            // Get element bounds to verify click was inside
            var boundingRectangle = element.Properties.BoundingRectangle.ValueOrDefault;
            
            if (boundingRectangle.IsEmpty)
            {
                Logger.Warning("⚠️ Could not get element bounds - allowing auto-show");
                return true; // If we can't get bounds, allow it (safer)
            }

            // Convert to Rectangle for easier checking
            var bounds = new Rectangle(
                (int)boundingRectangle.X,
                (int)boundingRectangle.Y,
                (int)boundingRectangle.Width,
                (int)boundingRectangle.Height
            );

            // Check if click was inside element bounds
            bool wasClickInside = _clickDetector.WasRecentClickInBounds(bounds);
            
            if (wasClickInside)
            {
                Logger.Info("✅ Click detected inside text input - triggering auto-show");
            }
            else
            {
                Logger.Debug("❌ Click was outside element bounds - ignoring focus change");
            }

            return wasClickInside;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking click-initiated focus", ex);
            // On error, allow auto-show (safer to show when unsure)
            return true;
        }
    }

    /// <summary>
    /// Check if control is a text input control
    /// </summary>
    private bool IsTextInputControl(ControlType controlType, string className, bool isKeyboardFocusable)
    {
        if (!isKeyboardFocusable)
            return false;

        // Check FlaUI control types
        if (controlType == ControlType.Edit || controlType == ControlType.Document)
            return true;

        if (!string.IsNullOrEmpty(className))
        {
            string classLower = className.ToLowerInvariant();
            
            if (classLower.Contains("edit") ||
                classLower.Contains("textbox") ||
                classLower.Contains("richedit") ||
                classLower.Contains("scintilla") ||
                classLower == "chrome_rendererwidgethosthwnd" ||
                classLower == "intermediate d3d window")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Enable or disable click requirement for auto-show
    /// </summary>
    public void SetClickRequirement(bool requireClick)
    {
        lock (_lockObject)
        {
            if (_requireClickForAutoShow == requireClick)
                return;

            _requireClickForAutoShow = requireClick;

            if (requireClick && _clickDetector == null)
            {
                _clickDetector = new MouseClickDetector();
                Logger.Info("🖱️ Click detection enabled");
            }
            else if (!requireClick && _clickDetector != null)
            {
                _clickDetector.Dispose();
                _clickDetector = null;
                Logger.Info("⚠️ Click detection disabled");
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            if (_isInitialized && _focusHandler != null)
            {
                try
                {
                    // Dispose automatically unregisters the handler
                    _focusHandler.Dispose();
                    Logger.Info("Focus changed event handler removed");
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to remove focus changed event handler", ex);
                }
            }

            _clickDetector?.Dispose();
            _clickDetector = null;

            _automation?.Dispose();
            _automation = null;
            _focusHandler = null;
            _isInitialized = false;

            Logger.Info("UI Automation focus tracker disposed");
        }
        catch (Exception ex)
        {
            Logger.Error("Error disposing UI Automation focus tracker", ex);
        }

        GC.SuppressFinalize(this);
    }

    ~UIAutomationFocusTracker()
    {
        Dispose();
    }
}

#region Event Args Classes

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

#endregion