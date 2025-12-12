namespace VirtualKeyboard;

/// <summary>
/// Manages clipboard operations (Copy, Cut, Paste, Delete, SelectAll)
/// </summary>
public class ClipboardManager
{
    private readonly KeyboardInputService _inputService;

    public ClipboardManager(KeyboardInputService inputService)
    {
        _inputService = inputService;
    }

    /// <summary>
    /// Copy selected text to clipboard
    /// </summary>
    public void Copy()
    {
        Logger.Info("Copy requested");
        _inputService.SendCtrlKey('C');
    }

    /// <summary>
    /// Cut selected text to clipboard
    /// </summary>
    public void Cut()
    {
        Logger.Info("Cut requested");
        _inputService.SendCtrlKey('X');
    }

    /// <summary>
    /// Paste text from clipboard
    /// </summary>
    public void Paste()
    {
        Logger.Info("Paste requested");
        _inputService.SendCtrlKey('V');
    }

    /// <summary>
    /// Delete selected text (without clipboard)
    /// </summary>
    public void Delete()
    {
        Logger.Info("Delete requested");
        _inputService.SendKey(0x2E); // VK_DELETE
    }

    /// <summary>
    /// Select all text
    /// </summary>
    public void SelectAll()
    {
        Logger.Info("Select All requested");
        _inputService.SendCtrlKey('A');
    }
}