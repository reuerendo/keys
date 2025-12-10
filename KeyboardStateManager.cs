using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VirtualKeyboard;

/// <summary>
/// Manages keyboard modifier states (Shift, Caps Lock, Ctrl, Alt)
/// </summary>
public class KeyboardStateManager
{
    // VK codes for modifiers
    private const byte VK_SHIFT = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_ALT = 0x12;

    private readonly KeyboardInputService _inputService;

    // Modifier states
    public bool IsShiftActive { get; private set; }
    public bool IsCapsLockActive { get; private set; }
    public bool IsCtrlActive { get; private set; }
    public bool IsAltActive { get; private set; }

    // Button references (lazy initialized)
    private Button _shiftButton;
    private Button _capsButton;
    private Button _ctrlButton;
    private Button _altButton;

    public KeyboardStateManager(KeyboardInputService inputService)
    {
        _inputService = inputService;
    }

    /// <summary>
    /// Toggle Shift state
    /// </summary>
    public void ToggleShift()
    {
        IsShiftActive = !IsShiftActive;
        
        if (IsShiftActive)
            _inputService.SendModifierKeyDown(VK_SHIFT);
        else
            _inputService.SendModifierKeyUp(VK_SHIFT);
        
        UpdateModifierButtonStyle();
        Logger.Info($"Shift toggled: {IsShiftActive}");
    }

    /// <summary>
    /// Toggle Caps Lock state
    /// </summary>
    public void ToggleCapsLock()
    {
        IsCapsLockActive = !IsCapsLockActive;
        UpdateModifierButtonStyle();
        Logger.Info($"Caps Lock toggled: {IsCapsLockActive}");
    }

    /// <summary>
    /// Toggle Ctrl state
    /// </summary>
    public void ToggleCtrl()
    {
        IsCtrlActive = !IsCtrlActive;
        
        if (IsCtrlActive)
            _inputService.SendModifierKeyDown(VK_CONTROL);
        else
            _inputService.SendModifierKeyUp(VK_CONTROL);
        
        UpdateModifierButtonStyle();
        Logger.Info($"Ctrl toggled: {IsCtrlActive}");
    }

    /// <summary>
    /// Toggle Alt state
    /// </summary>
    public void ToggleAlt()
    {
        IsAltActive = !IsAltActive;
        
        if (IsAltActive)
            _inputService.SendModifierKeyDown(VK_ALT);
        else
            _inputService.SendModifierKeyUp(VK_ALT);
        
        UpdateModifierButtonStyle();
        Logger.Info($"Alt toggled: {IsAltActive}");
    }

    /// <summary>
    /// Reset Shift if active
    /// </summary>
    public void ResetShiftIfActive()
    {
        if (IsShiftActive)
        {
            ToggleShift();
        }
    }

    /// <summary>
    /// Reset Ctrl if active
    /// </summary>
    public void ResetCtrlIfActive()
    {
        if (IsCtrlActive)
        {
            ToggleCtrl();
        }
    }

    /// <summary>
    /// Reset Alt if active
    /// </summary>
    public void ResetAltIfActive()
    {
        if (IsAltActive)
        {
            ToggleAlt();
        }
    }

    /// <summary>
    /// Update visual style of modifier buttons
    /// </summary>
    private void UpdateModifierButtonStyle()
    {
        if (_shiftButton != null)
        {
            _shiftButton.Opacity = IsShiftActive ? 0.7 : 1.0;
        }
        if (_capsButton != null)
        {
            _capsButton.Opacity = IsCapsLockActive ? 0.7 : 1.0;
        }
        if (_ctrlButton != null)
        {
            _ctrlButton.Opacity = IsCtrlActive ? 0.7 : 1.0;
        }
        if (_altButton != null)
        {
            _altButton.Opacity = IsAltActive ? 0.7 : 1.0;
        }
    }

    /// <summary>
    /// Initialize button references from UI tree
    /// </summary>
    public void InitializeButtonReferences(FrameworkElement rootElement)
    {
        FindShiftButton(rootElement);
        FindCapsButton(rootElement);
        FindCtrlButton(rootElement);
        FindAltButton(rootElement);
    }

    /// <summary>
    /// Find Shift button in UI tree
    /// </summary>
    private void FindShiftButton(FrameworkElement element)
    {
        if (_shiftButton != null) return;

        if (element is Button btn && btn.Tag as string == "Shift")
        {
            _shiftButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindShiftButton(fe);
                if (_shiftButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindShiftButton(scrollContent);
        }
    }

    /// <summary>
    /// Find Caps Lock button in UI tree
    /// </summary>
    private void FindCapsButton(FrameworkElement element)
    {
        if (_capsButton != null) return;

        if (element is Button btn && btn.Tag as string == "Caps")
        {
            _capsButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindCapsButton(fe);
                if (_capsButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindCapsButton(scrollContent);
        }
    }

    /// <summary>
    /// Find Ctrl button in UI tree
    /// </summary>
    private void FindCtrlButton(FrameworkElement element)
    {
        if (_ctrlButton != null) return;

        if (element is Button btn && btn.Tag as string == "Ctrl")
        {
            _ctrlButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindCtrlButton(fe);
                if (_ctrlButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindCtrlButton(scrollContent);
        }
    }

    /// <summary>
    /// Find Alt button in UI tree
    /// </summary>
    private void FindAltButton(FrameworkElement element)
    {
        if (_altButton != null) return;

        if (element is Button btn && btn.Tag as string == "Alt")
        {
            _altButton = btn;
            return;
        }

        if (element is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is FrameworkElement fe)
                    FindAltButton(fe);
                if (_altButton != null) return;
            }
        }
        else if (element is ScrollViewer scrollViewer && scrollViewer.Content is FrameworkElement scrollContent)
        {
            FindAltButton(scrollContent);
        }
    }
}