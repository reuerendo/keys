using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VirtualKeyboard;

/// <summary>
/// Manages interactive regions for toolbar buttons and drag areas using InputNonClientPointerSource
/// </summary>
public class InteractiveRegionsManager
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly IntPtr _hwnd;
    private readonly Window _window;
    private InputNonClientPointerSource _nonClientPointerSource;

    public InteractiveRegionsManager(IntPtr windowHandle, Window window)
    {
        _hwnd = windowHandle;
        _window = window;
    }

    /// <summary>
    /// Initialize InputNonClientPointerSource for managing non-client regions
    /// </summary>
    public void Initialize()
    {
        try
        {
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(windowId);
            Logger.Info("InputNonClientPointerSource initialized");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to initialize InputNonClientPointerSource", ex);
        }
    }

    /// <summary>
    /// Update interactive regions for toolbar buttons and drag region
    /// </summary>
    public void UpdateRegions()
    {
        if (_nonClientPointerSource == null)
            return;

        try
        {
            var rootElement = _window.Content as FrameworkElement;
            if (rootElement == null)
                return;

            double scale = GetRasterizationScale();

            // Setup passthrough regions for toolbar buttons
            var interactiveRects = GetToolbarButtonRegions(rootElement, scale);
            if (interactiveRects.Count > 0)
            {
                _nonClientPointerSource.SetRegionRects(
                    NonClientRegionKind.Passthrough,
                    interactiveRects.ToArray()
                );
                
                Logger.Info($"Set {interactiveRects.Count} interactive regions successfully");
            }

            // Setup drag region (Caption)
            SetupDragRegion(rootElement, scale);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to update interactive regions", ex);
        }
    }

    /// <summary>
    /// Get bounding rectangles for all toolbar buttons
    /// </summary>
    private List<Windows.Graphics.RectInt32> GetToolbarButtonRegions(FrameworkElement rootElement, double scale)
    {
        var interactiveRects = new List<Windows.Graphics.RectInt32>();

        var toolbarButtons = new[]
        {
            rootElement.FindName("CopyButton") as Button,
            rootElement.FindName("CutButton") as Button,
            rootElement.FindName("PasteButton") as Button,
            rootElement.FindName("DeleteButton") as Button,
            rootElement.FindName("SelectAllButton") as Button
        };

        foreach (var button in toolbarButtons)
        {
            if (button != null && button.ActualWidth > 0 && button.ActualHeight > 0)
            {
                var rect = GetElementRect(button, rootElement, scale);
                if (rect.HasValue)
                {
                    interactiveRects.Add(rect.Value);
                    Logger.Info($"Added interactive region for {button.Name}: X={rect.Value.X}, Y={rect.Value.Y}, W={rect.Value.Width}, H={rect.Value.Height}");
                }
            }
        }

        return interactiveRects;
    }

    /// <summary>
    /// Setup drag region for window title bar
    /// </summary>
    private void SetupDragRegion(FrameworkElement rootElement, double scale)
    {
        var dragRegion = rootElement.FindName("DragRegion") as Border;
        if (dragRegion != null && dragRegion.ActualWidth > 0 && dragRegion.ActualHeight > 0)
        {
            var dragRect = GetElementRect(dragRegion, rootElement, scale);
            if (dragRect.HasValue)
            {
                _nonClientPointerSource.SetRegionRects(
                    NonClientRegionKind.Caption,
                    new[] { dragRect.Value }
                );
                
                Logger.Info($"Set drag region: X={dragRect.Value.X}, Y={dragRect.Value.Y}, W={dragRect.Value.Width}, H={dragRect.Value.Height}");
            }
        }
    }

    /// <summary>
    /// Get element rectangle in physical coordinates
    /// </summary>
    private Windows.Graphics.RectInt32? GetElementRect(FrameworkElement element, FrameworkElement root, double scale)
    {
        try
        {
            var transform = element.TransformToVisual(root);
            var bounds = transform.TransformBounds(
                new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight)
            );

            return new Windows.Graphics.RectInt32
            {
                X = (int)Math.Round(bounds.X * scale),
                Y = (int)Math.Round(bounds.Y * scale),
                Width = (int)Math.Round(bounds.Width * scale),
                Height = (int)Math.Round(bounds.Height * scale)
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to get element rect for {element?.Name}", ex);
            return null;
        }
    }

    /// <summary>
    /// Get rasterization scale for converting to physical coordinates
    /// </summary>
    private double GetRasterizationScale()
    {
        try
        {
            if (_window.Content is FrameworkElement rootElement && 
                rootElement.XamlRoot?.RasterizationScale > 0)
            {
                return rootElement.XamlRoot.RasterizationScale;
            }
        }
        catch { }

        // Fallback: use DPI
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi / 96.0;
    }
}