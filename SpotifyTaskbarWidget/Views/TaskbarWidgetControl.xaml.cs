using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SpotifyTaskbarWidget.ViewModels;

namespace SpotifyTaskbarWidget.Views;

/// <summary>
/// Code-behind for the TaskbarWidgetControl.
/// Handles marquee text measurement and scroll updates.
/// </summary>
public partial class TaskbarWidgetControl : UserControl
{
    private readonly MarqueeTextViewModel _marqueeVM;
    private readonly DispatcherTimer _measureTimer;

    public TaskbarWidgetControl()
    {
        InitializeComponent();

        _marqueeVM = new MarqueeTextViewModel();

        // Subscribe to marquee scroll offset changes
        _marqueeVM.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MarqueeTextViewModel.ScrollOffset))
            {
                TrackNameTranslate.X = _marqueeVM.ScrollOffset;
            }
        };

        // Periodically measure the track name text for marquee
        _measureTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _measureTimer.Tick += OnMeasureTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _measureTimer.Start();
        MeasureTrackName();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _measureTimer.Stop();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is WidgetViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(WidgetViewModel.TrackName))
                {
                    _marqueeVM.Text = vm.TrackName;
                    Dispatcher.InvokeAsync(MeasureTrackName, DispatcherPriority.Loaded);
                }
            };

            // Initialize with current track name
            _marqueeVM.Text = vm.TrackName;
        }
    }

    private void OnMeasureTick(object? sender, EventArgs e)
    {
        MeasureTrackName();
    }

    /// <summary>
    /// Measures the actual rendered width of the track name text and the container
    /// to determine if marquee scrolling is needed.
    /// </summary>
    private void MeasureTrackName()
    {
        if (TrackNameText == null)
            return;

        TrackNameText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = TrackNameText.DesiredSize.Width;

        // The parent Canvas is our container
        var containerWidth = TrackNameText.Parent is Canvas canvas ? canvas.ActualWidth : 0;

        if (containerWidth > 0)
        {
            _marqueeVM.UpdateDimensions(textWidth, containerWidth);
        }
    }
}
