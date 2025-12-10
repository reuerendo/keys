using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility using Windows Events API with enhanced detection.
/// Works with Browsers, Office, UWP, WPF, and standard Win32 apps.
/// </summary>
public class AutoShowManager : IDisposable
{
    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint EVENT_OBJECT_VALUECHANGE = 0x800E;
    private const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    
    private readonly IntPtr _keyboardWindowHandle;
    private readonly DispatcherQueue _dispatcherQueue;
    
    private IntPtr _hookHandleFocus;
    private IntPtr _hookHandleValueChange;
    private WinEventDelegate _hookDelegate;
    private System.Threading.Timer _focusCheckTimer;
    private IntPtr _lastFocusedWindow = IntPtr.Zero;
    
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
                
                Logger.Info($"AutoShow (WinEvents Enhanced) {(_isEnabled ? "enabled" : "disabled")}");
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
            Logger.Info("Initializing AutoShow using Enhanced Windows Events API...");
            
            _hookDelegate = new WinEventDelegate(WinEventProc);
            
            Logger.Info("✓ AutoShow (WinEvents Enhanced) initialized successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize Windows Events", ex);
        }
    }

    private void SubscribeToFocusEvents()
    {
        if (_hookHandleFocus != IntPtr.Zero)
        {
            Logger.Warning("Already subscribed to focus events");
            return;
        }

        try
        {
            // Hook 1: Основной - события фокуса
            _hookHandleFocus = SetWinEventHook(
                EVENT_OBJECT_FOCUS,
                EVENT_OBJECT_FOCUS,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            // Hook 2: Дополнительный - изменение значений (для полей ввода)
            _hookHandleValueChange = SetWinEventHook(
                EVENT_OBJECT_VALUECHANGE,
                EVENT_OBJECT_VALUECHANGE,
                IntPtr.Zero,
                _hookDelegate,
                0,
                0,
                WINEVENT_OUTOFCONTEXT);

            if (_hookHandleFocus != IntPtr.Zero)
            {
                Logger.Info("✓ Subscribed to focus events (WinEvents Enhanced)");
                
                // Запускаем таймер для периодической проверки фокуса (fallback для сложных приложений)
                _focusCheckTimer = new System.Threading.Timer(CheckFocusTimer, null, 500, 500);
                Logger.Info("✓ Started focus monitoring timer");
            }
            else
            {
                Logger.Error("Failed to set WinEvent hooks");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to subscribe to focus events", ex);
        }
    }

    private void UnsubscribeFromFocusEvents()
    {
        _focusCheckTimer?.Dispose();
        _focusCheckTimer = null;

        if (_hookHandleFocus != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandleFocus);
            _hookHandleFocus = IntPtr.Zero;
        }

        if (_hookHandleValueChange != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandleValueChange);
            _hookHandleValueChange = IntPtr.Zero;
        }

        Logger.Info("Unsubscribed from focus events");
    }

    private void CheckFocusTimer(object state)
    {
        if (_isDisposed) return;

        try
        {
            IntPtr focusedWindow = GetForegroundWindow();
            
            if (focusedWindow == IntPtr.Zero || focusedWindow == _keyboardWindowHandle)
                return;

            // Проверяем только если фокус сменился
            if (focusedWindow != _lastFocusedWindow)
            {
                _lastFocusedWindow = focusedWindow;
                
                // Проверяем наличие каретки (текстового курсора)
                if (HasTextCaret(focusedWindow))
                {
                    Logger.Info($"Text input detected via timer: HWND=0x{focusedWindow:X}");
                    
                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        OnShowKeyboardRequested();
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error in focus check timer: {ex.Message}");
        }
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, 
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_isDisposed || hwnd == IntPtr.Zero || hwnd == _keyboardWindowHandle)
            return;

        try
        {
            if (IsInputElement(hwnd))
            {
                string className = GetWindowClassName(hwnd);
                Logger.Info($"Input focus detected: HWND=0x{hwnd:X}, Class={className}, Event=0x{eventType:X}");

                _dispatcherQueue.TryEnqueue(() =>
                {
                    OnShowKeyboardRequested();
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Error processing focus event: {ex.Message}");
        }
    }

    private bool IsInputElement(IntPtr hwnd)
    {
        try
        {
            string className = GetWindowClassName(hwnd);
            
            // Расширенный список классов окон для различных приложений
            string[] inputClasses = new[]
            {
                // Стандартные Windows элементы
                "Edit", "RichEdit", "RICHEDIT", "RICHEDIT20", "RICHEDIT50W",
                
                // Браузеры Chromium (Chrome, Edge, Opera, Brave)
                "Chrome_RenderWidgetHostHWND",
                "Chrome_WidgetWin_0", "Chrome_WidgetWin_1",
                
                // Firefox
                "MozillaWindowClass", "MozillaContentWindowClass",
                
                // Internet Explorer
                "Internet Explorer_Server",
                
                // Office приложения
                "_WwG", "OpusApp", "EXCEL7", "PPTFrameClass",
                
                // UWP / WinUI 3 приложения
                "Windows.UI.Core.CoreWindow",
                "ApplicationFrameInputSinkWindow",
                "TextBox", "RichEditBox",
                
                // WPF приложения
                "HwndWrapper",
                
                // Прочие
                "ThunderRT6TextBox", // Visual Basic
                "Scintilla", "SciTEWindow", // Редакторы кода
                "ConsoleWindowClass" // Консоль
            };

            // Проверяем прямое совпадение или частичное содержание
            foreach (string inputClass in inputClasses)
            {
                if (className.Equals(inputClass, StringComparison.OrdinalIgnoreCase) ||
                    className.Contains(inputClass, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Дополнительная проверка: есть ли у окна каретка (текстовый курсор)
            if (HasTextCaret(hwnd))
            {
                Logger.Debug($"Text caret detected in: {className}");
                return true;
            }

            // Проверка родительского окна (для вложенных элементов)
            IntPtr parent = GetParent(hwnd);
            if (parent != IntPtr.Zero && parent != hwnd)
            {
                string parentClass = GetWindowClassName(parent);
                foreach (string inputClass in inputClasses)
                {
                    if (parentClass.Contains(inputClass, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug($"Parent window is input: {parentClass}");
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private string GetWindowClassName(IntPtr hwnd)
    {
        const int maxClassNameLength = 256;
        System.Text.StringBuilder className = new System.Text.StringBuilder(maxClassNameLength);
        
        if (GetClassName(hwnd, className, maxClassNameLength) > 0)
        {
            return className.ToString();
        }
        
        return string.Empty;
    }

    private bool HasTextCaret(IntPtr hwnd)
    {
        try
        {
            uint threadId = GetWindowThreadProcessId(hwnd, out _);
            
            GUITHREADINFO guiInfo = new GUITHREADINFO();
            guiInfo.cbSize = Marshal.SizeOf(guiInfo);
            
            if (GetGUIThreadInfo(threadId, ref guiInfo))
            {
                // Если есть каретка и окно с кареткой не наша клавиатура
                if (guiInfo.hwndCaret != IntPtr.Zero && guiInfo.hwndCaret != _keyboardWindowHandle)
                {
                    return true;
                }
            }
            
            return false;
        }
        catch
        {
            return false;
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
        
        Logger.Info("AutoShowManager (WinEvents Enhanced) disposed");
    }

    // =========================================================================
    // Native Methods & Structures
    // =========================================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public System.Drawing.Rectangle rcCaret;
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, 
        IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
}
