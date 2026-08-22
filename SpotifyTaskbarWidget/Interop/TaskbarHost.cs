using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SpotifyTaskbarWidget.Interop;

/// discovers the taskbar window hierarchy, injects our window as a child,
/// and handles repositioning on resize/DPI changes.
public class TaskbarHost : IDisposable
{
    private IntPtr _taskbarHwnd;
    private IntPtr _trayNotifyHwnd;
    private IntPtr _rebarHwnd;
    private IntPtr _taskSwHwnd;
    private IntPtr _widgetHwnd;
    private Window? _hostWindow;
    private readonly DispatcherTimer _repositionTimer;
    private bool _isEmbedded;
    private int _widgetWidth = 320;
    private int _widgetRightMargin = 8;
    private (int X, int Y, int Width, int Height)? _lastPlacement;

    public event EventHandler? EmbeddingLost;
    public event EventHandler? EmbeddingRestored;

    public bool IsEmbedded => _isEmbedded;

    public TaskbarHost()
    {
        _repositionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _repositionTimer.Tick += OnRepositionTimerTick;
    }

    public bool Embed(Window hostWindow)
    {
        _hostWindow = hostWindow;
        _lastPlacement = null;

        Logger.Log("[TaskbarHost] Starting Embed sequence...");
        if (!DiscoverTaskbar())
        {
            Logger.Log("[TaskbarHost] Failed to discover taskbar windows.");
            return false;
        }

        // get the WPF window's HWND
        var helper = new WindowInteropHelper(hostWindow);
        helper.EnsureHandle();
        _widgetHwnd = helper.Handle;

        if (_widgetHwnd == IntPtr.Zero)
        {
            Logger.Log("[TaskbarHost] Widget HWND is zero.");
            return false;
        }

        Logger.Log($"[TaskbarHost] Widget HWND: {_widgetHwnd}");

        // apply child window styles
        ApplyChildStyles(_widgetHwnd);

        // reparent into the taskbar
        Logger.Log($"[TaskbarHost] Setting parent to Taskbar HWND: {_taskbarHwnd}");
        var previousParent = Win32.SetParent(_widgetHwnd, _taskbarHwnd);
        if (previousParent == IntPtr.Zero)
        {
            Logger.Log($"[TaskbarHost] SetParent failed. Error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }
        Logger.Log($"[TaskbarHost] Reparented. Previous parent was: {previousParent}");

        RepositionWidget();

        Win32.ShowWindow(_widgetHwnd, Win32.SW_SHOW);
        Logger.Log("[TaskbarHost] Win32.ShowWindow called on widget HWND.");

        _isEmbedded = true;
        _repositionTimer.Start();

        Logger.Log("[TaskbarHost] Successfully embedded widget in taskbar.");
        EmbeddingRestored?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Unembed()
    {
        _repositionTimer.Stop();

        if (_widgetHwnd != IntPtr.Zero && _isEmbedded)
        {
            Win32.SetParent(_widgetHwnd, IntPtr.Zero);
            Win32.ShowWindow(_widgetHwnd, Win32.SW_HIDE);
            _isEmbedded = false;
        }
    }

    public bool TryReembed()
    {
        if (_hostWindow == null)
            return false;

        _isEmbedded = false;
        return Embed(_hostWindow);
    }

    private bool DiscoverTaskbar()
    {
        // Shell_TrayWnd: main taskbar window
        _taskbarHwnd = Win32.FindWindow("Shell_TrayWnd", null);
        if (_taskbarHwnd == IntPtr.Zero)
        {
            Logger.Log("[TaskbarHost] Shell_TrayWnd not found.");
            return false;
        }

        // TrayNotifyWnd: system tray area (clock, notification icons)
        _trayNotifyHwnd = Win32.FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);

        // ReBarWindow32: contains the task button area
        _rebarHwnd = Win32.FindWindowEx(_taskbarHwnd, IntPtr.Zero, "ReBarWindow32", null);

        // MSTaskSwWClass: task switching buttons area
        if (_rebarHwnd != IntPtr.Zero)
        {
            _taskSwHwnd = Win32.FindWindowEx(_rebarHwnd, IntPtr.Zero, "MSTaskSwWClass", null);
        }

        Logger.Log($"[TaskbarHost] Discovered: Taskbar={_taskbarHwnd}, TrayNotify={_trayNotifyHwnd}, " +
                         $"Rebar={_rebarHwnd}, TaskSw={_taskSwHwnd}");

        return _taskbarHwnd != IntPtr.Zero;
    }

    private static void ApplyChildStyles(IntPtr hwnd)
    {
        var style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        style &= ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME);
        style |= Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, style);

        var exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        exStyle &= ~Win32.WS_EX_APPWINDOW;
        exStyle |= Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle);

        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
    }

    private void RepositionWidget()
    {
        if (_taskbarHwnd == IntPtr.Zero || _widgetHwnd == IntPtr.Zero)
            return;

        // taskbar bounds
        Win32.GetWindowRect(_taskbarHwnd, out var taskbarRect);
        int taskbarHeight = taskbarRect.Height;

        // DPI scaling factor
        var dpi = Win32.GetDpiForWindow(_taskbarHwnd);
        double scaleFactor = dpi / 96.0;
        int scaledWidth = (int)(_widgetWidth * scaleFactor);
        int scaledMargin = (int)(_widgetRightMargin * scaleFactor);

        int x;
        if (_trayNotifyHwnd != IntPtr.Zero)
        {
            Win32.GetWindowRect(_trayNotifyHwnd, out var trayRect);
            int trayLeftRelative = trayRect.Left - taskbarRect.Left;
            x = trayLeftRelative - scaledWidth - scaledMargin;
        }
        else
        {
            x = taskbarRect.Width - scaledWidth - scaledMargin - 200;
        }

        x = Math.Max(x, 0);

        int verticalPadding = (int)(4 * scaleFactor);
        int y = verticalPadding;
        int height = taskbarHeight - (verticalPadding * 2);

        var placement = (x, y, scaledWidth, height);
        if (placement == _lastPlacement)
            return;
        _lastPlacement = placement;

        Logger.Log($"[TaskbarHost] RepositionWidget: SetWindowPos to x={x}, y={y}, width={scaledWidth}, height={height} (Taskbar width={taskbarRect.Width}, height={taskbarHeight})");
        Win32.SetWindowPos(_widgetHwnd, Win32.HWND_TOP, x, y, scaledWidth, height, Win32.SWP_SHOWWINDOW | Win32.SWP_NOACTIVATE);
    }

    private void OnRepositionTimerTick(object? sender, EventArgs e)
    {
        if (!_isEmbedded)
            return;

        if (!Win32.IsWindow(_taskbarHwnd))
        {
            _isEmbedded = false;
            EmbeddingLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        var currentParent = Win32.GetParent(_widgetHwnd);
        if (currentParent != _taskbarHwnd)
        {
            _isEmbedded = false;
            EmbeddingLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        RepositionWidget();
    }

    public void Dispose()
    {
        _repositionTimer.Stop();
        Unembed();
    }
}
