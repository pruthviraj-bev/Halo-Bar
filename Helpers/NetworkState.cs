namespace DynamicIsland.Helpers;

/// <summary>
/// Immutable snapshot of live network throughput on the active interface,
/// expressed in bytes per second (not the adapter's negotiated link speed).
/// Values come directly from the interface byte counters (delta ÷ elapsed);
/// no bit conversion is performed.
/// </summary>
public sealed record NetworkState(
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond
);