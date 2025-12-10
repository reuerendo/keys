using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace VirtualKeyboard;

/// <summary>
/// Управляет автоматическим показом клавиатуры, используя Microsoft UI Automation.
/// Работает с браузерами, WPF, UWP и Win32 приложениями.
/// </summary>
public class AutoShowManager : IDisposable
{
    // Импорт функций для проверки собственного окна
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly IntPtr _keyboardWindowHandle;
    private bool _isEnabled;
    private DispatcherQueueTimer _pollingTimer;
    private bool _wasKeyboardShown;
    
    // UI Automation интерфейс
    private IUIAutomation _uiAutomation;

    public event EventHandler ShowKeyboardRequested;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled != value)
            {
                _isEnabled = value;
                if (_isEnabled) StartPolling();
                else StopPolling();
                
                Logger.Info($"AutoShow {(_isEnabled ? "enabled" : "disabled")}");
            }
        }
    }

    public AutoShowManager(IntPtr keyboardWindowHandle)
    {
        _keyboardWindowHandle = keyboardWindowHandle;
        InitializeUIAutomation();
        Logger.Info("AutoShowManager initialized (UI Automation mode)");
    }

    private void InitializeUIAutomation()
    {
        try
        {
            // Создаем COM-объект CUIAutomation
            _uiAutomation = new CUIAutomation() as IUIAutomation;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize UI Automation. Auto-show may not work.", ex);
        }
    }

    private void StartPolling()
    {
        if (_pollingTimer != null) return;

        try
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            if (dispatcherQueue == null) return;

            _pollingTimer = dispatcherQueue.CreateTimer();
            _pollingTimer.Interval = TimeSpan.FromMilliseconds(250);
            _pollingTimer.Tick += PollingTimer_Tick;
            _pollingTimer.Start();
            
            Logger.Info("Polling timer started (250ms interval)");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to start polling timer", ex);
        }
    }

    private void StopPolling()
    {
        if (_pollingTimer != null)
        {
            _pollingTimer.Stop();
            _pollingTimer = null;
            _wasKeyboardShown = false;
        }
    }

    private void PollingTimer_Tick(object sender, object e)
    {
        if (!_isEnabled || _uiAutomation == null) return;

        try
        {
            IntPtr foregroundWindow = GetForegroundWindow();

            // 1. Если активна наша клавиатура — ничего не делаем
            if (foregroundWindow == _keyboardWindowHandle) return;

            // 2. Если нет активного окна — сбрасываем состояние
            if (foregroundWindow == IntPtr.Zero)
            {
                _wasKeyboardShown = false;
                return;
            }

            // 3. Получаем элемент под фокусом через UI Automation
            IUIAutomationElement focusedElement = null;
            try 
            {
                _uiAutomation.GetFocusedElement(out focusedElement);
            }
            catch 
            {
                // Игнорируем исключения, если фокус быстро меняется.
                return;
            }

            if (focusedElement != null)
            {
                int controlType = 0;
                
                // Используем прямой вызов метода COM
                focusedElement.get_CurrentControlType(out controlType);

                // Проверяем, является ли это полем ввода
                bool isTextInput = IsTextControl(controlType);
                
                if (isTextInput)
                {
                    if (!_wasKeyboardShown)
                    {
                        Logger.Info($"Text input detected (Type ID: {controlType}). Requesting keyboard.");
                        OnShowKeyboardRequested();
                        _wasKeyboardShown = true;
                    }
                }
                else
                {
                    _wasKeyboardShown = false;
                }
            }
        }
        catch (Exception ex)
        {
            // Не крашим приложение из-за ошибок опроса
            Logger.Debug($"Error in polling tick: {ex.Message}");
        }
    }

    private bool IsTextControl(int controlType)
    {
        // ID типов контролов согласно документации Microsoft UIA:
        // 50004: UIA_EditControlTypeId (Стандартные поля ввода, TextBox)
        // 50030: UIA_DocumentControlTypeId (Word, содержимое веб-страницы в браузере)
        // 50003: UIA_ComboBoxControlTypeId (Выпадающие списки с вводом)
        
        return controlType == 50004 || // Edit
               controlType == 50030 || // Document 
               controlType == 50003;   // ComboBox
    }

    private void OnShowKeyboardRequested()
    {
        ShowKeyboardRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        StopPolling();
        // Освобождаем COM объект
        if (_uiAutomation != null)
        {
            try
            {
                Marshal.ReleaseComObject(_uiAutomation);
            }
            catch { }
            _uiAutomation = null;
        }
        Logger.Info("AutoShowManager disposed");
    }
}

// --- COM Interfaces Definitions ---

[ComImport]
[Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
// Это CoClass для создания объекта UIA.
public class CUIAutomation
{
}

[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
// IUIAutomation: основной интерфейс для работы с UIA.
public interface IUIAutomation
{
    void CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);
    void CompareRuntimeIds(IntPtr runId1, IntPtr runId2, out int areSame);
    void GetRootElement(out IUIAutomationElement root);
    void GetFocusedElement(out IUIAutomationElement element);
    // Остальные методы интерфейса опущены для краткости.
}

[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
// IUIAutomationElement: представляет UI-элемент.
public interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId(out IntPtr runtimeId);
    void FindFirst(int scope, IntPtr condition, out IUIAutomationElement found);
    void FindAll(int scope, IntPtr condition, out IntPtr found);
    void FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement found);
    void FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
    void BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement updatedElement);
    void GetCurrentPropertyValue(int propertyId, out object retVal);
    void GetCachedPropertyValue(int propertyId, out object retVal);
    void GetCurrentPattern(int patternId, out object retVal);
    void GetCachedPattern(int patternId, out object retVal);
    
    // Свойства элемента (должны быть методами, согласно COM)
    void get_CurrentProcessId(out int retVal);
    void get_CurrentControlType(out int retVal);
    void get_CurrentLocalizedControlType(out string retVal);
    void get_CurrentName(out string retVal);
    void get_CurrentAcceleratorKey(out string retVal);
    void get_CurrentAccessKey(out string retVal);
    void get_CurrentHasKeyboardFocus(out int retVal);
    void get_CurrentIsKeyboardFocusable(out int retVal);
    void get_CurrentIsEnabled(out int retVal);
} // Здесь была ошибка. Теперь чистое закрытие.