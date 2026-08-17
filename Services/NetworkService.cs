using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.UI.Dispatching;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Samples live network throughput (download/upload, bytes/s) from the active
/// network interface's cumulative byte counters. Never inspects packets and
/// never reads the negotiated link speed — the reported number is pure
/// interface statistics: (bytes now − bytes prev) ÷ elapsed.
///
/// The adapter is picked once per tick from the connected, non-virtual
/// interfaces that carry an IPv4 address, preferring the one with a default
/// gateway. If the active adapter changes, its counters reset, or a delta is
/// negative (counter wrap / adapter swap mid-tick), the baseline is simply
/// re-established on the next tick — the metric never goes negative.
///
/// Sampling is driven by a 1 s DispatcherQueueTimer rooted for the app's
/// lifetime (same pattern as the other services).
/// </summary>
public class NetworkService
{
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;

    // Baseline counters from the previous sample.
    private NetworkInterface? _baselineInterface;
    private long _baselineReceived;
    private long _baselineSent;
    private DateTimeOffset _baselineTime;

    public NetworkState CurrentState { get; private set; } = new(0, 0);

    public NetworkService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public void Initialize()
    {
        try
        {
            _pollTimer = _dispatcherQueue.CreateTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(1);
            _pollTimer.IsRepeating = true;
            _pollTimer.Tick += (_, _) => Poll();
            _pollTimer.Start();
            Logger.Info("NetworkService: initialized (1 s sampling)");
        }
        catch (Exception ex)
        {
            Logger.Error("NetworkService: failed to initialize", ex);
        }
    }

    private void Poll()
    {
        try
        {
            var active = FindActiveInterface();
            if (active == null)
            {
                CurrentState = new NetworkState(0, 0);
                _baselineInterface = null;
                _baselineTime = default;
                return;
            }

            var stats = active.GetIPv4Statistics();
            long received = stats.BytesReceived;
            long sent = stats.BytesSent;
            var now = DateTimeOffset.UtcNow;

            // First sample, adapter change, or counter wrap → re-baseline.
            if (_baselineInterface == null ||
                !string.Equals(_baselineInterface.Id, active.Id, StringComparison.Ordinal) ||
                received < _baselineReceived ||
                sent < _baselineSent)
            {
                _baselineInterface = active;
                _baselineReceived = received;
                _baselineSent = sent;
                _baselineTime = now;
                CurrentState = new NetworkState(0, 0);
                return;
            }

            double elapsedSeconds = (now - _baselineTime).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            // Bytes per second, computed directly from the byte counters — no
            // ×8 bit conversion (the display layer handles MB/s formatting).
            long downloadBytesPerSecond = (long)Math.Round((received - _baselineReceived) / elapsedSeconds);
            long uploadBytesPerSecond = (long)Math.Round((sent - _baselineSent) / elapsedSeconds);

            _baselineInterface = active;
            _baselineReceived = received;
            _baselineSent = sent;
            _baselineTime = now;

            CurrentState = new NetworkState(Math.Max(0, downloadBytesPerSecond), Math.Max(0, uploadBytesPerSecond));
        }
        catch (Exception ex)
        {
            // Adapter vanished mid-read or its stats became unavailable: discard
            // the sample and re-baseline on the next tick.
            Logger.Info($"NetworkService: poll failed (re-baselining) — {ex.Message}");
            _baselineInterface = null;
            _baselineTime = default;
            CurrentState = new NetworkState(0, 0);
        }
    }

    /// <summary>
    /// Picks the interface currently carrying traffic: up, connected, carrying an
    /// IPv4 unicast address, and not loopback/tunnel/receive-only. Prefers the one
    /// with an IPv4 default gateway (the real uplink); falls back to the first
    /// qualifying adapter so a gateway-less setup still reports something.
    /// </summary>
    private static NetworkInterface? FindActiveInterface()
    {
        NetworkInterface? gatewayCandidate = null;
        NetworkInterface? fallback = null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (nic.IsReceiveOnly) continue;

            bool hasIpv4;
            bool hasGateway;
            try
            {
                var props = nic.GetIPProperties();
                hasIpv4 = props.UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork);
                hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork);
            }
            catch
            {
                continue;
            }

            if (!hasIpv4) continue;

            fallback ??= nic;
            if (hasGateway)
            {
                gatewayCandidate ??= nic;
            }
        }

        return gatewayCandidate ?? fallback;
    }

    public void Dispose()
    {
        try
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer = null;
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}