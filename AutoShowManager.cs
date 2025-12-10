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
            // Это работает для Chrome, Edge, Word, Telegram и т.д.
            IUIAutomationElement focusedElement = null;
            try 
            {
                _uiAutomation.GetFocusedElement(out focusedElement);
            }
            catch 
            {
                // Иногда UIA выбрасывает исключение, если фокус меняется в момент опроса
                return;
            }

            if (focusedElement != null)
            {
                // Получаем тип контрола
                int controlType = focusedElement.CurrentControlType;
                
                // Проверяем, является ли это полем ввода
                bool isTextInput = IsTextControl(controlType);
                
                // Дополнительная проверка: не "только для чтения"
                // (Некоторые лейблы могут иметь фокус, но не принимать ввод)
                // Примечание: проверка IsReadOnly может быть медленной, 
                // поэтому используем её только если тип контрола подходящий.
                
                if (isTextInput)
                {
                    // Логируем только если состояние изменилось, чтобы не спамить в лог
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
        // UIA_EditControlTypeId = 50004 (Стандартные поля ввода, TextBox)
        // UIA_DocumentControlTypeId = 50030 (Word, содержимое веб-страницы в браузере)
        // UIA_ComboBoxControlTypeId = 50003 (Выпадающие списки с вводом)
        
        return controlType == 50004 || // Edit
               controlType == 50030 || // Document (Chrome/Edge content)
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
            Marshal.ReleaseComObject(_uiAutomation);
            _uiAutomation = null;
        }
        Logger.Info("AutoShowManager disposed");
    }
}

// --- COM Interfaces Definitions ---
// Определяем интерфейсы здесь, чтобы не требовать добавления внешних ссылок (DLL)

[ComImport]
[Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
public class CUIAutomation
{
}

[ComImport]
[Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomation
{
    void CompareElements(IUIAutomationElement el1, IUIAutomationElement el2, out int areSame);
    void CompareRuntimeIds(IntPtr runId1, IntPtr runId2, out int areSame);
    void GetRootElement(out IUIAutomationElement root);
    void GetFocusedElement(out IUIAutomationElement element);
    // Остальные методы интерфейса опущены для краткости, так как они нам не нужны
}

[ComImport]
[Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
    
    // Свойства элемента
    void get_CurrentProcessId(out int retVal);
    void get_CurrentControlType(out int retVal);
    void get_CurrentLocalizedControlType(out string retVal);
    void get_CurrentName(out string retVal);
    void get_CurrentAcceleratorKey(out string retVal);
    void get_CurrentAccessKey(out string retVal);
    void get_CurrentHasKeyboardFocus(out int retVal);
    void get_CurrentIsKeyboardFocusable(out int retVal);
    void get_CurrentIsEnabled(out int retVal);
    
    // Вспомогательное свойство для C# (чтобы не вызывать get_CurrentControlType вручную)
    int CurrentControlType 
    { 
        get 
        { 
            try { 
                get_CurrentControlType(out int val); 
                return val; 
            } catch { return 0; }
        } 
    }
}