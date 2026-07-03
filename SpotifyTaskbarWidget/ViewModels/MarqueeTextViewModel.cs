using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace SpotifyTaskbarWidget.ViewModels;

/// <summary>
/// Provides scrolling marquee behavior for text that overflows its container.
/// Manages a TranslateTransform offset that the View binds to.
/// </summary>
public class MarqueeTextViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _scrollTimer;
    private double _scrollOffset;
    private double _textWidth;
    private double _containerWidth;
    private bool _isScrolling;
    private string _text = string.Empty;
    private bool _needsScroll;

    // Scroll speed: pixels per tick
    private const double ScrollSpeed = 1.0;
    // Pause at start/end of scroll in ticks
    private const int PauseTicks = 60;
    // Tick interval
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(30);

    private int _pauseCounter;
    private ScrollDirection _direction = ScrollDirection.Left;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                ResetScroll();
            }
        }
    }

    public double ScrollOffset
    {
        get => _scrollOffset;
        private set => SetProperty(ref _scrollOffset, value);
    }

    public bool NeedsScroll
    {
        get => _needsScroll;
        private set => SetProperty(ref _needsScroll, value);
    }

    public MarqueeTextViewModel()
    {
        _scrollTimer = new DispatcherTimer
        {
            Interval = TickInterval
        };
        _scrollTimer.Tick += OnScrollTick;
    }

    /// <summary>
    /// Called by the View when the text or container dimensions are measured.
    /// </summary>
    public void UpdateDimensions(double textWidth, double containerWidth)
    {
        _textWidth = textWidth;
        _containerWidth = containerWidth;

        bool shouldScroll = textWidth > containerWidth + 2; // 2px tolerance
        NeedsScroll = shouldScroll;

        if (shouldScroll && !_isScrolling)
        {
            StartScroll();
        }
        else if (!shouldScroll && _isScrolling)
        {
            StopScroll();
        }
    }

    private void StartScroll()
    {
        _isScrolling = true;
        _direction = ScrollDirection.Left;
        _pauseCounter = PauseTicks; // Pause at start
        ScrollOffset = 0;
        _scrollTimer.Start();
    }

    private void StopScroll()
    {
        _isScrolling = false;
        _scrollTimer.Stop();
        ScrollOffset = 0;
    }

    private void ResetScroll()
    {
        StopScroll();
        // Dimensions will be re-measured by the View
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        if (_pauseCounter > 0)
        {
            _pauseCounter--;
            return;
        }

        double maxOffset = _textWidth - _containerWidth;
        if (maxOffset <= 0)
        {
            StopScroll();
            return;
        }

        if (_direction == ScrollDirection.Left)
        {
            ScrollOffset -= ScrollSpeed;
            if (Math.Abs(ScrollOffset) >= maxOffset)
            {
                ScrollOffset = -maxOffset;
                _direction = ScrollDirection.Right;
                _pauseCounter = PauseTicks;
            }
        }
        else
        {
            ScrollOffset += ScrollSpeed;
            if (ScrollOffset >= 0)
            {
                ScrollOffset = 0;
                _direction = ScrollDirection.Left;
                _pauseCounter = PauseTicks;
            }
        }
    }

    private enum ScrollDirection
    {
        Left,
        Right
    }

    // ─── INotifyPropertyChanged ──────────────────────────────────────────

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
