using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using DynamicIsland.ViewModels;
using DynamicIsland.Widgets;
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

        // Collapse when the user clicks anywhere outside the widget.
        Activated += MainWindow_Activated;
    }

    private void SetAcrylicBackdrop()
    {
        if (DesktopAcrylicController.IsSupported())
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

    public Visibility BoolToVisibility(bool value)
    {
        return value ? Visibility.Visible : Visibility.Collapsed;
    }

    public Visibility BoolToVisibilityInverse(bool value)
    {
        return value ? Visibility.Collapsed : Visibility.Visible;
    }

    private UserControl? _expandedTaskbarAnchor;

    public UserControl? GetTaskbarContent(bool isExpanded, UserControl? activeWidget)
    {
        if (isExpanded)
        {
            return _expandedTaskbarAnchor ??= new WeatherCollapsedWidget();
        }
        return activeWidget;
    }

    // ── Window activation ──────────────────────────────────────────────────

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        bool configActive = _configuration != null ? _configuration.IsInputActive : false;
        Helpers.Logger.Info($"[DEBUG_EVENT] MainWindow_Activated: State={e.WindowActivationState}, IsInputActive={configActive} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
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
        Helpers.Logger.Info($"[DEBUG_EVENT] MainWindow.SetFullscreenSuppressed: suppress={suppress}");
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
