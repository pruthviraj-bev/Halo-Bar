using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace DynamicIsland.Widgets;

public sealed partial class PillDashboard : UserControl, IIslandWidget
{
    private const double CardGap = 8;
    private const uint ThumbnailSize = 64;
    private bool _shelfExpanded;
    private bool _isDragOver;

    // Pre-resolved storage items for drag-out. Resolved when the shelf contents
    // change (NOT inside the drag handler) so StartDragAsync is invoked
    // synchronously from the pointer event — awaiting file resolution between
    // the pointer event and StartDragAsync can drop the user-gesture context
    // and fail the drag. Null until resolution succeeds.
    private List<IStorageItem>? _dragOutItems;

    // Bumped on every ItemsChanged; a rebuild only publishes its result if the
    // version is unchanged, so a slow rebuild can never clobber a newer cache
    // or resurrect items that were cleared while it was resolving.
    private int _shelfVersion;

    // Paths whose thumbnail load already produced no image (no shell thumbnail,
    // or the path no longer resolves). Prevents re-resolving the same item on
    // every shelf change (IO churn + error-log spam). Cleared when the shelf
    // empties, so a re-added file gets a fresh attempt.
    private readonly HashSet<string> _thumbnailAttemptedPaths = new();

    // ── IIslandWidget ────────────────────────────────────────────────────────
    public WidgetPriority Priority => WidgetPriority.Default;
    public bool AutoExpand => false;
    public WindowProfile PreferredProfile => WindowProfile.Collapsed;
    public void OnActivated() { }
    public void OnSuspended() { }
    public void OnResumed() { }
    public void OnDeactivated() { }

    // ── Width signal ─────────────────────────────────────────────────────────
    public event EventHandler<double>? TotalWidthChanged;

    /// <summary>True while the file shelf panel is expanded inline.</summary>
    public bool IsShelfExpanded => _shelfExpanded;

    // ── Construction ─────────────────────────────────────────────────────────
    public PillDashboard()
    {
        InitializeComponent();
        MusicCard.StateChanged += OnCardStateChanged;
        ShelfCard.StateChanged += OnCardStateChanged;
        ShelfCard.ShelfButtonClicked += OnShelfButtonClicked;
        App.FileShelfStore.ItemsChanged += OnShelfItemsChanged;
        UpdateCardVisibility();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TotalWidthChanged?.Invoke(this, ComputeTotalWidth());
        RefreshFileList();
        _ = RebuildDragOutCacheAsync();
        _ = LoadThumbnailsAsync();
    }

    // ── Card visibility ───────────────────────────────────────────────────────
    private void OnCardStateChanged(object? sender, EventArgs e)
    {
        UpdateCardVisibility();
        TotalWidthChanged?.Invoke(this, ComputeTotalWidth());
    }

    private void OnShelfItemsChanged(object? sender, EventArgs e)
    {
        _shelfVersion++;
        UpdateCardVisibility();
        TotalWidthChanged?.Invoke(this, ComputeTotalWidth());
        RefreshFileList();
        _ = RebuildDragOutCacheAsync();
        _ = LoadThumbnailsAsync();

        // Auto-collapse shelf panel if cleared
        if (App.FileShelfStore.IsEmpty)
        {
            // Fresh shelf: re-added files get a new thumbnail attempt.
            _thumbnailAttemptedPaths.Clear();
            if (_shelfExpanded)
                CollapseShelf();
        }
    }

    private void UpdateCardVisibility()
    {
        bool musicVisible = MusicCard.ShouldShow;
        bool shelfVisible = ShelfCard.ShouldShow;

        MusicCard.Visibility = musicVisible
            ? Visibility.Visible
            : Visibility.Collapsed;

        WeatherCard.Visibility = musicVisible
            ? Visibility.Collapsed
            : Visibility.Visible;

        ShelfCard.Visibility = shelfVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private double ComputeTotalWidth()
    {
        double total = MusicCard.ShouldShow
            ? MusicCard.CardWidth
            : WeatherCard.CardWidth;

        if (ShelfCard.ShouldShow)
            total += CardGap + ShelfCard.CardWidth;

        return total;
    }

    // ── Shelf expand/collapse ─────────────────────────────────────────────────
    private void OnShelfButtonClicked(object? sender, EventArgs e)
    {
        if (_shelfExpanded)
            CollapseShelf();
        else
            ExpandShelf();
    }

    private void ExpandShelf()
    {
        _shelfExpanded = true;
        VisualStateManager.GoToState(this, "ShelfExpanded", true);
        RefreshFileList();

        // The user can now drag the handle, so make sure the drag payload and
        // the first item's thumbnail (the drag ghost) are ready — otherwise a
        // fast first drag silently cancels or shows the generic ghost.
        _ = RebuildDragOutCacheAsync();
        _ = LoadThumbnailsAsync();

        // Grow window upward — same pattern as ClipboardWidget.ExpandWidget()
        var (width, _) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(width, 340);
    }

    private void CollapseShelf()
    {
        _shelfExpanded = false;
        VisualStateManager.GoToState(this, "ShelfCollapsed", true);

        var (width, height) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(width, height);
    }

    private void RefreshFileList()
    {
        // Rows are re-templated on every refresh — drop the stale selection ref.
        _selectedRow = null;
        if (FileList == null) return;
        FileList.ItemsSource = null;
        FileList.ItemsSource = App.FileShelfStore.Items;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        App.FileShelfStore.Clear();
    }

    // ── Drag handling ─────────────────────────────────────────────────────────
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(
                Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        if (App.FileShelfStore.IsFull) return;

        _isDragOver = true;
        e.AcceptedOperation =
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        DragOverlay.Opacity = 1;
        if (DragMessageStrip != null)
            DragMessageStrip.Visibility = Visibility.Visible;

        // Only grow pill slightly on drag-over, don't open full
        if (!_shelfExpanded)
        {
            var (width, _) = App.WindowService.CompactSize;
            App.WindowService.StartSizeAnimation(width, 80);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(
                Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            return;

        if (!App.FileShelfStore.IsFull)
            e.AcceptedOperation =
                Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (!_isDragOver) return;
        _isDragOver = false;

        DragOverlay.Opacity = 0;
        if (DragMessageStrip != null)
            DragMessageStrip.Visibility = Visibility.Collapsed;

        if (!_shelfExpanded)
        {
            var (width, height) = App.WindowService.CompactSize;
            App.WindowService.StartSizeAnimation(width, height);
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        _isDragOver = false;
        DragOverlay.Opacity = 0;
        if (DragMessageStrip != null)
            DragMessageStrip.Visibility = Visibility.Collapsed;

        if (!e.DataView.Contains(
                Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            // Nothing valid dropped — collapse back to pill
            if (_shelfExpanded)
                CollapseShelf();
            else
            {
                var (w, h) = App.WindowService.CompactSize;
                App.WindowService.StartSizeAnimation(w, h);
            }
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var item in items)
            App.FileShelfStore.TryAdd(item.Path);

        // Collapse back to pill after drop — shelf icon will appear
        // showing the count. User clicks shelf icon to see contents.
        CollapseShelf();
    }

    // ── Drag-out handle ───────────────────────────────────────────────────

    /// <summary>
    /// CanDrag initiation (system drag threshold; no manual pointer capture).
    /// Populates the DataPackage from the pre-resolved cache so the platform
    /// translates the StorageItems to CF_HDROP for Win32 targets (Explorer,
    /// desktop), and sets a custom ghost from the first staged item's real
    /// shell thumbnail — the default WinUI visual is a generic text box.
    /// </summary>
    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        try
        {
            var items = _dragOutItems;
            if (items == null || items.Count == 0)
            {
                args.Cancel = true;
                return;
            }

            args.Data.RequestedOperation =
                DataPackageOperation.Copy | DataPackageOperation.Move;
            args.AllowedOperations =
                DataPackageOperation.Copy | DataPackageOperation.Move;
            args.Data.SetStorageItems(items);

            // Real shell thumbnail as the drag ghost; falls back to the default
            // visual when the first item's thumbnail isn't loaded yet.
            var storeItems = App.FileShelfStore.Items;
            if (storeItems.Count > 0 && storeItems[0].Thumbnail is BitmapImage bmp)
                args.DragUI.SetContentFromBitmapImage(bmp);
        }
        catch (Exception ex)
        {
            // Fail soft: never let a drag-start exception escape the gesture.
            Logger.Error("PillDashboard: drag-out start failed", ex);
            args.Cancel = true;
        }
    }

    /// <summary>
    /// Fires when the drag ends. A completed drop (copy or move) means the
    /// files left the shelf.
    /// </summary>
    private void OnDropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        if ((args.DropResult & (DataPackageOperation.Copy | DataPackageOperation.Move)) != 0)
            App.FileShelfStore.Clear();
    }

    /// <summary>
    /// Resolves the currently staged files into IStorageItem objects so the
    /// drag-out handler can call StartDragAsync synchronously. Runs on the UI
    /// thread; paths that can no longer be resolved are skipped. Null when the
    /// shelf is empty or nothing could be resolved.
    /// </summary>
    private async Task RebuildDragOutCacheAsync()
    {
        _dragOutItems = null;
        int version = _shelfVersion;

        // Snapshot: the store can mutate between awaits (e.g. a drop lands while
        // we resolve), which would invalidate the live list's enumerator.
        var files = new List<StashedFile>(App.FileShelfStore.Items);
        if (files.Count == 0) return;

        var items = new List<IStorageItem>(files.Count);
        foreach (var f in files)
        {
            try
            {
                items.Add(f.IsFolder
                    ? await StorageFolder.GetFolderFromPathAsync(f.Path)
                    : await StorageFile.GetFileFromPathAsync(f.Path));
            }
            catch (Exception ex)
            {
                Logger.Error($"PillDashboard: drag-out resolve failed for '{f.Path}'", ex);
            }
        }

        // Publish only if the shelf didn't change while we were resolving.
        if (version != _shelfVersion) return;
        if (items.Count > 0)
            _dragOutItems = items;
    }

    // ── File list row interactions ───────────────────────────────────────────

    // Row hover highlight; fully transparent brush keeps the row hit-testable
    // (a null background would let taps fall through in the gaps between children).
    private static readonly SolidColorBrush RowHoverBrush = new(Windows.UI.Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush RowDefaultBrush = new(Windows.UI.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush RowSelectedBrush = CreateSelectedBrush();

    /// <summary>Row currently highlighted by single-tap selection; null when none.</summary>
    private Grid? _selectedRow;

    private static SolidColorBrush CreateSelectedBrush()
    {
        // Translucent accent tint for the selected row; neutral fallback when the
        // accent resource is unavailable.
        if (Application.Current.Resources.TryGetValue("AccentBrush", out var value) && value is SolidColorBrush accent)
            return new SolidColorBrush(accent.Color) { Opacity = 0.35 };
        return new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    private void OnFileRowPointerEntered(object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var row = (Grid)sender;
        if (!ReferenceEquals(row, _selectedRow))
            row.Background = RowHoverBrush;
    }

    private void OnFileRowPointerExited(object sender,
        Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var row = (Grid)sender;
        if (!ReferenceEquals(row, _selectedRow))
            row.Background = RowDefaultBrush;
    }

    /// <summary>Single tap selects/highlights the row (open is double-tap or Enter).</summary>
    private void OnFileRowTapped(object sender,
        Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;

        // Taps that land on the row's remove button belong to the button.
        if (IsInsideButton(e.OriginalSource)) return;

        if (sender is Grid row)
            SelectRow(row);
    }

    /// <summary>
    /// Double-tap opens the staged file/folder with its default handler.
    /// The shelf collapses once the item is launched (transient-island behavior).
    /// </summary>
    private async void OnFileRowDoubleTapped(object sender,
        Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true; // must be set before any await below

        // Double-taps that land on the row's remove button belong to the button.
        if (IsInsideButton(e.OriginalSource)) return;

        if (sender is FrameworkElement { DataContext: StashedFile file })
            await LaunchAndCollapseAsync(file);
    }

    private void SelectRow(Grid row)
    {
        if (ReferenceEquals(_selectedRow, row)) return;

        if (_selectedRow != null)
            _selectedRow.Background = RowDefaultBrush;

        _selectedRow = row;
        row.Background = RowSelectedBrush;
    }

    private async Task LaunchAndCollapseAsync(StashedFile file)
    {
        if (await TryLaunchAsync(file))
            CollapseShelf();
    }

    /// <summary>
    /// Removes the row's item from the shelf (auto-collapses when empty).
    /// </summary>
    private void OnFileRowRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StashedFile file })
            App.FileShelfStore.Remove(file.Path);
    }

    private static bool IsInsideButton(object originalSource)
    {
        for (var current = originalSource as DependencyObject;
             current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button) return true;
        }
        return false;
    }

    /// <summary>
    /// Opens the staged item with its default handler — files via
    /// LaunchFileAsync, folders via LaunchFolderAsync (Explorer). Same pattern
    /// as the original ExpandedDashboard File Shelf.
    /// </summary>
    private static async Task<bool> TryLaunchAsync(StashedFile file)
    {
        try
        {
            // Returns false when the user declines (no handler, cancelled prompt).
            return file.IsFolder
                ? await Windows.System.Launcher.LaunchFolderAsync(
                    await StorageFolder.GetFolderFromPathAsync(file.Path))
                : await Windows.System.Launcher.LaunchFileAsync(
                    await StorageFile.GetFileFromPathAsync(file.Path));
        }
        catch (Exception ex)
        {
            Logger.Error($"PillDashboard: launch failed for '{file.Path}'", ex);
            return false;
        }
    }

    // ── Shell thumbnails ─────────────────────────────────────────────────────

    /// <summary>
    /// Loads shell thumbnails for the staged files and rebinds the grid once at
    /// the end (x:Bind is OneTime, so the grid must be re-templated to pick up
    /// the freshly loaded thumbnails). Version-guarded like the drag-out cache:
    /// a slow load never paints stale items, and a superseded load skips the
    /// rebind (the newer batch owns it). Files without a shell thumbnail keep
    /// the Folder/Document glyph fallback.
    /// </summary>
    private async Task LoadThumbnailsAsync()
    {
        int version = _shelfVersion;
        var files = new List<StashedFile>(App.FileShelfStore.Items);
        bool loadedAny = false;

        foreach (var f in files)
        {
            if (version != _shelfVersion) return;
            if (f.Thumbnail != null) continue;
            if (!_thumbnailAttemptedPaths.Add(f.Path)) continue; // already known icon-less/unresolvable

            var thumbnail = await TryLoadThumbnailAsync(f);
            if (thumbnail != null)
            {
                f.Thumbnail = thumbnail;
                loadedAny = true;
            }
        }

        // Rebind so the OneTime x:Bind sees the thumbnails we just loaded —
        // only when something actually changed (the ItemsChanged refresh already
        // showed the glyph fallback for icon-less items).
        if (version == _shelfVersion && loadedAny)
            RefreshFileList();
    }

    private static async Task<BitmapImage?> TryLoadThumbnailAsync(StashedFile f)
    {
        try
        {
            // Same decode pattern as MediaWidgetViewModel.LoadThumbnailAsync:
            // read the stream inside SetSourceAsync, then dispose it.
            using var thumbnail = f.IsFolder
                ? await (await StorageFolder.GetFolderFromPathAsync(f.Path))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbnailSize)
                : await (await StorageFile.GetFileFromPathAsync(f.Path))
                    .GetThumbnailAsync(ThumbnailMode.SingleItem, ThumbnailSize);

            if (thumbnail == null) return null;

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.Error($"PillDashboard: thumbnail load failed for '{f.Path}'", ex);
            return null;
        }
    }

    private void OnShelfPanelPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnShelfPanelTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }
}