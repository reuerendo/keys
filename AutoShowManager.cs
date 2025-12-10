using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard display when text input fields receive focus
/// Uses UI Automation to detect focus changes similar to Windows TabTip.exe
/// </summary>
public class AutoShowManager : IDisposable
{
    private readonly IntPtr _keyboardWindowHandle;
    private AutomationFocusChangedEventHandler _focusChangedHandler;
    private bool _isEnabled;
    private bool _disposed;
    
    // Delay to prevent immediate re-showing if keyboard was just hidden
    private DateTime _lastHideTime = DateTime.MinValue;
    private const int HIDE_COOLDOWN_MS = 500;

    // P/Invoke for window operations
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    public event EventHandler ShowKeyboardRequested;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled)
                {
                    StartMonitoring();
                }
                else
                {
                    StopMonitoring();
                }
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        Logger.Info("AutoShowManager created");
    }

    /// <summary>
    /// Start monitoring focus changes via UI Automation
    /// </summary>
    private void StartMonitoring()
    {
        if (_focusChangedHandler != null)
        {
            Logger.Warning("Focus monitoring already active");
            return;
        }

        try
        {
            _focusChangedHandler = new AutomationFocusChangedEventHandler(OnFocusChanged);
            Automation.AddAutomationFocusChangedEventHandler(_focusChangedHandler);
            
            Logger.Info("UI Automation focus monitoring started");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start UI Automation monitoring", ex);
        }
    }

    /// <summary>
    /// Stop monitoring focus changes
    /// </summary>
    private void StopMonitoring()
    {
        if (_focusChangedHandler != null)
        {
            try
            {
                Automation.RemoveAutomationFocusChangedEventHandler(_focusChangedHandler);
                _focusChangedHandler = null;
                
                Logger.Info("UI Automation focus monitoring stopped");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to stop UI Automation monitoring", ex);
            }
        }
    }

    /// <summary>
    /// Called when focus changes to any UI element
    /// </summary>
    private void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        if (!_isEnabled || _disposed)
            return;

        try
        {
            // Check cooldown period
            if ((DateTime.Now - _lastHideTime).TotalMilliseconds < HIDE_COOLDOWN_MS)
            {
                return;
            }

            AutomationElement focusedElement = sender as AutomationElement;
            if (focusedElement == null)
            {
                // Try to get focused element directly
                try
                {
                    focusedElement = AutomationElement.FocusedElement;
                }
                catch
                {
                    return;
                }
            }

            if (focusedElement == null)
                return;

            // Check if keyboard itself has focus - don't show if so
            if (IsKeyboardWindowFocused())
            {
                Logger.Debug("Keyboard has focus, ignoring focus change");
                return;
            }

            // Check if the focused element is a text input control
            if (IsTextInputElement(focusedElement))
            {
                // Get process info for logging
                int processId = focusedElement.Current.ProcessId;
                string controlType = focusedElement.Current.ControlType.ProgrammaticName;
                string name = focusedElement.Current.Name;
                string automationId = focusedElement.Current.AutomationId;
                
                Logger.Info($"Text input focused: Process={processId}, Type={controlType}, Name='{name}', ID='{automationId}'");
                
                // Raise event to show keyboard
                ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (ElementNotAvailableException)
        {
            // Element was removed before we could query it - ignore
            Logger.Debug("Element not available during focus check");
        }
        catch (Exception ex)
        {
            Logger.Error("Error in OnFocusChanged handler", ex);
        }
    }

    /// <summary>
    /// Check if the element is a text input control
    /// </summary>
    private bool IsTextInputElement(AutomationElement element)
    {
        try
        {
            var controlType = element.Current.ControlType;
            
            // Check for Edit controls (TextBox, etc.)
            if (controlType == ControlType.Edit)
            {
                // Check if it's read-only
                bool isReadOnly = false;
                try
                {
                    if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
                    {
                        isReadOnly = ((ValuePattern)valuePattern).Current.IsReadOnly;
                    }
                }
                catch
                {
                    // If we can't determine, assume it's editable
                }

                if (isReadOnly)
                {
                    Logger.Debug("Edit control is read-only, ignoring");
                    return false;
                }

                return true;
            }

            // Check for Document controls (rich text editors, browsers)
            if (controlType == ControlType.Document)
            {
                // Check if it supports text editing
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object valuePattern))
                {
                    bool isReadOnly = ((ValuePattern)valuePattern).Current.IsReadOnly;
                    if (!isReadOnly)
                    {
                        return true;
                    }
                }

                // Check for TextPattern support (indicates text editing capability)
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern))
                {
                    return true;
                }
            }

            // Check for ComboBox with editable text field
            if (controlType == ControlType.ComboBox)
            {
                // Check if combo box is editable
                try
                {
                    var walker = TreeWalker.ControlViewWalker;
                    var child = walker.GetFirstChild(element);
                    while (child != null)
                    {
                        if (child.Current.ControlType == ControlType.Edit)
                        {
                            Logger.Debug("Editable ComboBox detected");
                            return true;
                        }
                        child = walker.GetNextSibling(child);
                    }
                }
                catch
                {
                    // If we can't check children, be conservative
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking if element is text input", ex);
            return false;
        }
    }

    /// <summary>
    /// Check if our keyboard window currently has focus
    /// </summary>
    private bool IsKeyboardWindowFocused()
    {
        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            return foregroundWindow == _keyboardWindowHandle;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Notify manager that keyboard was hidden (for cooldown tracking)
    /// </summary>
    public void NotifyKeyboardHidden()
    {
        _lastHideTime = DateTime.Now;
        Logger.Debug("Keyboard hide notification received, cooldown started");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopMonitoring();
        
        Logger.Info("AutoShowManager disposed");
    }
}