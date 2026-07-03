using System.Diagnostics;
using System.Windows.Threading;
using SpotifyTaskbarWidget.Spotify;

namespace SpotifyTaskbarWidget.Services;

/// <summary>
/// Adaptive polling engine for Spotify playback state.
/// Adjusts polling frequency based on playback activity:
///   - Active playback: every 1 second
///   - Paused: every 10 seconds  
///   - Idle (no player): every 30 seconds
/// Uses exponential backoff on repeated API errors.
/// </summary>
public class PlaybackPollingService : IDisposable
{
    // ─── Polling Intervals ───────────────────────────────────────────────

    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PausedInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoffInterval = TimeSpan.FromMinutes(2);

    // ─── State ───────────────────────────────────────────────────────────

    private readonly SpotifyClient _client;
    private readonly DispatcherTimer _timer;
    private PollingState _currentState = (PollingState)(-1);
    private int _consecutiveErrors;
    private PlaybackState? _lastPlaybackState;
    private bool _isRunning;

    // ─── Events ──────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the playback state changes (new track, play/pause, etc.).
    /// </summary>
    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

    /// <summary>
    /// Raised when the connection to Spotify is lost (repeated errors).
    /// </summary>
    public event EventHandler? ConnectionLost;

    public PlaybackState? LastPlaybackState => _lastPlaybackState;
    public bool IsRunning => _isRunning;

    public PlaybackPollingService(SpotifyClient client)
    {
        _client = client;
        _timer = new DispatcherTimer
        {
            Interval = IdleInterval
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// Starts the polling loop.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _consecutiveErrors = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(100); // First poll immediately
        _timer.Start();
        Debug.WriteLine("[Polling] Started.");
    }

    /// <summary>
    /// Stops the polling loop.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _timer.Stop();
        Logger.Log("[Polling] Stopped.");
    }

    /// <summary>
    /// Forces an immediate poll (e.g., after the user presses play/pause).
    /// </summary>
    public async Task ForceRefreshAsync()
    {
        // Brief delay to let Spotify process the command
        await Task.Delay(300);
        await PollAsync();
    }

    // ─── Private ─────────────────────────────────────────────────────────

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await PollAsync();
    }

    private async Task PollAsync()
    {
        try
        {
            var newState = await _client.GetCurrentPlaybackAsync();
            _consecutiveErrors = 0;

            var previousState = _lastPlaybackState;
            _lastPlaybackState = newState;

            // Determine new polling state
            PollingState newPollingState;
            if (newState == null)
            {
                newPollingState = PollingState.Idle;
            }
            else if (newState.IsPlaying)
            {
                newPollingState = PollingState.Active;
            }
            else
            {
                newPollingState = PollingState.Paused;
            }

            // Update timer interval if state changed
            if (newPollingState != _currentState)
            {
                _currentState = newPollingState;
                _timer.Interval = GetIntervalForState(newPollingState);
                Logger.Log($"[Polling] State → {newPollingState}, Interval → {_timer.Interval.TotalSeconds}s");
            }

            // Notify listeners if playback state meaningfully changed
            if (HasStateChanged(previousState, newState))
            {
                PlaybackStateChanged?.Invoke(this, new PlaybackStateChangedEventArgs(newState, previousState));
            }
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            Logger.Log($"[Polling] Error #{_consecutiveErrors}: {ex.Message}");

            if (_consecutiveErrors >= 5)
            {
                ConnectionLost?.Invoke(this, EventArgs.Empty);
            }

            // Exponential backoff on errors
            var backoff = TimeSpan.FromSeconds(Math.Min(
                Math.Pow(2, _consecutiveErrors) * 2,
                MaxBackoffInterval.TotalSeconds));
            _timer.Interval = backoff;
        }
    }

    private static TimeSpan GetIntervalForState(PollingState state) => state switch
    {
        PollingState.Active => ActiveInterval,
        PollingState.Paused => PausedInterval,
        PollingState.Idle => IdleInterval,
        _ => IdleInterval
    };

    /// <summary>
    /// Determines if the playback state has changed meaningfully
    /// (ignoring minor progress changes during active playback).
    /// </summary>
    private static bool HasStateChanged(PlaybackState? previous, PlaybackState? current)
    {
        if (previous == null && current == null)
            return false;
        if (previous == null || current == null)
            return true;

        return previous.TrackName != current.TrackName
               || previous.ArtistName != current.ArtistName
               || previous.IsPlaying != current.IsPlaying
               || previous.AlbumArtUrl != current.AlbumArtUrl
               || previous.VolumePercent != current.VolumePercent;
    }

    public void Dispose()
    {
        Stop();
    }

    private enum PollingState
    {
        Idle,
        Active,
        Paused
    }
}

/// <summary>
/// Event arguments for playback state changes.
/// </summary>
public class PlaybackStateChangedEventArgs : EventArgs
{
    public PlaybackState? CurrentState { get; }
    public PlaybackState? PreviousState { get; }

    public PlaybackStateChangedEventArgs(PlaybackState? current, PlaybackState? previous)
    {
        CurrentState = current;
        PreviousState = previous;
    }
}
