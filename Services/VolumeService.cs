using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Polls system output volume and emits notifications only on meaningful changes.
/// </summary>
public class VolumeService
{
    private readonly DispatcherQueue _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;
    private VolumeState? _lastState;
    private IMMDeviceEnumerator? _deviceEnumerator;
    private IMMDevice? _defaultRenderDevice;
    private IAudioEndpointVolume? _endpointVolume;

    public event EventHandler<(VolumeState State, TimeSpan Duration)>? NotificationRequired;

    /// <summary>
    /// Last known volume state, refreshed by the 150 ms poll. Consumers (e.g. the
    /// dashboard's 1 s stats tick) should read this instead of calling
    /// <see cref="ReadCurrentState"/> — each call is two COM calls into the audio
    /// endpoint, which is wasteful at 1 Hz when the poll already keeps this fresh.
    /// </summary>
    public VolumeState CurrentState => _lastState ?? new VolumeState(0, false);

    private const int ClsctxAll = 23;

    public VolumeService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public void Initialize()
    {
        try
        {
            InitializeEndpointVolume();
            _lastState = ReadCurrentState();
            Logger.Info($"VolumeService: initialized at {_lastState.VolumePercent}% (muted={_lastState.IsMuted})");

            // Pass 15 diagnostic: HALO_P15_NOVOLUME=1 skips the 150 ms volume
            // poll so the idle render stream can be attributed to it vs. other
            // sustainers. Default behavior unchanged.
            if (!Helpers.MotionDiagnostics.P15NoVolumePoll)
            {
                _pollTimer = _dispatcherQueue.CreateTimer();
                _pollTimer.Interval = TimeSpan.FromMilliseconds(150);
                _pollTimer.IsRepeating = true;
                _pollTimer.Tick += (_, _) => Poll();
                _pollTimer.Start();
            }
            else
            {
                Logger.Info("[P15] volume polling disabled (HALO_P15_NOVOLUME=1, diagnostic).");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("VolumeService: failed to initialize", ex);
        }
    }

    private void Poll()
    {
        try
        {
            var current = ReadCurrentState();
            if (_lastState == null)
            {
                _lastState = current;
                return;
            }

            bool muteChanged = current.IsMuted != _lastState.IsMuted;
            bool volumeChanged = Math.Abs(current.VolumePercent - _lastState.VolumePercent) >= 1;

            if (!muteChanged && !volumeChanged)
                return;

            _lastState = current;
            Logger.Info($"VolumeService: changed to {current.VolumePercent}% (muted={current.IsMuted})");
            NotificationRequired?.Invoke(this, (current, NotificationDuration.Short));
        }
        catch (Exception ex)
        {
            Logger.Error("VolumeService: poll failed", ex);
        }
    }

    private void InitializeEndpointVolume()
    {
        _deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        int hr = _deviceEnumerator.GetDefaultAudioEndpoint(EDataFlow.ERender, ERole.EMultimedia, out _defaultRenderDevice);
        if (hr != 0 || _defaultRenderDevice == null)
            throw new InvalidOperationException($"VolumeService: GetDefaultAudioEndpoint failed with HRESULT 0x{hr:X8}.");

        Guid iid = typeof(IAudioEndpointVolume).GUID;
        hr = _defaultRenderDevice.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object endpointObject);
        if (hr != 0)
            throw new InvalidOperationException($"VolumeService: Activate(IAudioEndpointVolume) failed with HRESULT 0x{hr:X8}.");

        _endpointVolume = (IAudioEndpointVolume)endpointObject;
    }

    public VolumeState ReadCurrentState()
    {
        if (_endpointVolume == null)
            throw new InvalidOperationException("VolumeService: endpoint volume is not initialized.");

        int hr = _endpointVolume.GetMasterVolumeLevelScalar(out float scalar);
        if (hr != 0)
            throw new InvalidOperationException($"VolumeService: GetMasterVolumeLevelScalar failed with HRESULT 0x{hr:X8}.");

        hr = _endpointVolume.GetMute(out bool isMuted);
        if (hr != 0)
            throw new InvalidOperationException($"VolumeService: GetMute failed with HRESULT 0x{hr:X8}.");

        int percent = (int)Math.Round(scalar * 100.0);
        percent = Math.Clamp(percent, 0, 100);

        return new VolumeState(percent, isMuted);
    }

    public void SetVolume(int percent)
    {
        if (_endpointVolume == null) return;
        try
        {
            float scalar = percent / 100f;
            Guid guid = Guid.Empty;
            _endpointVolume.SetMasterVolumeLevelScalar(scalar, ref guid);
            // Read immediately to update state
            var current = ReadCurrentState();
            _lastState = current;
            Logger.Info($"VolumeService: changed to {current.VolumePercent}% (muted={current.IsMuted}) via SetVolume");
        }
        catch (Exception ex)
        {
            Logger.Error("VolumeService: SetVolume failed", ex);
        }
    }

    public void SetMute(bool isMuted)
    {
        if (_endpointVolume == null) return;
        try
        {
            Guid guid = Guid.Empty;
            _endpointVolume.SetMute(isMuted, ref guid);
            // Read immediately to update state
            var current = ReadCurrentState();
            _lastState = current;
            Logger.Info($"VolumeService: changed to {current.VolumePercent}% (muted={current.IsMuted}) via SetMute");
        }
        catch (Exception ex)
        {
            Logger.Error("VolumeService: SetMute failed", ex);
        }
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

            if (_endpointVolume != null && Marshal.IsComObject(_endpointVolume))
            {
                Marshal.ReleaseComObject(_endpointVolume);
                _endpointVolume = null;
            }

            if (_defaultRenderDevice != null && Marshal.IsComObject(_defaultRenderDevice))
            {
                Marshal.ReleaseComObject(_defaultRenderDevice);
                _defaultRenderDevice = null;
            }

            if (_deviceEnumerator != null && Marshal.IsComObject(_deviceEnumerator))
            {
                Marshal.ReleaseComObject(_deviceEnumerator);
                _deviceEnumerator = null;
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private enum EDataFlow
    {
        ERender = 0,
        ECapture = 1,
        EAll = 2
    }

    private enum ERole
    {
        EConsole = 0,
        EMultimedia = 1,
        ECommunications = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.Interface)] out object interfacePointer);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channelNumber, float levelDb, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channelNumber, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid eventContext);
        int VolumeStepDown(ref Guid eventContext);
        int QueryHardwareSupport(out uint hardwareSupportMask);
        int GetVolumeRange(out float volumeMinDb, out float volumeMaxDb, out float volumeIncrementDb);
    }
}
