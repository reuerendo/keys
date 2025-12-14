using System;
using System.Runtime.InteropServices;

namespace VirtualKeyboard;

/// <summary>
/// Helper class for disabling Windows DWM (Desktop Window Manager) animations
/// </summary>
public static class DwmHelper
{
    // DWM Window Attributes
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const int DWMWA_CLOAK = 13;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attr,
        out int attrValue,
        int attrSize);

    /// <summary>
    /// Disable all DWM transitions for a window (fade, slide, zoom animations)
    /// </summary>
    public static bool DisableTransitions(IntPtr hwnd)
    {
        try
        {
            int value = 1; // TRUE
            int result = DwmSetWindowAttribute(
                hwnd,
                DWMWA_TRANSITIONS_FORCEDISABLED,
                ref value,
                sizeof(int));

            if (result == 0) // S_OK
            {
                Logger.Info("DWM transitions disabled successfully");
                return true;
            }
            else
            {
                Logger.Warning($"DWM transitions disable returned: 0x{result:X}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to disable DWM transitions", ex);
            return false;
        }
    }

    /// <summary>
    /// Enable DWM transitions for a window
    /// </summary>
    public static bool EnableTransitions(IntPtr hwnd)
    {
        try
        {
            int value = 0; // FALSE
            int result = DwmSetWindowAttribute(
                hwnd,
                DWMWA_TRANSITIONS_FORCEDISABLED,
                ref value,
                sizeof(int));

            if (result == 0)
            {
                Logger.Info("DWM transitions enabled successfully");
                return true;
            }
            else
            {
                Logger.Warning($"DWM transitions enable returned: 0x{result:X}");
                return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to enable DWM transitions", ex);
            return false;
        }
    }

    /// <summary>
    /// Check if transitions are disabled for a window
    /// </summary>
    public static bool AreTransitionsDisabled(IntPtr hwnd)
    {
        try
        {
            int value;
            int result = DwmGetWindowAttribute(
                hwnd,
                DWMWA_TRANSITIONS_FORCEDISABLED,
                out value,
                sizeof(int));

            if (result == 0)
            {
                return value != 0;
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to check DWM transitions state", ex);
            return false;
        }
    }

    /// <summary>
    /// Cloak/uncloak window (hide without animation)
    /// </summary>
    public static bool SetCloaked(IntPtr hwnd, bool cloaked)
    {
        try
        {
            int value = cloaked ? 1 : 0;
            int result = DwmSetWindowAttribute(
                hwnd,
                DWMWA_CLOAK,
                ref value,
                sizeof(int));

            return result == 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to set cloak state", ex);
            return false;
        }
    }
}