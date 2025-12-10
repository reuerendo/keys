// AutoShowManager.cs
using System;
using System.Threading;
using System.Windows.Automation;
using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace VirtualKeyboard
{
    /// <summary>
    /// AutoShowManager (UIA-based)
    /// Uses UI Automation Focus Changed event to detect editable text controls.
    /// Provides a small WinEvent fallback if UIA isn't available for some edge cases.
    /// </summary>
    public sealed class AutoShowManager : IDisposable
    {
        private const int DebounceMilliseconds = 300;

        private readonly IntPtr _keyboardWindowHandle;
        private readonly DispatcherQueue _dispatcherQueue;

        private bool _isEnabled;
        private bool _isDisposed;

        private AutomationFocusChangedEventHandler _uiaFocusHandler;
        private long _lastShowTimestampTicks;

        // Optional fallback hooks (kept minimal). If you prefer to remove fallback entirely - you can.
        private IntPtr _winEventHook = IntPtr.Zero;
        private WinEventDelegate _winEventDelegate;

        public event EventHandler ShowKeyboardRequested;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                if (_isEnabled)
                {
                    Subscribe();
                }
                else
                {
                    Unsubscribe();
                }

                Logger.Info($"AutoShow (UIA) {(_isEnabled ? "enabled" : "disabled")}");
            }
        }

        public AutoShowManager(IntPtr keyboardWindowHandle)
        {
            _keyboardWindowHandle = keyboardWindowHandle;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("DispatcherQueue unavailable on this thread.");

            Initialize();
        }

        private void Initialize()
        {
            try
            {
                Logger.Info("Initializing AutoShowManager (UIA)...");
                _uiaFocusHandler = new AutomationFocusChangedEventHandler(OnAutomationFocusChanged);

                // Prepare optional WinEvent fallback delegate (kept but not hooked until needed)
                _winEventDelegate = new WinEventDelegate(WinEventProc);

                Logger.Info("✓ AutoShowManager (UIA) initialized");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to initialize AutoShowManager", ex);
            }
        }

        private void Subscribe()
        {
            try
            {
                // Subscribe UIA focus changed
                Automation.AddAutomationFocusChangedEventHandler(_uiaFocusHandler);
                Logger.Info("✓ Subscribed to UIA Focus Changed events");

                // Optionally, you may add a minimal WinEvent hook fallback for very old apps:
                // _winEventHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
                // if (_winEventHook != IntPtr.Zero) Logger.Info("✓ WinEvent fallback hooked");
            }
            catch (Exception ex)
            {
                Logger.Warning("UIA subscription failed, attempting WinEvent fallback", ex);
                // Try to hook fallback if UIA fails (rare)
                TryHookWinEventFallback();
            }
        }

        private void Unsubscribe()
        {
            try
            {
                Automation.RemoveAutomationFocusChangedEventHandler(_uiaFocusHandler);
                Logger.Info("Unsubscribed from UIA Focus Changed events");
            }
            catch (Exception ex)
            {
                // ignore if not subscribed
                Logger.Debug($"Error removing UIA handler: {ex.Message}");
            }

            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
                Logger.Info("Unsubscribed WinEvent fallback");
            }
        }

        private void OnAutomationFocusChanged(object src, AutomationFocusChangedEventArgs e)
        {
            if (_isDisposed || !_isEnabled) return;

            try
            {
                // Get currently focused element (thread-safe property)
                AutomationElement focused = AutomationElement.FocusedElement;
                if (focused == null) return;

                // Avoid reacting to our own keyboard window
                if (IsElementKeyboardWindow(focused))
                    return;

                if (IsEditableText(focused))
                {
                    DebouncedRequestShow("UIA");
                }
            }
            catch (ElementNotAvailableException)
            {
                // element disappeared between event and handling - ignore
            }
            catch (Exception ex)
            {
                Logger.Debug($"Error in OnAutomationFocusChanged: {ex.Message}");
            }
        }

        private void DebouncedRequestShow(string sourceTag = "")
        {
            long now = DateTime.UtcNow.Ticks;
            long deltaMs = TimeSpan.FromTicks(now - _lastShowTimestampTicks).TotalMillisecondsAsLong();

            if (deltaMs >= 0 && deltaMs < DebounceMilliseconds)
            {
                Logger.Debug($"Debounced ShowKeyboard (source={sourceTag}, delta={deltaMs}ms)");
                return;
            }

            _lastShowTimestampTicks = now;

            // Dispatch to UI thread
            _dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    Logger.Info($"ShowKeyboardRequested (source={sourceTag})");
                    ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Error invoking ShowKeyboardRequested: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Returns true if automation element represents an editable text control (not read-only).
        /// Uses patterns (ValuePattern/TextPattern) and ControlType as heuristics.
        /// </summary>
        private bool IsEditableText(AutomationElement el)
        {
            if (el == null) return false;

            try
            {
                // If element is not enabled, not editable
                object isEnabledObj = el.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true);
                if (isEnabledObj is bool isEnabled && !isEnabled) return false;

                // If it's a password box, do not auto-show (optional; change if you want to show on password fields)
                object isPasswordObj = el.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                if (isPasswordObj is bool isPassword && isPassword) return false;

                // Try ValuePattern (common for simple edit controls)
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))
                {
                    try
                    {
                        var valuePattern = (ValuePattern)vp;
                        bool readOnly = valuePattern.Current.IsReadOnly;
                        return !readOnly;
                    }
                    catch
                    {
                        // ignore pattern access issues and continue
                    }
                }

                // TextPattern (rich text / document)
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out _))
                {
                    return true;
                }

                // Some controls advertise ControlType.Edit or Document
                var controlType = el.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                if (controlType == ControlType.Edit || controlType == ControlType.Document)
                {
                    // final check: does it expose ValuePattern or TextPattern? If not, assume editable
                    return true;
                }

                // Fallback: if the element exposes the ValuePattern and IsReadOnly property is false via raw property retrieval
                object readOnlyProp = el.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty, true);
                if (readOnlyProp is bool ro)
                {
                    return !ro;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"IsEditableText error: {ex.Message}");
            }

            return false;
        }

        private bool IsElementKeyboardWindow(AutomationElement el)
        {
            try
            {
                object hwndObj = el.GetCurrentPropertyValue(AutomationElement.NativeWindowHandleProperty, true);
                if (hwndObj is int hwndInt && hwndInt != 0)
                {
                    IntPtr hwnd = new IntPtr(hwndInt);
                    return hwnd == _keyboardWindowHandle;
                }
            }
            catch
            {
                // ignore
            }

            return false;
        }

        private void TryHookWinEventFallback()
        {
            try
            {
                // Minimal fallback: hook focus events via WinEvent API only if UIA failed
                _winEventHook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
                if (_winEventHook != IntPtr.Zero)
                {
                    Logger.Info("✓ WinEvent fallback hooked");
                }
                else
                {
                    Logger.Warning("WinEvent fallback failed to hook");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"WinEvent fallback error: {ex.Message}");
            }
        }

        #region Dispose

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                Unsubscribe();
            }
            catch { }

            Logger.Info("AutoShowManager (UIA) disposed");
        }

        #endregion

        #region Helpers & Extensions

        // Small helper to convert ticks diff to long ms safely
        // placed as private extension-like method
        private static class TimeSpanExtensions
        {
            public static long TotalMillisecondsAsLong(this TimeSpan ts)
            {
                return (long)ts.TotalMilliseconds;
            }
        }

        // Overload caller convenience
        private static long TotalMillisecondsAsLong(this long ticksDelta)
        {
            return TimeSpan.FromTicks(ticksDelta).TotalMillisecondsAsLong();
        }

        #endregion

        #region WinEvent fallback interop (minimal)

        // Only used as a fallback if UIA can't be installed for some reason.
        // You can remove this whole region if not needed.

        private const uint EVENT_OBJECT_FOCUS = 0x8005;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (_isDisposed || hwnd == IntPtr.Zero || hwnd == _keyboardWindowHandle) return;

            try
            {
                // Convert hwnd to AutomationElement and reuse IsEditableText
                var el = AutomationElement.FromHandle(hwnd);
                if (el != null && IsEditableText(el))
                {
                    DebouncedRequestShow("WinEventFallback");
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"WinEventProc error: {ex.Message}");
            }
        }

        #endregion
    }

    // Simple Logger stub - replace with your app logger
    internal static class Logger
    {
        public static void Info(string s) => System.Diagnostics.Debug.WriteLine("[INFO] " + s);
        public static void Warning(string s, Exception ex = null) => System.Diagnostics.Debug.WriteLine("[WARN] " + s + (ex != null ? " " + ex.Message : ""));
        public static void Debug(string s) => System.Diagnostics.Debug.WriteLine("[DBG] " + s);
        public static void Error(string s, Exception ex = null) => System.Diagnostics.Debug.WriteLine("[ERR] " + s + (ex != null ? " " + ex.Message : ""));
    }
}
