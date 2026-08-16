using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using IDropTarget = DynamicIsland.Helpers.OleDropTargetInterop.IDropTarget;
using IDataObject = DynamicIsland.Helpers.OleDropTargetInterop.IDataObject;
using POINTL = DynamicIsland.Helpers.OleDropTargetInterop.POINTL;
using FORMATETC = DynamicIsland.Helpers.OleDropTargetInterop.FORMATETC;
using STGMEDIUM = DynamicIsland.Helpers.OleDropTargetInterop.STGMEDIUM;

namespace DynamicIsland.Helpers;

/// <summary>
/// PASS 47 (GOAL 2): native OLE drop target for the Halo window.
///
/// WinUI 3's XAML AllowDrop path does not deliver Explorer drags on this
/// window: the XAML tree is hosted in a Microsoft.UI.Content.DesktopChildSiteBridge
/// child inside a fixed 1000x890 layered, region-clipped HWND, so the OLE
/// DragEnter/DragOver stream never surfaces as XAML DragEnter on the pill.
/// Rather than chase the XAML pipeline, this registers a REAL OLE IDropTarget
/// on the Halo HWND and its bridge child — whichever HWND the OLE hit-test
/// resolves — and routes the drag SIGNAL to the File Shelf.
///
/// .NET Core no longer ships ComTypes.IDropTarget/POINTL (they were dropped
/// with the .NET Framework OLE interop), so the OLE surface is declared in
/// <see cref="OleDropTargetInterop"/> with self-contained [ComImport] interfaces
/// and [StructLayout] structs whose GUIDs/vtable order match the OS contracts.
///
/// Diagnostics: PASS 47 is gated on OBSERVATION, not assumption. Every register
/// logs its HWND identity ([DRAG-REGISTER]); every native IDropTarget method
/// logs at its FIRST LINE ([DRAG-OLE]), before any payload/region/async work,
/// so "COM never called us", "we rejected the payload", and "we accepted but
/// the shelf visual failed" are distinguishable. DragEnter/DragOver also log
/// the window actually under the cursor ([DRAG-HIT]) and whether it matches the
/// registered HWNDs ([DRAG-MATCH]).
/// </summary>
internal sealed class OleDropTarget
{
    private const short CF_HDROP = 15;
    private const int DVASPECT_CONTENT = 1;
    private const int TYMED_HGLOBAL = 1;
    private const int DROPEFFECT_NONE = 0;
    private const int DROPEFFECT_COPY = 1;
    private const uint DragQueryFileCount = 0xFFFFFFFF;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint GA_ROOT = 2;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Dictionary<IntPtr, RegisteredTarget> _adapters = new();
    private readonly short _shellIdListFormat;
    private readonly short _fileDropFormat;
    private readonly short _fileGroupDescriptorFormat;
    private readonly short _storageItemsFormat;
    private readonly int _creationThreadId;
    private IntPtr _mainHaloHwnd = IntPtr.Zero;
    private bool _inside;
    private bool _sessionAccepted;
    // Reroute tolerance: OLE can fire DragLeave on the overlay then DragEnter
    // on the bridge (or vice versa) when the cursor crosses between the two
    // registered HWNDs mid-drag (e.g. moving from the pill strip into the
    // "Drop here" popup band). The leave signal is debounced so a genuine
    // exit still closes the popup while a reroute (followed by DragEnter)
    // keeps it open instead of flickering it closed.
    private DispatcherQueueTimer? _dragLeaveTimer;
    private int _dragLeaveToken;    // bumped on every enter/leave — generation
    private int _pendingLeaveToken; // generation captured when the timer armed

    /// <summary>Raised (UI thread) when a shell-file drag enters the Halo drop target.</summary>
    public event EventHandler? FileDragEntered;

    /// <summary>Raised (UI thread) when the shell-file drag leaves the Halo drop target.</summary>
    public event EventHandler? FileDragLeft;

    /// <summary>Raised (UI thread) with the resolved file/folder paths when a drop completes.</summary>
    public event EventHandler<string[]>? FilesDropped;

    public OleDropTarget(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _creationThreadId = Environment.CurrentManagedThreadId;
        _shellIdListFormat = RegisterClipboardFormatW("Shell IDList Array");
        _fileDropFormat = RegisterClipboardFormatW("FileDrop");
        _fileGroupDescriptorFormat = RegisterClipboardFormatW("FileGroupDescriptorW");
        _storageItemsFormat = RegisterClipboardFormatW("StorageItems");
        Logger.Info($"[DRAG-OLE-LIFETIME] targetCreated threadId={_creationThreadId}");
    }

    /// <summary>
    /// Ensures the calling (UI) thread is OLE-initialized so RegisterDragDrop can
    /// succeed. Idempotent; safe to call before every RegisterDragDrop.
    /// </summary>
    public static void EnsureOleInitialized()
    {
        if (Interlocked.CompareExchange(ref _oleInitState, 1, 0) != 0) return;
        int threadId = Environment.CurrentManagedThreadId;
        int hr = OleInitialize(IntPtr.Zero);
        // S_OK / S_FALSE (already STA-initialized) are success; RPC_E_CHANGED_MODE
        // (already MTA-initialized) cannot be fixed here — RegisterDragDrop's own
        // return code will surface it in the [DRAG-REGISTER] log.
        Logger.Info($"[DRAG-OLE-THREAD] threadId={threadId} oleInitialized=true hr=0x{hr:X8}");
    }

    private static int _oleInitState;

    /// <summary>
    /// Registers this target on the given HWND exactly once. Replaces any prior
    /// registration on that HWND. <paramref name="mainHaloHwnd"/> is the owning
    /// top-level Halo HWND, used for register/hit diagnostics.
    /// </summary>
    public void Register(IntPtr hwnd, IntPtr mainHaloHwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (_mainHaloHwnd == IntPtr.Zero) _mainHaloHwnd = mainHaloHwnd;

        // STEP 12 — never over-register: one adapter per HWND, registered once.
        if (_adapters.ContainsKey(hwnd))
        {
            Logger.Info($"[DRAG-REGISTER] hwnd=0x{hwnd.ToInt64():X} already registered — skipped");
            return;
        }

        // STEP 1 — identify each registered HWND explicitly (never assume which
        // hwnd is the bridge or that the second registration is the bridge).
        var adapter = new RegisteredTarget(hwnd, this);
        string className = GetClassNameOf(hwnd);
        string title = GetWindowTitleOf(hwnd);
        GetWindowRect(hwnd, out RECT r);
        IntPtr parent = GetParent(hwnd);
        IntPtr root = GetAncestor(hwnd, GA_ROOT);
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        bool isBridge = className.Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase);
        bool isMain = hwnd == _mainHaloHwnd;

        int hr = RegisterDragDrop(hwnd, adapter);
        if (hr == 0) _adapters[hwnd] = adapter;

        Logger.Info(
            "[DRAG-REGISTER] " +
            $"hwnd=0x{hwnd.ToInt64():X} class=\"{className}\" title=\"{title}\" " +
            $"rect=({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}) " +
            $"parent=0x{parent.ToInt64():X} root=0x{root.ToInt64():X} " +
            $"visible={IsWindowVisible(hwnd)} enabled={IsWindowEnabled(hwnd)} " +
            $"exStyle=0x{exStyle:X8} transparent={(exStyle & WS_EX_TRANSPARENT) != 0} " +
            $"layered={(exStyle & WS_EX_LAYERED) != 0} " +
            $"noActivate={(exStyle & WS_EX_NOACTIVATE) != 0} " +
            $"toolWindow={(exStyle & WS_EX_TOOLWINDOW) != 0} " +
            $"topmost={(exStyle & WS_EX_TOPMOST) != 0} " +
            $"isMainHalo={isMain} isDesktopChildSiteBridge={isBridge} " +
            $"registerHr=0x{hr:X8} registered={hr == 0}");
        Logger.Info($"[DRAG-OLE-THREAD] registerThreadId={Environment.CurrentManagedThreadId} " +
                    $"uiThreadId={_creationThreadId}");
        Logger.Info($"[DRAG-OLE-LIFETIME] targetRegistered registeredCount={_adapters.Count}");
    }

    /// <summary>Revokes this target from every HWND it registered on.</summary>
    public void RevokeAll()
    {
        foreach (var hwnd in _adapters.Keys)
            RevokeDragDrop(hwnd);
        Logger.Info($"[DRAG-OLE-LIFETIME] targetRevoked count={_adapters.Count}");
        _adapters.Clear();
    }

    /// <summary>Every HWND currently registered as a drop target.</summary>
    public ICollection<IntPtr> RegisteredHwnds => _adapters.Keys;

    /// <summary>PASS 53: true while the current drag session is accepted
    /// (pointer over the pill / drag-grown region). Lets the overlay stay up
    /// across the collapsed→expanded growth during a drop session.</summary>
    internal bool IsDragActive => _sessionAccepted;

    // ── Native IDropTarget entry points (delegated from RegisteredTarget) ──

    internal int OnDragEnter(IntPtr hwnd, IDataObject pDataObj, uint grfKeyState, POINTL pt, ref int pdwEffect)
    {
        // STEP 2 — first line, before ANY payload/region/async work.
        Logger.Info($"[DRAG-OLE] DragEnter hwnd=0x{hwnd.ToInt64():X} pt=({pt.x},{pt.y}) effectIn=0x{pdwEffect:X8}");
        LogDragHit(hwnd, pt);
        LogDragMatch(hwnd, pt);
        // A DragEnter after a DragLeave is a reroute (overlay↔bridge), not a
        // genuine exit — cancel any pending debounced leave signal.
        _dragLeaveToken++;
        bool payload = HasShellFilePayload(pDataObj);
        bool insideRegion = IsInsideRegion(pt);
        bool insidePill = IsInsidePill(pt);
        // PASS 53: accept ONLY while the point is on the compact pill (or the
        // region that grew from it during a drag). Accepting at any region point
        // would open the File Shelf for drags over the expanded dashboard
        // (acceptance gate; the overlay is the native hit-test fix).
        // PASS 15: while the "Drop here" popup is up, the band ABOVE the pill
        // is a drop target too — the popup band is part of the live region and
        // accepting there keeps the popup open (instead of OLE rerouting to the
        // bridge, re-deriving insidePill=false, and closing it).
        bool accept = payload && IsInsideDropTarget(pt);
        _sessionAccepted = accept;
        pdwEffect = accept ? DROPEFFECT_COPY : DROPEFFECT_NONE;

        if (accept && !_inside)
        {
            _inside = true;
            _dispatcherQueue.TryEnqueue(() => FileDragEntered?.Invoke(this, EventArgs.Empty));
        }
        else if (!accept && _inside)
        {
            _inside = false;
            _dispatcherQueue.TryEnqueue(() => FileDragLeft?.Invoke(this, EventArgs.Empty));
        }

        Logger.Info($"[DRAG-EFFECT] DragEnter effect=0x{pdwEffect:X8} acceptedPayload={payload} " +
                    $"insideRegion={insideRegion} insidePill={IsInsidePill(pt)}");
        return 0;
    }

    internal int OnDragOver(IntPtr hwnd, uint grfKeyState, POINTL pt, ref int pdwEffect)
    {
        Logger.Info($"[DRAG-OLE] DragOver hwnd=0x{hwnd.ToInt64():X} pt=({pt.x},{pt.y}) effectIn=0x{pdwEffect:X8}");
        LogDragHit(hwnd, pt);

        // STEP 9 — preserve COPY while inside; collapse as soon as the cursor
        // leaves the Halo region. OLE DragOver is the authoritative signal.
        // Payload acceptance is session-stable (proven once at DragEnter); the
        // region is the per-frame hover authority.
        bool insideRegion = IsInsideRegion(pt);
        bool accept = _sessionAccepted && insideRegion;
        pdwEffect = accept ? DROPEFFECT_COPY : DROPEFFECT_NONE;

        if (_inside && !insideRegion)
        {
            _inside = false;
            _dispatcherQueue.TryEnqueue(() => FileDragLeft?.Invoke(this, EventArgs.Empty));
        }
        else if (!_inside && insideRegion && _sessionAccepted)
        {
            _inside = true;
            _dispatcherQueue.TryEnqueue(() => FileDragEntered?.Invoke(this, EventArgs.Empty));
        }

        Logger.Info($"[DRAG-EFFECT] DragOver effect=0x{pdwEffect:X8} insideRegion={insideRegion} insidePill={IsInsidePill(pt)}");
        return 0;
    }

    internal int OnDragLeave(IntPtr hwnd)
    {
        Logger.Info($"[DRAG-OLE] DragLeave hwnd=0x{hwnd.ToInt64():X}");
        if (_inside)
        {
            _inside = false;
            // Debounced so a cross-HWND reroute (DragLeave here, then DragEnter
            // on the other registered window) does not close the popup. A
            // genuine exit still signals ~100 ms later.
            ArmDragLeaveDebounce();
        }
        return 0;
    }

    /// <summary>Signals FileDragLeft after a short grace period, unless a
    /// DragEnter (reroute) arrives first and bumps the generation.</summary>
    private void ArmDragLeaveDebounce()
    {
        int token = ++_dragLeaveToken;
        _pendingLeaveToken = token;
        if (_dragLeaveTimer == null)
        {
            _dragLeaveTimer = _dispatcherQueue.CreateTimer();
            _dragLeaveTimer.Interval = TimeSpan.FromMilliseconds(100);
            _dragLeaveTimer.IsRepeating = false;
            _dragLeaveTimer.Tick += (_, _) =>
            {
                if (_dragLeaveToken == _pendingLeaveToken)
                {
                    _dragLeaveToken++;
                    _dispatcherQueue.TryEnqueue(() => FileDragLeft?.Invoke(this, EventArgs.Empty));
                }
            };
        }
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    internal int OnDrop(IntPtr hwnd, IDataObject pDataObj, uint grfKeyState, POINTL pt, ref int pdwEffect)
    {
        Logger.Info($"[DRAG-OLE] Drop hwnd=0x{hwnd.ToInt64():X} pt=({pt.x},{pt.y}) effectIn=0x{pdwEffect:X8}");
        bool wasInside = _inside;
        _inside = false;
        _sessionAccepted = false;
        pdwEffect = wasInside ? DROPEFFECT_COPY : DROPEFFECT_NONE;

        string[] paths = ExtractPaths(pDataObj);
        Logger.Info($"[DRAG] Drop resolved {paths.Length} path(s); effect=0x{pdwEffect:X8}");
        _dispatcherQueue.TryEnqueue(() => FilesDropped?.Invoke(this, paths));
        return 0;
    }

    // ── STEP 3/4 — window under the cursor vs the registered set ──────────

    private void LogDragHit(IntPtr hwnd, POINTL pt)
    {
        try
        {
            var screen = new POINTL { x = pt.x, y = pt.y };
            IntPtr wfp = WindowFromPoint(screen);
            IntPtr root = GetAncestor(wfp, GA_ROOT);
            IntPtr child = ChildWindowFromPoint(GetAncestor(hwnd, GA_ROOT), new POINTL
            {
                x = pt.x - ClientOriginX(GetAncestor(hwnd, GA_ROOT)),
                y = pt.y - ClientOriginY(GetAncestor(hwnd, GA_ROOT)),
            });

            GetWindowRect(wfp, out RECT wfpRect);
            string wfpClass = GetClassNameOf(wfp);
            string rootClass = GetClassNameOf(root);
            bool insideHaloRegion = IsInsideRegion(pt);
            bool insidePill = IsInsidePill(pt);

            // POINTL from IDropTarget is SCREEN coordinates — no origin subtraction
            // beyond the explicit ChildWindowFromPoint client conversion above.
            Logger.Info(
                "[DRAG-HIT] " +
                $"screen=({pt.x},{pt.y}) " +
                $"window=0x{wfp.ToInt64():X} windowClass=\"{wfpClass}\" " +
                $"wfpRect=({wfpRect.Left},{wfpRect.Top},{wfpRect.Right - wfpRect.Left}x{wfpRect.Bottom - wfpRect.Top}) " +
                $"childWindowFromPoint=0x{child.ToInt64():X} " +
                $"root=0x{root.ToInt64():X} rootClass=\"{rootClass}\" " +
                $"mainHalo=0x{_mainHaloHwnd.ToInt64():X} bridge=0x{FindBridgeHwnd().ToInt64():X} " +
                $"insideHaloRegion={insideHaloRegion} insidePill={insidePill}");
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG-HIT] failed", ex);
        }
    }

    private void LogDragMatch(IntPtr hwnd, POINTL pt)
    {
        try
        {
            IntPtr wfp = WindowFromPoint(new POINTL { x = pt.x, y = pt.y });
            bool isRegisteredMain = wfp == _mainHaloHwnd;
            bool isRegisteredBridge = wfp != IntPtr.Zero && RegisteredHwnds.Contains(wfp) &&
                                      GetClassNameOf(wfp).Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase);
            bool inSet = wfp != IntPtr.Zero && RegisteredHwnds.Contains(wfp);
            Logger.Info(
                "[DRAG-MATCH] " +
                $"actual=0x{wfp.ToInt64():X} registeredMain={isRegisteredMain} " +
                $"registeredBridge={isRegisteredBridge} actualInRegisteredSet={inSet} " +
                $"actualClass=\"{GetClassNameOf(wfp)}\" actualRoot=0x{GetAncestor(wfp, GA_ROOT).ToInt64():X}");
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG-MATCH] failed", ex);
        }
    }

    private IntPtr FindBridgeHwnd()
    {
        foreach (var hwnd in RegisteredHwnds)
            if (GetClassNameOf(hwnd).Contains("DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase))
                return hwnd;
        return IntPtr.Zero;
    }

    private bool IsInsideRegion(POINTL pt) =>
        App.Window.IsPointInCurrentRegion(pt.x, pt.y);

    private bool IsInsidePill(POINTL pt)
    {
        try
        {
            (int X, int Y, int W, int H)? rect = App.Window.GetPillScreenRect();
            if (rect == null) return false;
            return pt.x >= rect.Value.X && pt.x <= rect.Value.X + rect.Value.W
                && pt.y >= rect.Value.Y && pt.y <= rect.Value.Y + rect.Value.H;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// PASS 15: the drop-target shape is the compact pill (PASS 53 — never
    /// accept over the expanded dashboard). While the "Drop here" popup is up
    /// the live region grows to include the band above the pill, and that band
    /// is a drop target too — so the popup stays open (and drops land) when
    /// the cursor moves into the DROP HERE area.
    /// </summary>
    private bool IsInsideDropTarget(POINTL pt)
    {
        if (IsInsidePill(pt)) return true;
        try
        {
            return !App.IslandController.IsExpanded
                && App.IslandController.IsDropPopupActive
                && App.Window.IsPointInCurrentRegion(pt.x, pt.y);
        }
        catch
        {
            return false;
        }
    }

    // ── STEP 6 — payload probing (signal-only, no resolution) ─────────────

    private bool HasShellFilePayload(IDataObject data)
    {
        try
        {
            bool hasCF_HDROP = QueryGetData(data, CF_HDROP);
            bool hasShellIdList = _shellIdListFormat != 0 && QueryGetData(data, _shellIdListFormat);
            bool hasFileDrop = _fileDropFormat != 0 && QueryGetData(data, _fileDropFormat);
            bool hasFileGroupDescriptor = _fileGroupDescriptorFormat != 0 && QueryGetData(data, _fileGroupDescriptorFormat);
            bool hasStorageItems = _storageItemsFormat != 0 && QueryGetData(data, _storageItemsFormat);

            bool accepted = hasCF_HDROP || hasShellIdList || hasFileDrop;
            Logger.Info(
                "[DRAG-PAYLOAD] " +
                $"hasCF_HDROP={hasCF_HDROP} hasFileDrop={hasFileDrop} hasShellIdList={hasShellIdList} " +
                $"hasFileGroupDescriptor={hasFileGroupDescriptor} hasStorageItems={hasStorageItems} " +
                $"accepted={accepted}");
            return accepted;
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG] payload probe failed", ex);
            return false;
        }
    }

    private static bool QueryGetData(IDataObject data, short cfFormat)
    {
        var fet = new FORMATETC
        {
            cfFormat = cfFormat,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED_HGLOBAL,
        };
        return data.QueryGetData(ref fet) == 0;
    }

    private static string[] ExtractPaths(IDataObject data)
    {
        var fet = new FORMATETC
        {
            cfFormat = CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED_HGLOBAL,
        };

        try
        {
            int hr = data.GetData(ref fet, out STGMEDIUM medium);
            if (hr != 0) return Array.Empty<string>();
            IntPtr hDrop = medium.unionmember;
            try
            {
                uint count = DragQueryFile(hDrop, DragQueryFileCount, null, 0);
                var paths = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    uint len = DragQueryFile(hDrop, i, null, 0);
                    if (len == 0) continue;
                    var sb = new StringBuilder((int)len + 1);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    paths.Add(sb.ToString());
                }
                return paths.ToArray();
            }
            finally
            {
                if (hDrop != IntPtr.Zero) GlobalFree(hDrop);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG] drop-path extraction failed", ex);
            return Array.Empty<string>();
        }
    }

    // ── Win32 helpers ─────────────────────────────────────────────────────

    private static string GetClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "null";
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) != 0 ? sb.ToString() : "?";
    }

    private static string GetWindowTitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) != 0 ? sb.ToString() : "";
    }

    private static int ClientOriginX(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return 0;
        return r.Left;
    }

    private static int ClientOriginY(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return 0;
        return r.Top;
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget pDropTarget);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short RegisterClipboardFormatW(string lpszFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINTL pt);

    [DllImport("user32.dll")]
    private static extern IntPtr ChildWindowFromPoint(IntPtr hWnd, POINTL pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Per-HWND IDropTarget adapter so every native callback knows WHICH
    /// registered window OLE selected — the shared target object is registered
    /// on multiple HWNDs and a single COM object cannot report that.
    /// Holds a strong reference to the owner for its whole lifetime.
    /// </summary>
    private sealed class RegisteredTarget : OleDropTargetInterop.IDropTarget
    {
        private readonly IntPtr _hwnd;
        private readonly OleDropTarget _owner;

        public RegisteredTarget(IntPtr hwnd, OleDropTarget owner)
        {
            _hwnd = hwnd;
            _owner = owner;
        }

        public int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref int pdwEffect) =>
            _owner.OnDragEnter(_hwnd, pDataObj, grfKeyState, pt, ref pdwEffect);

        public int DragOver(uint grfKeyState, POINTL pt, ref int pdwEffect) =>
            _owner.OnDragOver(_hwnd, grfKeyState, pt, ref pdwEffect);

        public int DragLeave() =>
            _owner.OnDragLeave(_hwnd);

        public int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref int pdwEffect) =>
            _owner.OnDrop(_hwnd, pDataObj, grfKeyState, pt, ref pdwEffect);
    }
}

/// <summary>
/// OLE surface for <see cref="OleDropTarget"/> (ComTypes equivalents, self-
/// contained for .NET Core — ComTypes.IDropTarget/POINTL do not ship there).
/// Namespace-level so the adapter's base-class list can reference IDropTarget.
/// </summary>
/// <remarks>
/// GUIDs and vtable order match the OS contracts; the marshaller resolves
/// interface identity from the Guid, not the declaration site.
/// </remarks>
internal static class OleDropTargetInterop
{
    /// <summary>IUnknown-compatible OLE IDropTarget (00000118-...).</summary>
    [ComImport]
    [Guid("00000118-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropTarget
    {
        [PreserveSig]
        int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig]
        int DragOver(uint grfKeyState, POINTL pt, ref int pdwEffect);
        [PreserveSig]
        int DragLeave();
        [PreserveSig]
        int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref int pdwEffect);
    }

    /// <summary>IUnknown-compatible OLE IDataObject (0000010E-...) — vtable order matters.</summary>
    [ComImport]
    [Guid("0000010E-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDataObject
    {
        [PreserveSig]
        int GetData(ref FORMATETC pformatetcIn, out STGMEDIUM pmedium);
        [PreserveSig]
        int GetDataHere(ref FORMATETC pformatetc, ref STGMEDIUM pmedium);
        [PreserveSig]
        int QueryGetData(ref FORMATETC pformatetc);
        [PreserveSig]
        int GetCanonicalFormatEtc(ref FORMATETC pformatetcIn, out FORMATETC pformatetcOut);
        [PreserveSig]
        int SetData(ref FORMATETC pformatetc, ref STGMEDIUM pmedium, bool fRelease);
        [PreserveSig]
        int EnumFormatEtc(int dwDirection, out IntPtr ppenumFormatEtc);
        [PreserveSig]
        int DAdvise(ref FORMATETC pformatetc, int advf, IntPtr pAdvSink, out int pdwConnection);
        [PreserveSig]
        int DUnadvise(int dwConnection);
        [PreserveSig]
        int EnumDAdvise(out IntPtr ppenumAdvise);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FORMATETC
    {
        public int cfFormat;
        public IntPtr ptd;
        public int dwAspect;
        public int lindex;
        public int tymed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STGMEDIUM
    {
        public int tymed;
        public IntPtr unionmember;
        public IntPtr pUnkForRelease;
    }
}
