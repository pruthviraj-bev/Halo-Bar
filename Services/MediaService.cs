using System;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;
using DynamicIsland.Helpers;

namespace DynamicIsland.Services;

/// <summary>
/// Service responsible for querying and controlling system-wide media playback via WinRT SMTC.
/// </summary>
public class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    // Single atomic source of truth for all media states
    public MediaState CurrentState { get; private set; } = new MediaState("", "", null, false);

    public event EventHandler<MediaState>? MediaStateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
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
            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }

            _currentSession = _sessionManager?.GetCurrentSession();

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
                TriggerStateUpdate();
            }
            else
            {
                // Reset to empty state when no active player session exists
                UpdateState(new MediaState("", "", null, false));
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("MediaService: Error during UpdateCurrentSession", ex);
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        UpdateCurrentSession();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        TriggerStateUpdate();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        TriggerStateUpdate();
    }

    private async void TriggerStateUpdate()
    {
        if (_currentSession == null) return;

        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            var playbackInfo = _currentSession.GetPlaybackInfo();
            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            if (props != null)
            {
                Helpers.Logger.Info($"[DEBUG] MediaService: TryGetMediaPropertiesAsync success. Title='{props.Title}', Artist='{props.Artist}', HasThumbnail={props.Thumbnail != null}");
                
                UpdateState(new MediaState(
                    props.Title ?? "",
                    props.Artist ?? "",
                    props.Thumbnail,
                    isPlaying
                ));
            }
            else
            {
                Helpers.Logger.Info("[DEBUG] MediaService: TryGetMediaPropertiesAsync returned null properties");
            }
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error("[DEBUG] MediaService: Error getting media state update", ex);
        }
    }

    private void UpdateState(MediaState newState)
    {
        CurrentState = newState;
        MediaStateChanged?.Invoke(this, newState);
    }

    public async Task PlayAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TryPlayAsync();
        }
    }

    public async Task PauseAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TryPauseAsync();
        }
    }

    public async Task SkipNextAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TrySkipNextAsync();
        }
    }

    public async Task SkipPreviousAsync()
    {
        if (_currentSession != null)
        {
            await _currentSession.TrySkipPreviousAsync();
        }
    }
}
