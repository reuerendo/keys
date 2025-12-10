using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Manages automatic keyboard visibility based on text input focus
/// </summary>
public class AutoShowManager : IDisposable
{
    // Win32 Constants
    private const int WH_CALLWNDPROC = 4;
    private const int WM_SETFOCUS = 0x0007;
    private const int WM_KILLFOCUS = 0x0008;
    
    // Edit control class names
    private static readonly string[] EditControlClasses = 
    {
        "Edit",
        "RichEdit",
        "RichEdit20A",
        "RichEdit20W",
        "RichEdit50W",
        "RICHEDIT60W"
    };

    // Delegates
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    // Structures
    [StructLayout(LayoutKind.Sequential)]
    private struct CWPSTRUCT
    {
        public IntPtr lParam;
        public IntPtr wParam;
        public uint message;
        public IntPtr hwnd;
    }

    // P/Invoke
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private IntPtr _hookHandle;
    private HookProc _hookProc;
    private IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private DispatcherQueue _dispatcherQueue;

    public event EventHandler ShowKeyboardRequested;
    public event EventHandler HideKeyboardRequested;

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
                    InstallHook();
                }
                else
                {
                    UninstallHook();
                }
                
                Logger.Info($"AutoShow {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        
        // Keep reference to prevent garbage collection
        _hookProc = HookCallback;
        
        Logger.Info("AutoShowManager initialized");
    }

    private void InstallHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Logger.Warning("Hook already installed");
            return;
        }

        try
        {
            IntPtr hModule = GetModuleHandle(null);
            _hookHandle = SetWindowsHookEx(WH_CALLWNDPROC, _hookProc, hModule, 0);
            
            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Logger.Error($"Failed to install hook. Error: {error}");
            }
            else
            {
                Logger.Info("Windows hook installed successfully");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Exception installing hook", ex);
        }
    }

    private void UninstallHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            try
            {
                bool success = UnhookWindowsHookEx(_hookHandle);
                if (success)
                {
                    Logger.Info("Windows hook uninstalled successfully");
                }
                else
                {
                    Logger.Warning("Failed to uninstall hook");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception uninstalling hook", ex);
            }
            finally
            {
                _hookHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isEnabled)
        {
            try
            {
                CWPSTRUCT msg = Marshal.PtrToStructure<CWPSTRUCT>(lParam);
                
                // Check if message is focus-related
                if (msg.message == WM_SETFOCUS)
                {
                    // Don't show keyboard if keyboard window itself receives focus
                    if (msg.hwnd != _keyboardWindowHandle)
                    {
                        if (IsEditControl(msg.hwnd))
                        {
                            Logger.Info($"Focus gained on edit control: 0x{msg.hwnd:X}");
                            OnShowKeyboardRequested();
                        }
                    }
                }
                else if (msg.message == WM_KILLFOCUS)
                {
                    // Optional: hide keyboard when focus is lost
                    // For now, we keep keyboard visible for better UX
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception in hook callback", ex);
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool IsEditControl(IntPtr hwnd)
    {
        try
        {
            var className = new System.Text.StringBuilder(256);
            int result = GetClassName(hwnd, className, className.Capacity);
            
            if (result > 0)
            {
                string controlClass = className.ToString();
                
                foreach (string editClass in EditControlClasses)
                {
                    if (controlClass.Equals(editClass, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug($"Edit control detected: {controlClass}");
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Error checking control class", ex);
        }

        return false;
    }

    private void OnShowKeyboardRequested()
    {
        // Dispatch to UI thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    private void OnHideKeyboardRequested()
    {
        // Dispatch to UI thread
        if (_dispatcherQueue != null)
        {
            _dispatcherQueue.TryEnqueue(() =>
            {
                HideKeyboardRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    public void Dispose()
    {
        UninstallHook();
        Logger.Info("AutoShowManager disposed");
    }
}