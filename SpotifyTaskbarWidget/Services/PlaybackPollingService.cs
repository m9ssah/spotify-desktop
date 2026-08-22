using System.Diagnostics;
using System.Windows.Threading;
using SpotifyTaskbarWidget.Spotify;

namespace SpotifyTaskbarWidget.Services;

public class PlaybackPollingService : IDisposable
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PausedInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoffInterval = TimeSpan.FromMinutes(2);

    private readonly SpotifyClient _client;
    private readonly DispatcherTimer _timer;
    private PollingState _currentState = (PollingState)(-1);
    private int _consecutiveErrors;
    private PlaybackState? _lastPlaybackState;
    private bool _isRunning;

    public event EventHandler<PlaybackStateChangedEventArgs>? PlaybackStateChanged;

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

    public void Start()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        _consecutiveErrors = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Start();
        Debug.WriteLine("[Polling] Started.");
    }

    public void Stop()
    {
        _isRunning = false;
        _timer.Stop();
        Logger.Log("[Polling] Stopped.");
    }

    public async Task ForceRefreshAsync()
    {
        await Task.Delay(300);
        await PollAsync();
    }

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

            if (newPollingState != _currentState)
            {
                _currentState = newPollingState;
                _timer.Interval = GetIntervalForState(newPollingState);
                Logger.Log($"[Polling] State → {newPollingState}, Interval → {_timer.Interval.TotalSeconds}s");
            }

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
