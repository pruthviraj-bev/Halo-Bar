using System;
using System.Runtime.InteropServices;

namespace DynamicIsland.Helpers;

/// <summary>
/// PASS 38 (GOAL 2): OLE initialization for the unpackaged-app drag path.
///
/// Explorer's drag manager delivers file/folder drags over the Halo pill as an
/// OLE DragEnter/DragOver/Drop stream. For that stream to reach the window's
/// XAML drop target (PillDashboard.AllowDrop), the UI thread that owns the
/// window must be COM-initialized for OLE: <c>RegisterDragDrop</c> (performed
/// internally by WinUI when a XAML element opts into drag-and-drop) fails with
/// OLE_E_NOT_INITIALIZED when <c>OleInitialize</c> has not been called on the
/// thread. Packaged apps get this from the package activation path; unpackaged
/// WinUI 3 apps (WindowsPackageType=None) do NOT — so with no OLE init the
/// Explorer drag never surfaces as XAML DragEnter/DragOver and the File Shelf
/// never opens on drag-hover. This class initializes OLE on the UI thread once
/// at startup, and also drives the [DRAG] forensics switches.
/// </summary>
public static class Win32DragSupport
{
    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    private static bool _initialized;
    private static bool _attempted;

    private const int S_OK = 0;
    private const int S_FALSE = 1;

    /// <summary>
    /// Ensures the calling thread is OLE-initialized. Safe to call once from
    /// OnLaunched (UI thread); idempotent. Never throws.
    /// </summary>
    public static void EnsureOleInitialized()
    {
        if (_attempted) return;
        _attempted = true;
        try
        {
            int hr = OleInitialize(IntPtr.Zero);
            if (hr == S_OK)
            {
                _initialized = true;
                Logger.Info("[DRAG] OleInitialize → S_OK (OLE initialized on the UI thread — Explorer drag routing enabled).");
            }
            else if (hr == S_FALSE)
            {
                Logger.Info("[DRAG] OleInitialize → S_FALSE (OLE already initialized on this thread).");
            }
            else
            {
                Logger.Info($"[DRAG] OleInitialize failed hr=0x{hr:X8} — Explorer drag routing may not reach the pill.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DRAG] OleInitialize threw", ex);
        }
    }

    public static bool IsInitialized => _initialized;
}
