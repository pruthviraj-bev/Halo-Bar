using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DynamicIsland.Helpers;
using DynamicIsland.Interfaces;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace DynamicIsland.Widgets;

public sealed partial class PillDashboard : UserControl, IIslandWidget
{
    private const double CardGap = 8;
    private const uint ThumbnailSize = 64;
    // PASS 56: floating "Drop here" popup geometry (DIP). These are the single
    // source of truth for the chip and the popup region target height; the
    // chip's Height/Margin are applied in the constructor from these.
    private const double DropPopupChipWidthDip = 200;
    private const double DropPopupChipHeightDip = 60;
    private const double DropPopupGapDip = 4;
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
        // PASS 56: popup chip geometry from the code constants (single source of
        // truth shared with the region target-height math).
        DropHerePopup.Width = DropPopupChipWidthDip;
        DropHerePopup.Height = DropPopupChipHeightDip;
        DropHerePopup.Margin = new Thickness(0, 0, 0, DropPopupGapDip);
        MusicCard.StateChanged += OnCardStateChanged;
        ShelfCard.StateChanged += OnCardStateChanged;
        ShelfCard.ShelfButtonClicked += OnShelfButtonClicked;
        App.FileShelfStore.ItemsChanged += OnShelfItemsChanged;
        UpdateCardVisibility();
        Loaded += OnLoaded;
        // PASS 9: keep the window strip hugging the pill's measured width — a
        // fixed CardWidth leaves variable slack on the right because the
        // title/artist widths change per track.
        CardStripBorder.LayoutUpdated += OnCardStripLayoutUpdated;
    }

    // Last width published to the window strip (see OnCardStripLayoutUpdated).
    private double _lastReportedStripWidth = double.NegativeInfinity;

    /// <summary>
    /// PASS 9: re-reports the outer pill's ACTUAL rendered width whenever it
    /// changes (track change → different title width). The hysteresis guard
    /// (2 DIP) prevents an animation feedback loop: resizing the strip does not
    /// change the pill's own content width, so once the strip matches, no
    /// further reports fire. CardStripBorder.ActualWidth already includes the
    /// shelf card when it is visible.
    /// </summary>
    private void OnCardStripLayoutUpdated(object? sender, object e)
    {
        if (CardStripBorder == null || CardStripBorder.ActualWidth <= 1) return;
        if (Math.Abs(CardStripBorder.ActualWidth - _lastReportedStripWidth) < 2.0) return;
        _lastReportedStripWidth = CardStripBorder.ActualWidth;
        TotalWidthChanged?.Invoke(this, CardStripBorder.ActualWidth);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // PASS 9: outer pill height = live taskbar height − 2 DIP (dynamic, never
        // hardcoded). Re-applied whenever the card strip re-lays out so a
        // taskbar-height change that reaches WindowService is picked up.
        ApplyCardStripPillHeight();
        TotalWidthChanged?.Invoke(this, ComputeTotalWidth());
        RefreshFileList();
        _ = RebuildDragOutCacheAsync();
        _ = LoadThumbnailsAsync();
    }

    private void ApplyCardStripPillHeight()
    {
        if (CardStripBorder == null) return;
        double taskbarHeight = Math.Max(App.WindowService.TaskbarHeightDips, 1);
        CardStripBorder.Height = Math.Max(taskbarHeight - 2, 1);

        // PASS 9 (final): content area height = taskbar − 10 DIP
        //  outer pill  = taskbar − 2
        //  inner       = outer − 4  (2 DIP padding each side)
        //  content     = inner − 4  (2 DIP inner padding each side)
        // MusicPillCard scales its ContentGrid + album art to this, so the
        // content fills the pill instead of measuring at its tiny desired size
        // (the old StackPanel root cause).
        double contentHeight = Math.Max(taskbarHeight - 10, 12);
        if (MusicCard != null)
            MusicCard.ContentAreaHeight = contentHeight;
    }

    // ── Card visibility ───────────────────────────────────────────────────────
    private void OnCardStateChanged(object? sender, EventArgs e)
    {
        UpdateCardVisibility();
        ApplyCardStripPillHeight();
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
        // PASS 9: once laid out, report the ACTUAL outer-pill width so the
        // window strip hugs the pill instead of leaving acrylic slack on the
        // right. Falls back to the fixed card widths before the first layout.
        if (CardStripBorder != null && CardStripBorder.ActualWidth > 1)
            return CardStripBorder.ActualWidth;

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

    // ── Drag handling (PASS 37 + PASS 38 + PASS 54 + PASS 56) ────────────────
    // Drag-and-drop hover over the pill shows ONLY the small floating "Drop
    // here" popup ABOVE the pill (DropHerePopup) — the File Shelf is never
    // expanded by hover alone and the compact pill content stays untouched.
    // Drop is the single point where files are staged (then the shelf's normal
    // icon/count behavior applies).
    //
    // PASS 38 (GOAL 2) layer forensics: the real Explorer drag is a WinUI/
    // Windows drag lifecycle (OLE DragEnter/DragOver/Drop), NOT mouse hover.
    // Every event is logged as [DRAG] with cursor / data formats /
    // containsStorageItems / shelf state / hwnd+window rect so the failing
    // layer (Explorer → Windows drag manager → Halo HWND → XAML drop target →
    // ExpandShelf) is attributed by evidence. The StorageItems gate is relaxed
    // to any shell-file format because real Explorer drags are known to arrive
    // without StorageItems synthesis in unpackaged WinUI 3 apps, and drop-path
    // resolution prefers the OLE FileDrop format over the flaky
    // GetStorageItemsAsync (microsoft/microsoft-ui-xaml#9296).
    // True when the current expansion was opened BY DRAG (vs. the shelf icon
    // button) — drag-leave must close a drag-opened shelf but must NOT close a
    // button-opened one. (PASS 54: drags no longer open the shelf, so this only
    // guards the pre-existing button-opened state.)
    private bool _dragOpenedShelf;
    private int _dragOverLogCount;

    private static void LogDrag(string evt, DragEventArgs e, bool hasStorage, bool shelfExpanded, bool dragOver, bool allowDrop)
    {
        try
        {
            var pt = e.GetPosition(null); // app-window coordinates
            string formats = string.Join(",", e.DataView.AvailableFormats);
            bool hasFileDrop = formats.Contains("FileDrop", StringComparison.OrdinalIgnoreCase);
            bool hasShellIdList = formats.Contains("Shell IDList Array", StringComparison.OrdinalIgnoreCase);
            // PASS 39 (GOAL 2): HWND-level hit-test attribution — cursor pixel
            // owner, halo hwnd, insideHaloRegion/insidePillRegion — so the drag
            // decision tree (hit-test → OLE registration → data formats) can be
            // answered from a single line.
            string hitTest = App.Window.DescribeDragHitTest();
            Logger.Info($"[P39-DRAG] event={evt} cursor=({pt.X:F0},{pt.Y:F0}) dataFormats=[{formats}] " +
                        $"hasStorageItems={hasStorage} hasFileDrop={hasFileDrop} hasShellIdList={hasShellIdList} " +
                        $"allowDrop={allowDrop} shelfExpanded={shelfExpanded} dragOver={dragOver} " +
                        $"{hitTest}");
        }
        catch (Exception ex)
        {
            Logger.Error("[P39-DRAG] log failed", ex);
        }
    }

    // ── PASS 56: floating "Drop here" popup ──────────────────────────────────
    // During an external file-drag hover the Halo shows a small popup ABOVE the
    // compact pill. Geometry uses the existing popup system: the pill region
    // grows upward from the taskbar strip (StartSizeAnimation → the MainWindow
    // popup stage) while the compact pill stays anchored and its content stays
    // visible; the popup chip is an in-flow StackPanel row that occupies the
    // revealed band. The chip also plays a small in/out animation (opacity
    // 0→1, scale 0.92→1, translateY down→0).

    /// <summary>
    /// Shows the floating popup. DragEnter must NOT open the File Shelf, expand
    /// the dashboard, or replace the pill content — it only reveals the popup
    /// and grows the pill region by the chip height.
    /// </summary>
    private void ShowDropHerePopup()
    {
        _isDragOver = true;
        _dragOpenedShelf = !_shelfExpanded;
        if (App.FileShelfStore.IsFull) return;
        // The popup belongs to the compact pill — never show it (or resize the
        // region) while the dashboard is expanded, where the pill is hidden.
        if (App.IslandController.IsExpanded) return;
        // An open shelf occupies the band above the pill — never popup over it.
        if (_shelfExpanded) return;

        DropHerePopup.Visibility = Visibility.Visible;
        AnimateDropHerePopup(show: true);

        var (w, h) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(
            w, h + (int)DropPopupChipHeightDip + (int)DropPopupGapDip);
    }

    /// <summary>
    /// Hides the floating popup and returns the pill region to the compact
    /// strip. A shelf the user opened with the icon button is left alone.
    /// </summary>
    private void HideDropHerePopup()
    {
        _isDragOver = false;
        if (DropHerePopup.Visibility != Visibility.Visible) return;

        AnimateDropHerePopup(show: false);

        // Never shrink an expanded dashboard; the chip is only shown while the
        // compact pill is the base object, so this never collapses an
        // icon-opened shelf either.
        if (App.IslandController.IsExpanded) return;
        var (w, h) = App.WindowService.CompactSize;
        App.WindowService.StartSizeAnimation(w, h);
    }

    private Storyboard? _dropPopupStoryboard;

    private void AnimateDropHerePopup(bool show)
    {
        _dropPopupStoryboard?.Stop();
        _dropPopupStoryboard = null;

        var storyboard = new Storyboard();
        double ms = 160;

        var opacity = new DoubleAnimation
        {
            From = show ? 0.0 : 1.0,
            To = show ? 1.0 : 0.0,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacity, DropHerePopup);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);

        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var scale = new DoubleAnimation
            {
                From = show ? 0.92 : 1.0,
                To = show ? 1.0 : 0.92,
                Duration = TimeSpan.FromMilliseconds(ms),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(scale, DropHerePopupTransform);
            Storyboard.SetTargetProperty(scale, axis);
            storyboard.Children.Add(scale);
        }

        var translateY = new DoubleAnimation
        {
            From = show ? 10.0 : 0.0,
            To = show ? 0.0 : 10.0,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(translateY, DropHerePopupTransform);
        Storyboard.SetTargetProperty(translateY, "TranslateY");
        storyboard.Children.Add(translateY);

        if (!show)
            storyboard.Completed += OnDropPopupHideCompleted;

        _dropPopupStoryboard = storyboard;
        storyboard.Begin();
    }

    private void OnDropPopupHideCompleted(object? sender, object e)
    {
        _dropPopupStoryboard = null;
        if (!_isDragOver)
            DropHerePopup.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// PASS 38 (GOAL 2): accepts Explorer file/folder drags regardless of
    /// whether WinRT surfaces them as StorageItems — real Explorer drags can
    /// arrive with only the OLE formats (FileDrop / Shell IDList Array) when
    /// StorageItems synthesis is unavailable. All three mean "a shell item
    /// drag" and should open the shelf.
    /// </summary>
    private static bool DragCarriesFiles(DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems)) return true;
        foreach (var fmt in e.DataView.AvailableFormats)
        {
            if (fmt.Equals("FileDrop", StringComparison.OrdinalIgnoreCase)
                || fmt.Equals("Shell IDList Array", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// PASS 38 (GOAL 2): resolves the dropped paths robustly. WinRT's
    /// GetStorageItemsAsync is known to be unreliable for Explorer drags in
    /// WinUI 3 (#9296) — prefer the OLE FileDrop format (CF_HDROP → string[]),
    /// fall back to StorageItems.
    /// </summary>
    private static async Task<List<string>> ResolveDroppedPathsAsync(DataPackageView view)
    {
        var paths = new List<string>();
        try
        {
            if (view.Contains("FileDrop") && await view.GetDataAsync("FileDrop") is string[] drop)
                paths.AddRange(drop);
            if (paths.Count == 0)
            {
                var items = await view.GetStorageItemsAsync();
                foreach (var item in items)
                    paths.Add(item.Path);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG] dropped-path resolution failed", ex);
        }
        return paths;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        LogDrag("enter", e, e.DataView.Contains(StandardDataFormats.StorageItems), _shelfExpanded, _isDragOver, RootGrid.AllowDrop);
        if (!DragCarriesFiles(e)) return;

        if (App.FileShelfStore.IsFull) return;

        e.AcceptedOperation =
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        // PASS 54 + PASS 56: drag-hover over the pill shows ONLY the small
        // floating "Drop here" popup above the pill — the File Shelf must NOT
        // expand merely because an item hovers. It opens only when files are
        // actually dropped.
        ShowDropHerePopup();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // DragOver fires many times per second — log the first one per session
        // and then only in verbose mode (HALO_P38_DRAGLOG=1 / HALO_P39_DRAGLOG=1).
        bool hasStorage = e.DataView.Contains(StandardDataFormats.StorageItems);
        if (Helpers.MotionDiagnostics.EnableP38DragLog
            || Helpers.MotionDiagnostics.EnableP39DragLog
            || (++_dragOverLogCount % 20) == 1)
            LogDrag("over", e, hasStorage, _shelfExpanded, _isDragOver, RootGrid.AllowDrop);

        if (!DragCarriesFiles(e)) return;

        if (App.FileShelfStore.IsFull) return;

        e.AcceptedOperation =
            Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        // PASS 54: hover never expands the shelf — the "Drop here" popup
        // (already shown by OnDragEnter) is the only hover visual.
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        LogDrag("leave", e, e.DataView.Contains(StandardDataFormats.StorageItems), _shelfExpanded, _isDragOver, RootGrid.AllowDrop);
        if (!_isDragOver) return;
        _isDragOver = false;

        HideDropHerePopup();

        // Close only a shelf that was opened by the drag itself; a shelf the
        // user opened with the icon button stays open.
        if (_dragOpenedShelf && _shelfExpanded)
            CollapseShelf();
        _dragOpenedShelf = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        LogDrag("drop", e, e.DataView.Contains(StandardDataFormats.StorageItems), _shelfExpanded, _isDragOver, RootGrid.AllowDrop);
        _isDragOver = false;
        _dragOpenedShelf = false;
        HideDropHerePopup();

        var paths = await ResolveDroppedPathsAsync(e.DataView);
        if (paths.Count == 0)
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

        foreach (var path in paths)
            App.FileShelfStore.TryAdd(path);

        // Collapse back to pill after drop — shelf icon will appear
        // showing the count. User clicks shelf icon to see contents.
        CollapseShelf();
    }

    // ── PASS 47 (GOAL 2) — native OLE drag signal entry points ─────────────
    // Explorer file drags do not surface as XAML DragEnter on this window, so
    // WindowService's OLE drop target calls these instead of OnDragEnter etc.
    // They mirror the XAML handlers minus the event args: the ENTER signal is
    // accepted without resolving the payload (QueryGetData only), the DROP is
    // the single place that resolves it.

    /// <summary>
    /// Native OLE drag-enter signal. Mirrors <see cref="OnDragEnter"/>: accepts
    /// any shell-file drag. PASS 54/PASS 56: this must NOT mutate the shelf's
    /// expanded state — it shows only the transient floating "Drop here" popup
    /// above the compact pill. The File Shelf opens only when files are
    /// actually dropped. The payload is NOT resolved here.
    /// </summary>
    public void NotifyExternalDragEnter()
    {
        Logger.Info($"[SHELF-EXT] NotifyExternalDragEnter isDragOver={_isDragOver} shelfExpanded={_shelfExpanded} full={App.FileShelfStore.IsFull}");
        if (App.FileShelfStore.IsFull) return;
        if (_isDragOver) return;

        ShowDropHerePopup();
    }

    /// <summary>
    /// Native OLE drag-leave signal. Mirrors <see cref="OnDragLeave"/>: closes
    /// only a shelf that the drag itself opened.
    /// </summary>
    public void NotifyExternalDragLeave()
    {
        Logger.Info($"[SHELF-EXT] NotifyExternalDragLeave isDragOver={_isDragOver} shelfExpanded={_shelfExpanded}");
        if (!_isDragOver) return;
        _isDragOver = false;

        HideDropHerePopup();

        if (_dragOpenedShelf && _shelfExpanded)
            CollapseShelf();
        _dragOpenedShelf = false;
    }

    /// <summary>
    /// Native OLE drop completion. Mirrors <see cref="OnDrop"/> with the paths
    /// already resolved by the OLE target: adds them to the shelf and collapses
    /// back to the pill.
    /// </summary>
    public void AcceptExternalDrop(string[] paths)
    {
        Logger.Info($"[SHELF-EXT] AcceptExternalDrop pathCount={paths.Length} shelfExpanded={_shelfExpanded}");
        _isDragOver = false;
        _dragOpenedShelf = false;
        HideDropHerePopup();

        if (paths.Length == 0)
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

        foreach (var path in paths)
            App.FileShelfStore.TryAdd(path);

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