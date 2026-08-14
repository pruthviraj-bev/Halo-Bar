using System;
using System.Diagnostics;

namespace DynamicIsland.Helpers;

/// <summary>
/// Pass 7 process-memory forensics.
///
/// Measures the REAL process footprint (working set, private bytes, commit) plus
/// managed GC state at lifecycle checkpoints. This answers questions the Pass 6
/// [MEM] markers (managed/UI-thread allocations only) cannot: total process RAM,
/// where it comes from, and whether it returns to baseline after temporary UI
/// (dashboard, media, transient pills) disappears.
///
/// Metric semantics (all read via the cached current-process handle):
///  - workingSetMB : physical RAM currently mapped for the process.
///  - privateMB    : private bytes — committed memory not shareable with other
///                   processes (heap, COM/WinRT, native allocations). This is the
///                   closest single number to "what Halo Bar alone holds".
///  - commitMB     : pagefile-backed commit charge (PagedMemorySize64).
///  - managedHeapMB: current managed GC heap usage (no forced collection).
///  - gcCommittedMB: managed memory committed to the GC heap. Native/COM/WinUI
///                   footprint ≈ privateMB − gcCommittedMB (estimate only).
///  - lohMB        : large-object heap bytes (images/streams land here).
///  - gen0/1/2     : collection counts — rising gen2 over time with no fall in
///                   working set is the classic leak signature.
///
/// Pure measurement. GC is NEVER forced from normal lifecycle code — the only
/// exception is <see cref="GcDiagnostic"/>, the explicitly marked one-shot
/// experiment used to separate garbage-awaiting-collection from retained objects.
/// </summary>
public static class ProcessMemoryProfiler
{
    private static readonly Process _process = Process.GetCurrentProcess();
    private static long _lastCheckpointMs = Environment.TickCount64;

    /// <summary>
    /// Emits one compact, machine-readable [MEM-P7] line with process + GC metrics.
    /// Thread-safe and cheap — safe on any thread, but intended for lifecycle
    /// events (expand/collapse, track changes, 60 s sampler), never per frame.
    /// </summary>
    public static void Checkpoint(string name)
    {
        try
        {
            _process.Refresh();
            long now = Environment.TickCount64;
            long elapsed = now - _lastCheckpointMs;
            _lastCheckpointMs = now;

            var gcInfo = GC.GetGCMemoryInfo();
            // GenerationInfo is ordered by GCGeneration; LargeObjectHeap is index 3
            // (0=NonGCHeap, 1=FrozenObject, 2=Tenured, 3=LargeObjectHeap, 4=PinnedHeap).
            long lohBytes = gcInfo.GenerationInfo[3].SizeAfterBytes;

            Logger.Info(
                $"[MEM-P7] checkpoint={name} " +
                $"workingSetMB={MB(_process.WorkingSet64):F1} " +
                $"privateMB={MB(_process.PrivateMemorySize64):F1} " +
                $"commitMB={MB(_process.PagedMemorySize64):F1} " +
                $"managedHeapMB={MB(GC.GetTotalMemory(false)):F1} " +
                $"gcCommittedMB={MB(gcInfo.TotalCommittedBytes):F1} " +
                $"lohMB={MB(lohBytes):F1} " +
                $"gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} " +
                $"sinceLastCheckpointMs={elapsed}");
        }
        catch (Exception ex)
        {
            Logger.Error("[MEM-P7] checkpoint failed", ex);
        }
    }

    /// <summary>
    /// EXPLICITLY MARKED diagnostic experiment (Pass 7 spec): one forced
    /// collection to separate "managed garbage awaiting collection" from
    /// "objects still strongly referenced". Called exactly once, from the
    /// expand-count hook after the 20th expand/collapse cycle. NOT used in
    /// normal lifecycle code.
    /// </summary>
    public static void GcDiagnostic(string tag)
    {
        long before = GC.GetTotalMemory(false);
        int gen2Before = GC.CollectionCount(2);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long after = GC.GetTotalMemory(true);
        int gen2After = GC.CollectionCount(2);

        Logger.Info(
            $"[MEM-P7-GC] tag={tag} " +
            $"managedHeapBeforeMB={MB(before):F1} " +
            $"managedHeapAfterMB={MB(after):F1} " +
            $"reclaimedMB={MB(Math.Max(0, before - after)):F1} " +
            $"gen2Before={gen2Before} gen2After={gen2After}");

        // Full post-GC snapshot so the report can compare retained vs collected.
        Checkpoint(tag + "PostGc");
    }

    private static double MB(long bytes) => bytes / (1024.0 * 1024.0);
}
