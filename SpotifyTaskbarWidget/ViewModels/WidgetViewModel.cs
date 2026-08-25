using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpotifyTaskbarWidget.Services;
using SpotifyTaskbarWidget.Spotify;

namespace SpotifyTaskbarWidget.ViewModels;

/// main ViewModel for the taskbar widget.
/// binds to the TaskbarWidgetControl and drives all display and interaction logic.
public class WidgetViewModel : INotifyPropertyChanged
{
    // servcies

    private readonly SpotifyClient _client;
    private readonly PlaybackPollingService _pollingService;
    private readonly HttpClient _imageHttpClient;

    // backing fields

    private string _trackName = "Not Playing";
    private string _artistName = string.Empty;
    private bool _isPlaying;
    private ImageSource? _albumArt;
    private bool _hasTrack;
    private bool _isIdle = true;
    private double _progressPercent;
    private string? _currentAlbumArtUrl;

    // public properties

    public string TrackName
    {
        get => _trackName;
        set => SetProperty(ref _trackName, value);
    }

    public string ArtistName
    {
        get => _artistName;
        set => SetProperty(ref _artistName, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (SetProperty(ref _isPlaying, value))
                OnPropertyChanged(nameof(PlayPauseIcon));
        }
    }

    public ImageSource? AlbumArt
    {
        get => _albumArt;
        set => SetProperty(ref _albumArt, value);
    }

    public bool HasTrack
    {
        get => _hasTrack;
        set => SetProperty(ref _hasTrack, value);
    }

    public bool IsIdle
    {
        get => _isIdle;
        set => SetProperty(ref _isIdle, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    public string PlayPauseIcon => IsPlaying
        ? "M 6,2 L 6,14 L 10,14 L 10,2 Z M 12,2 L 12,14 L 16,14 L 16,2 Z" // Pause
        : "M 5,2 L 5,14 L 16,8 Z"; // Play

    public ICommand PlayPauseCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand PreviousTrackCommand { get; }

    // constructor
    public WidgetViewModel(SpotifyClient client, PlaybackPollingService pollingService)
    {
        _client = client;
        _pollingService = pollingService;
        _imageHttpClient = new HttpClient();

        PlayPauseCommand = new RelayCommand(async () => await TogglePlayPauseAsync());
        NextTrackCommand = new RelayCommand(async () => await NextTrackAsync());
        PreviousTrackCommand = new RelayCommand(async () => await PreviousTrackAsync());

        // sub to playback state changes
        _pollingService.PlaybackStateChanged += OnPlaybackStateChanged;

        // load last state if available
        var lastState = _pollingService.LastPlaybackState;
        if (lastState != null)
        {
            UpdateFromPlaybackState(lastState);
        }
    }

    // command handlers

    private async Task TogglePlayPauseAsync()
    {
        bool success;
        if (IsPlaying)
        {
            success = await _client.PauseAsync();
            if (success) IsPlaying = false;
        }
        else
        {
            success = await _client.PlayAsync();
            if (success) IsPlaying = true;
        }

        if (success)
        {
            await _pollingService.ForceRefreshAsync();
        }
    }

    private async Task NextTrackAsync()
    {
        var success = await _client.NextTrackAsync();
        if (success)
        {
            await _pollingService.ForceRefreshAsync();
        }
    }

    private async Task PreviousTrackAsync()
    {
        var success = await _client.PreviousTrackAsync();
        if (success)
        {
            await _pollingService.ForceRefreshAsync();
        }
    }

    // state update handlers
    private void OnPlaybackStateChanged(object? sender, PlaybackStateChangedEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (e.CurrentState != null)
            {
                UpdateFromPlaybackState(e.CurrentState);
            }
            else
            {
                IsIdle = true;
                IsPlaying = false;
            }
        });
    }

    private void UpdateFromPlaybackState(PlaybackState state)
    {
        TrackName = string.IsNullOrEmpty(state.TrackName) ? "Not Playing" : state.TrackName;
        ArtistName = state.ArtistName;
        IsPlaying = state.IsPlaying;
        HasTrack = !string.IsNullOrEmpty(state.TrackName);
        IsIdle = !HasTrack;

        if (state.DurationMs > 0)
        {
            ProgressPercent = (double)state.ProgressMs / state.DurationMs * 100;
        }

        // Load album art if URL changed
        if (state.AlbumArtUrl != _currentAlbumArtUrl)
        {
            _currentAlbumArtUrl = state.AlbumArtUrl;
            _ = LoadAlbumArtAsync(state.AlbumArtUrl);
        }
    }

    private async Task LoadAlbumArtAsync(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            AlbumArt = null;
            return;
        }

        try
        {
            var imageBytes = await _imageHttpClient.GetByteArrayAsync(url);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(imageBytes);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64; // small for taskbar
            image.EndInit();
            image.Freeze(); // req for cross-thread use

            Application.Current?.Dispatcher.Invoke(() =>
            {
                AlbumArt = image;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WidgetVM] Failed to load album art: {ex.Message}");
        }
    }

    // INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

/// simple ICommand implementation for async actions.
public class RelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (_isExecuting)
            return;

        _isExecuting = true;
        try
        {
            await _executeAsync();
        }
        finally
        {
            _isExecuting = false;
        }
    }
}
