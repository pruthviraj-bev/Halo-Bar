using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Service responsible for querying and controlling system-wide media playback via WinRT SMTC.
/// </summary>
public class MediaService
{
    // Temporary diagnostic instrumentation for media-session lifecycle tracing.
    // Remove together with DescribeSession/DescribeApp/FindReferenceIndex once confirmed.
    private static readonly bool SessionTracing = true;

    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    // User's manual pin from the source switcher. When non-null, auto-follow is suspended
    // and this session becomes the effective session until the pin is cleared.
    private GlobalSystemMediaTransportControlsSession? _selectedSession;

    private IReadOnlyList<GlobalSystemMediaTransportControlsSession> _cachedSessions = Array.Empty<GlobalSystemMediaTransportControlsSession>();

    // Single atomic source of truth for all media states
    public MediaState CurrentState { get; private set; } = new MediaState("", "", null, false);

    public event EventHandler<MediaState>? MediaStateChanged;

    public bool HasMultipleSources => _cachedSessions.Count > 1;

    // The session every control method operates on: the user's pin if one is set,
    // otherwise whatever Windows currently considers the current session.
    private GlobalSystemMediaTransportControlsSession? EffectiveSession => _selectedSession ?? _currentSession;

    // The session instance whose events are CURRENTLY wired up. Used instead of
    // reference-comparing effective snapshots so that silent background updates to
    // _currentSession while a pin is active can never cause a missed re-wire.
    private GlobalSystemMediaTransportControlsSession? _subscribedSession;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                _sessionManager.SessionsChanged += OnSessionsChanged;
                RefreshCachedSessions();
                UpdateCurrentSession();
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("Failed to initialize MediaService", ex);
        }
    }

    private void UpdateCurrentSession()
    {
        try
        {
            _currentSession = _sessionManager?.GetCurrentSession();
            if (SessionTracing)
                Logger.Info($"[SESSION] UpdateCurrentSession: GetCurrentSession() -> {DescribeSession(_currentSession)}");

            if (!ReferenceEquals(_subscribedSession, EffectiveSession))
            {
                DetachSessionEvents(_subscribedSession);
                AttachSessionEvents(EffectiveSession);
            }

            if (EffectiveSession != null)
            {
                TriggerStateUpdate();
            }
            else
            {
                // Reset to empty state when no active player session exists
                UpdateState(new MediaState("", "", null, false, sourceAppUserModelId: "", sourceName: "Unknown Source"));
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("MediaService: Error during UpdateCurrentSession", ex);
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (SessionTracing)
            Logger.Info($"[SESSION] OnCurrentSessionChanged fired (prev current={DescribeSession(_currentSession)})");
        else
            Helpers.Logger.Info("MediaService: OnCurrentSessionChanged fired");

        if (_selectedSession != null)
        {
            // Auto-follow is suspended while a source is pinned. Keep the underlying
            // _currentSession field fresh for when the pin is cleared, but do NOT
            // re-wire events or trigger a state update for the effective session.
            _currentSession = _sessionManager?.GetCurrentSession();
            return;
        }

        UpdateCurrentSession();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        Helpers.Logger.Info("MediaService: OnSessionsChanged fired");

        RefreshCachedSessions();
        ValidatePinnedSession();

        if (SessionTracing)
            Logger.Info($"[SESSION] After SessionsChanged: current={DescribeSession(_currentSession)} effective={DescribeSession(EffectiveSession)}");

        // Re-resolve the effective session against the fresh snapshot. SessionsChanged is
        // the OS's session-removal signal and fires promptly; CurrentSessionChanged can lag
        // 2-3s after a player exits. Without this re-resolution, _currentSession keeps
        // referencing the dead session, so the empty state is never published and the
        // MediaWidget lingers on the stack until CurrentSessionChanged arrives.
        UpdateCurrentSession();
    }

    /// <summary>
    /// Defensive periodic check that the pinned source is still alive. Relies on a fresh
    /// GetSessions() snapshot rather than the SessionsChanged event, which does not reliably
    /// fire when browser-hosted SMTC sessions close. No-op when no pin is active.
    /// </summary>
    private void ValidatePinnedSession()
    {
        if (_selectedSession == null) return;

        RefreshCachedSessions();
        if (FindSessionIndex(_selectedSession) >= 0) return;

        Helpers.Logger.Info("MediaService: Pinned source no longer found in active sessions, reverting to auto-follow");
        DetachSessionEvents(_selectedSession);
        _selectedSession = null;
        UpdateCurrentSession();
    }

    /// <summary>
    /// Called periodically from the dashboard's 1-second timer to validate the pinned source
    /// even when the OS does not raise SessionsChanged for a closed session.
    /// </summary>
    public void TickValidation()
    {
        ValidatePinnedSession();
    }

    private void RefreshCachedSessions()
    {
        if (!SessionTracing)
        {
            _cachedSessions = _sessionManager?.GetSessions() ?? Array.Empty<GlobalSystemMediaTransportControlsSession>();
            return;
        }

        var prev = _cachedSessions;
        _cachedSessions = _sessionManager?.GetSessions() ?? Array.Empty<GlobalSystemMediaTransportControlsSession>();

        foreach (var gone in prev)
        {
            if (FindReferenceIndex(_cachedSessions, gone) < 0)
            {
                bool wasEffective = ReferenceEquals(gone, EffectiveSession) || ReferenceEquals(gone, _currentSession);
                Logger.Info($"[SESSION] Removed(ref){(wasEffective ? " [WAS CURRENT/EFFECTIVE]" : "")}: {DescribeSession(gone)}");
            }
        }
        foreach (var fresh in _cachedSessions)
        {
            if (FindReferenceIndex(prev, fresh) < 0)
                Logger.Info($"[SESSION] Added(ref): {DescribeSession(fresh)}");
        }

        Logger.Info($"[SESSION] RefreshCachedSessions: {_cachedSessions.Count} active session(s)");
        for (int i = 0; i < _cachedSessions.Count; i++)
            Logger.Info($"[SESSION]   [{i}] {DescribeSession(_cachedSessions[i])}");
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (SessionTracing) Logger.Info($"[SESSION] MediaPropertiesChanged: {DescribeSession(sender)}");
        TriggerStateUpdate();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (SessionTracing) Logger.Info($"[SESSION] PlaybackInfoChanged: {DescribeSession(sender)}");
        TriggerStateUpdate();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (SessionTracing) Logger.Info($"[SESSION] TimelinePropertiesChanged: {DescribeSession(sender)}");
        TriggerStateUpdate();
    }

    private void AttachSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return;
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _subscribedSession = session;
    }

    private void DetachSessionEvents(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return;
        session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        if (ReferenceEquals(_subscribedSession, session))
        {
            _subscribedSession = null;
        }
    }

    private async void TriggerStateUpdate()
    {
        var session = EffectiveSession;
        if (session == null) return;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = session.GetTimelineProperties();

            if (SessionTracing)
            {
                string status = playbackInfo?.PlaybackStatus.ToString() ?? "(null)";
                Logger.Info($"[SESSION] TriggerStateUpdate: {DescribeSession(session)} status={status} isPlaying={isPlaying} title='{props?.Title ?? "(null)"}'");
                if (playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
                    Logger.Info("[SESSION]  ^^ PlaybackStatus=Stopped on the effective session — candidate for 'kept-alive dead session'");
            }

            TimeSpan duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;

            TimeSpan position = timeline.Position;
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (duration > TimeSpan.Zero && position > duration) position = duration;

            string sourceAppUserModelId = string.Empty;
            try
            {
                sourceAppUserModelId = session.SourceAppUserModelId ?? string.Empty;
            }
            catch
            {
                // Some sessions may not expose a readable AUMID.
            }

            if (props != null)
            {
                UpdateState(new MediaState(
                    props.Title ?? "",
                    props.Artist ?? "",
                    props.Thumbnail,
                    isPlaying,
                    position,
                    duration,
                    timeline.LastUpdatedTime,
                    playbackInfo?.AutoRepeatMode,
                    sourceAppUserModelId,
                    GetDisplayName(sourceAppUserModelId)
                ));
            }
            else
            {
                // A null properties result is a common symptom of a session that has just been
                // torn down. Confirm against a fresh snapshot; if the session is truly gone,
                // publish the empty state immediately so the widget pops instead of lingering
                // until the lagging CurrentSessionChanged event. A live session that is simply
                // mid-metadata-load still appears in the snapshot, so it will not be popped.
                RefreshCachedSessions();
                if (FindSessionIndex(session) < 0)
                {
                    Helpers.Logger.Info("MediaService: current session no longer active, publishing empty state");
                    UpdateState(new MediaState("", "", null, false, sourceAppUserModelId: "", sourceName: "Unknown Source"));
                }
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("MediaService: Error getting media state update", ex);
        }
    }

    private void UpdateState(MediaState newState)
    {
        if (SessionTracing)
        {
            string suffix = string.IsNullOrEmpty(newState.Title)
                ? " (EMPTY — MediaWidget pop trigger)"
                : "";
            Logger.Info($"[SESSION] UpdateState: title='{newState.Title}' artist='{newState.Artist}' isPlaying={newState.IsPlaying}{suffix}");
        }

        CurrentState = newState;
        MediaStateChanged?.Invoke(this, newState);
    }

    private static string GetDisplayName(string appId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return appId;
        try
        {
            return AppInfo.GetFromAppUserModelId(appId).DisplayInfo.DisplayName;
        }
        catch
        {
            return appId;
        }
    }

    // ── Diagnostic session tracing (temporary) ────────────────────────────

    private string DescribeSession(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s == null) return "null";

        string aumid = "(unreadable)";
        try { aumid = string.IsNullOrEmpty(s.SourceAppUserModelId) ? "(empty)" : s.SourceAppUserModelId; } catch { }

        string status = "(unreadable)";
        try { status = s.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "(null)"; } catch { }

        string pos = "(unreadable)";
        try
        {
            var tl = s.GetTimelineProperties();
            var dur = tl.EndTime - tl.StartTime;
            pos = $"{tl.Position:hh\\:mm\\:ss}/{dur:hh\\:mm\\:ss}";
        }
        catch { }

        string app = DescribeApp(aumid);
        return $"{{hash={RuntimeHelpers.GetHashCode(s):X8} aumid='{aumid}' {app} status={status} pos={pos} isCurrent={ReferenceEquals(s, _currentSession)} isEffective={ReferenceEquals(s, EffectiveSession)}}}";
    }

    private static string DescribeApp(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return "";
        try
        {
            var info = AppInfo.GetFromAppUserModelId(aumid);
            if (info == null) return "";
            string display = info.DisplayInfo?.DisplayName ?? "?";
            string pfn = info.Package?.Id?.FamilyName ?? "?";
            return $"display='{display}' pfn='{pfn}'";
        }
        catch
        {
            return "";
        }
    }

    private static int FindReferenceIndex(IReadOnlyList<GlobalSystemMediaTransportControlsSession> list, GlobalSystemMediaTransportControlsSession session)
    {
        for (int i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], session)) return i;
        return -1;
    }

    private int FindSessionIndex(GlobalSystemMediaTransportControlsSession session)
    {
        for (int i = 0; i < _cachedSessions.Count; i++)
        {
            var candidate = _cachedSessions[i];
            if (ReferenceEquals(candidate, session)) return i;
            try
            {
                if (candidate.SourceAppUserModelId == session.SourceAppUserModelId) return i;
            }
            catch
            {
                // Fall through to the next candidate if identity isn't readable.
            }
        }
        return -1;
    }

    public void SelectNextSource()
    {
        SelectSource(1);
    }

    public void SelectPreviousSource()
    {
        SelectSource(-1);
    }

    private void SelectSource(int direction)
    {
        var effective = EffectiveSession;
        if (effective == null) return;

        RefreshCachedSessions();
        if (_cachedSessions.Count < 2) return;

        int index = FindSessionIndex(effective);
        if (index < 0) index = 0;

        int newIndex = (index + direction + _cachedSessions.Count) % _cachedSessions.Count;
        if (newIndex == index) return;

        var target = _cachedSessions[newIndex];

        DetachSessionEvents(effective);
        _selectedSession = target;
        AttachSessionEvents(_selectedSession);
        TriggerStateUpdate();
    }

    public async Task PlayAsync()
    {
        var session = EffectiveSession;
        if (session != null)
        {
            await session.TryPlayAsync();
        }
    }

    public async Task PauseAsync()
    {
        var session = EffectiveSession;
        if (session != null)
        {
            await session.TryPauseAsync();
        }
    }

    public async Task SkipNextAsync()
    {
        var session = EffectiveSession;
        if (session != null)
        {
            await session.TrySkipNextAsync();
        }
    }

    public async Task SkipPreviousAsync()
    {
        var session = EffectiveSession;
        if (session != null)
        {
            await session.TrySkipPreviousAsync();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var session = EffectiveSession;
        if (session == null) return;

        try
        {
            await session.TryChangePlaybackPositionAsync(position.Ticks);
            TriggerStateUpdate();
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("MediaService: Failed to seek", ex);
        }
    }

    public async Task ToggleRepeatAsync()
    {
        var session = EffectiveSession;
        if (session == null) return;

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var current = playbackInfo?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None;
            var next = current switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None,
            };
            await session.TryChangeAutoRepeatModeAsync(next);
            TriggerStateUpdate();
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("MediaService: Failed to toggle repeat", ex);
        }
    }
}
