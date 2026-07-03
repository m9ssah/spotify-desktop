using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace SpotifyTaskbarWidget.Interop;

/// <summary>
/// Monitors for explorer.exe restarts (which destroy and recreate the taskbar)
/// and raises an event so the widget can re-embed itself.
/// Uses the RegisterShellHookWindow API to detect when Shell_TrayWnd is recreated.
/// </summary>
public class ShellHook : IDisposable
{
    private HwndSource? _hwndSource;
    private uint _shellHookMessage;
    private bool _isRegistered;

    /// <summary>
    /// Raised when explorer.exe has restarted and the taskbar has been recreated.
    /// The subscriber should attempt to re-embed the widget.
    /// </summary>
    public event EventHandler? TaskbarRecreated;

    /// <summary>
    /// The "TaskbarCreated" message registered by Windows shell.
    /// This is broadcast when explorer.exe restarts.
    /// </summary>
    private uint _taskbarCreatedMessage;

    /// <summary>
    /// Initializes the shell hook by creating a message-only window and registering for shell notifications.
    /// Call this after the WPF application has started.
    /// </summary>
    public void Initialize()
    {
        // Register the Windows "TaskbarCreated" message
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");

        // Create a hidden message-only window for receiving shell hook messages
        var parameters = new HwndSourceParameters("SpotifyShellHook")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE — message-only window
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        // Register for shell hook messages
        if (_hwndSource.Handle != IntPtr.Zero)
        {
            _isRegistered = Win32.RegisterShellHookWindow(_hwndSource.Handle);
            _shellHookMessage = Win32.RegisterWindowMessage("SHELLHOOK");

            Debug.WriteLine($"[ShellHook] Registered: {_isRegistered}, " +
                             $"ShellHookMsg: {_shellHookMessage}, " +
                             $"TaskbarCreatedMsg: {_taskbarCreatedMessage}");
        }
    }

    /// <summary>
    /// Processes window messages looking for taskbar recreation signals.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint message = (uint)msg;

        // Check for the "TaskbarCreated" broadcast — most reliable indicator of explorer restart
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            Debug.WriteLine("[ShellHook] TaskbarCreated message received — explorer.exe restarted.");

            // Delay slightly to let explorer finish creating all taskbar windows
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(2000); // Wait for taskbar to fully initialize
                TaskbarRecreated?.Invoke(this, EventArgs.Empty);
            });

            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_hwndSource != null)
        {
            if (_isRegistered)
            {
                Win32.DeregisterShellHookWindow(_hwndSource.Handle);
                _isRegistered = false;
            }

            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
