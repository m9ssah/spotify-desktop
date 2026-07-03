using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SpotifyTaskbarWidget.Interop;

/// <summary>
/// Manages embedding our WPF widget inside the Windows 11 taskbar.
/// Discovers the taskbar window hierarchy, injects our window as a child,
/// and handles repositioning on resize/DPI changes.
/// </summary>
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

    /// <summary>
    /// Discovers the taskbar windows and embeds the given WPF window.
    /// </summary>
    public bool Embed(Window hostWindow)
    {
        _hostWindow = hostWindow;

        Logger.Log("[TaskbarHost] Starting Embed sequence...");
        if (!DiscoverTaskbar())
        {
            Logger.Log("[TaskbarHost] Failed to discover taskbar windows.");
            return false;
        }

        // Get the WPF window's HWND
        var helper = new WindowInteropHelper(hostWindow);
        helper.EnsureHandle();
        _widgetHwnd = helper.Handle;

        if (_widgetHwnd == IntPtr.Zero)
        {
            Logger.Log("[TaskbarHost] Widget HWND is zero.");
            return false;
        }

        Logger.Log($"[TaskbarHost] Widget HWND: {_widgetHwnd}");

        // Apply child window styles
        ApplyChildStyles(_widgetHwnd);

        // Reparent into the taskbar
        Logger.Log($"[TaskbarHost] Setting parent to Taskbar HWND: {_taskbarHwnd}");
        var previousParent = Win32.SetParent(_widgetHwnd, _taskbarHwnd);
        if (previousParent == IntPtr.Zero)
        {
            Logger.Log($"[TaskbarHost] SetParent failed. Error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return false;
        }
        Logger.Log($"[TaskbarHost] Reparented. Previous parent was: {previousParent}");

        // Position the widget
        RepositionWidget();

        // Show it
        Win32.ShowWindow(_widgetHwnd, Win32.SW_SHOW);
        Logger.Log("[TaskbarHost] Win32.ShowWindow called on widget HWND.");

        _isEmbedded = true;
        _repositionTimer.Start();

        Logger.Log("[TaskbarHost] Successfully embedded widget in taskbar.");
        EmbeddingRestored?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Removes the widget from the taskbar.
    /// </summary>
    public void Unembed()
    {
        _repositionTimer.Stop();

        if (_widgetHwnd != IntPtr.Zero && _isEmbedded)
        {
            // Reparent back to desktop
            Win32.SetParent(_widgetHwnd, IntPtr.Zero);
            Win32.ShowWindow(_widgetHwnd, Win32.SW_HIDE);
            _isEmbedded = false;
        }
    }

    /// <summary>
    /// Attempts to re-embed after an explorer restart.
    /// </summary>
    public bool TryReembed()
    {
        if (_hostWindow == null)
            return false;

        _isEmbedded = false;
        return Embed(_hostWindow);
    }

    /// <summary>
    /// Walk the taskbar window hierarchy to find key windows.
    /// </summary>
    private bool DiscoverTaskbar()
    {
        // Shell_TrayWnd is the main taskbar window
        _taskbarHwnd = Win32.FindWindow("Shell_TrayWnd", null);
        if (_taskbarHwnd == IntPtr.Zero)
        {
            Logger.Log("[TaskbarHost] Shell_TrayWnd not found.");
            return false;
        }

        // TrayNotifyWnd is the system tray area (clock, notification icons)
        _trayNotifyHwnd = Win32.FindWindowEx(_taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);

        // ReBarWindow32 contains the task button area
        _rebarHwnd = Win32.FindWindowEx(_taskbarHwnd, IntPtr.Zero, "ReBarWindow32", null);

        // MSTaskSwWClass is the actual task switching buttons area
        if (_rebarHwnd != IntPtr.Zero)
        {
            _taskSwHwnd = Win32.FindWindowEx(_rebarHwnd, IntPtr.Zero, "MSTaskSwWClass", null);
        }

        Logger.Log($"[TaskbarHost] Discovered: Taskbar={_taskbarHwnd}, TrayNotify={_trayNotifyHwnd}, " +
                         $"Rebar={_rebarHwnd}, TaskSw={_taskSwHwnd}");

        return _taskbarHwnd != IntPtr.Zero;
    }

    /// <summary>
    /// Applies WS_CHILD styles and removes top-level window decorations.
    /// </summary>
    private static void ApplyChildStyles(IntPtr hwnd)
    {
        // Modify window style: remove popup/caption, add child
        var style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        style &= ~(Win32.WS_POPUP | Win32.WS_CAPTION | Win32.WS_THICKFRAME);
        style |= Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;
        Win32.SetWindowLong(hwnd, Win32.GWL_STYLE, style);

        // Modify extended style: hide from Alt+Tab, remove app window behavior
        var exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        exStyle &= ~Win32.WS_EX_APPWINDOW;
        exStyle |= Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, exStyle);

        // Force style update
        Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
    }

    /// <summary>
    /// Calculates the correct position and moves the widget.
    /// Position: just to the left of the system tray notification area.
    /// </summary>
    private void RepositionWidget()
    {
        if (_taskbarHwnd == IntPtr.Zero || _widgetHwnd == IntPtr.Zero)
            return;

        // Get taskbar bounds
        Win32.GetWindowRect(_taskbarHwnd, out var taskbarRect);
        int taskbarHeight = taskbarRect.Height;

        // Get DPI scaling factor
        var dpi = Win32.GetDpiForWindow(_taskbarHwnd);
        double scaleFactor = dpi / 96.0;
        int scaledWidth = (int)(_widgetWidth * scaleFactor);
        int scaledMargin = (int)(_widgetRightMargin * scaleFactor);

        // Calculate X position: to the left of the system tray
        int x;
        if (_trayNotifyHwnd != IntPtr.Zero)
        {
            Win32.GetWindowRect(_trayNotifyHwnd, out var trayRect);
            // Convert tray left from screen coords to taskbar-relative coords
            int trayLeftRelative = trayRect.Left - taskbarRect.Left;
            x = trayLeftRelative - scaledWidth - scaledMargin;
        }
        else
        {
            // Fallback: position near right edge of taskbar
            x = taskbarRect.Width - scaledWidth - scaledMargin - 200;
        }

        // Ensure we don't go negative
        x = Math.Max(x, 0);

        // Center vertically in taskbar with small padding
        int verticalPadding = (int)(4 * scaleFactor);
        int y = verticalPadding;
        int height = taskbarHeight - (verticalPadding * 2);

        Logger.Log($"[TaskbarHost] RepositionWidget: SetWindowPos to x={x}, y={y}, width={scaledWidth}, height={height} (Taskbar width={taskbarRect.Width}, height={taskbarHeight})");
        Win32.SetWindowPos(_widgetHwnd, Win32.HWND_TOP, x, y, scaledWidth, height, Win32.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Periodically checks if the widget is still embedded and repositions if needed.
    /// </summary>
    private void OnRepositionTimerTick(object? sender, EventArgs e)
    {
        if (!_isEmbedded)
            return;

        // Check if taskbar still exists
        if (!Win32.IsWindow(_taskbarHwnd))
        {
            _isEmbedded = false;
            EmbeddingLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Check if our window is still a child of the taskbar
        var currentParent = Win32.GetParent(_widgetHwnd);
        if (currentParent != _taskbarHwnd)
        {
            _isEmbedded = false;
            EmbeddingLost?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Reposition (handles resolution/DPI/taskbar resize changes)
        RepositionWidget();
    }

    public void Dispose()
    {
        _repositionTimer.Stop();
        Unembed();
    }
}
