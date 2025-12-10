using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility using Windows Events API (более надёжная альтернатива UIA).
/// Works with all applications that support accessibility.
/// </summary>
public class AutoShowManager : IDisposable
{
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    
    private readonly IntPtr _keyboardWindowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    
    private IntPtr _hookHandle;
    private WinEventDelegate _hookDelegate; // Храним делегат, чтобы не был собран GC
    private bool _isEnabled;
    private bool _isDisposed;

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
                    SubscribeToFocusEvents();
                else
                    UnsubscribeFromFocusEvents();
                
                Logger.Info($"AutoShow (WinEvents) {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        InitializeWinEvents();
    }

    private void InitializeWinEvents()
    {
        try
        {
            Logger.Info("Initializing AutoShow using Windows Events API...");
            
            // Создаём делегат и сохраняем ссылку
            _hookDelegate = new WinEventDelegate(WinEventProc);
            
            Logger.Info("✓ AutoShow (WinEvents) initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize Windows Events", ex);
        }
    }

    private void SubscribeToFocusEvents()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Logger.Warning("Already subscribed to focus events");
            return;
        }

        try
        {
            // Устанавливаем хук на события фокуса
            _hookHandle = SetWinEventHook(
                EVENT_OBJECT_FOCUS,
                EVENT_OBJECT_FOCUS,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (_hookHandle != IntPtr.Zero)
            {
                Logger.Info("✓ Subscribed to global focus change events (WinEvents)");
            }
            else
            {
                Logger.Error("Failed to set WinEvent hook");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to subscribe to focus events", ex);
        }
    }

    private void UnsubscribeFromFocusEvents()
    {
        if (_hookHandle == IntPtr.Zero) return;

        try
        {
            bool result = UnhookWinEvent(_hookHandle);
            if (result)
            {
                _hookHandle = IntPtr.Zero;
                Logger.Info("Unsubscribed from focus events");
            }
            else
            {
                Logger.Warning("Failed to unhook WinEvent");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to unsubscribe from focus events", ex);
        }
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || hwnd == _keyboardWindowHandle)
            return;

        try
        {
            // Проверяем класс окна
            const int maxClassNameLength = 256;
            System.Text.StringBuilder className = new System.Text.StringBuilder(maxClassNameLength);
            
            if (GetClassName(hwnd, className, maxClassNameLength) > 0)
            {
                string classNameStr = className.ToString();
                
                // Проверяем, является ли это полем ввода
                bool isEditControl = classNameStr.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                                    classNameStr.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                                    classNameStr.Equals("RICHEDIT50W", StringComparison.OrdinalIgnoreCase) ||
                                    classNameStr.Contains("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) ||
                                    classNameStr.Contains("Internet Explorer_Server", StringComparison.OrdinalIgnoreCase);

                if (isEditControl)
                {
                    Logger.Info($"Input focus detected: HWND=0x{hwnd:X}, Class={classNameStr}");

                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        OnShowKeyboardRequested();
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error processing focus event: {ex.Message}");
        }
    }

    private void OnShowKeyboardRequested()
    {
        ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        UnsubscribeFromFocusEvents();
        
        Logger.Info("AutoShowManager (WinEvents) disposed");
    }

    // =========================================================================
    // Native Methods
    // =========================================================================

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, 
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
}
