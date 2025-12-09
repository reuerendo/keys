using Microsoft.UI.Xaml;

namespace VirtualKeyboard;

public partial class App : Application
{
    private Window m_window;
    private TrayIconManager _trayIconManager;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        m_window = new MainWindow();
        
        // Initialize tray icon manager
        _trayIconManager = new TrayIconManager(m_window, this);
        
        // Show initial notification
        _trayIconManager.ShowNotification(
            "Virtual Keyboard",
            "Virtual Keyboard is running. Double-click tray icon to show/hide.",
            System.Windows.Forms.ToolTipIcon.Info
        );
        
        m_window.Activate();
        
        Logger.Info("Application launched with tray icon support");
    }
}