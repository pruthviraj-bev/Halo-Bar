using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using DynamicIsland.ViewModels;
using WinRT;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;

namespace DynamicIsland.Views;

/// <summary>
/// Taskbar widget shell window.
///
/// Routing rules:
///  - Left-click  → IslandController.NotifyIslandClick()   (expand/collapse toggle)
///  - Hover enter → IslandController.NotifyMouseEnter()    (cancel auto-collapse)
///  - Hover exit  --> IslandController.NotifyMouseLeave()    (restart short auto-collapse)
///  - Deactivated → IslandController.NotifyFocusLost()     (immediate collapse)
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindowViewModel ViewModel { get; } = new();
    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _configuration;

    public MainWindow()
    {
        InitializeComponent();

        SetAcrylicBackdrop();
    }

    private void SetAcrylicBackdrop()
    {
        bool supported = DesktopAcrylicController.IsSupported();
        if (supported)
        {
            _acrylicController = new DesktopAcrylicController();

            _configuration = new SystemBackdropConfiguration();
            
            // Force active backdrop state permanently so the window never goes solid gray on deactivation
            _configuration.IsInputActive = true;

            var supportsSystemBackdrop = this.As<ICompositionSupportsSystemBackdrop>();
            _acrylicController.AddSystemBackdropTarget(supportsSystemBackdrop);
            _acrylicController.SetSystemBackdropConfiguration(_configuration);
        }
    }

    // ── Click to expand/collapse ───────────────────────────────────────────

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(RootGrid).Properties;
        if (properties.IsLeftButtonPressed)
        {
            App.IslandController.NotifyIslandClick();
            e.Handled = true;
        }
    }

    // SetFullscreenSuppressed: actual hide/show is handled by WindowService.ForceAboveTaskbar
    // via AppWindow.Hide() / AppWindow.Show() so the acrylic surface is also removed.
    // This method is kept for future use (e.g. additional UI state on suppression).
    public void SetFullscreenSuppressed(bool suppress)
    {
    }

    // ── Hover tracking (for auto-collapse) ────────────────────────────────

    private void RootGrid_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        App.IslandController.NotifyMouseEnter();
    }

    private void RootGrid_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        App.IslandController.NotifyMouseLeave();
    }
}
