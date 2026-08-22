using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using SpotifyTaskbarWidget.Interop;
using SpotifyTaskbarWidget.Services;
using SpotifyTaskbarWidget.Spotify;
using SpotifyTaskbarWidget.ViewModels;
using SpotifyTaskbarWidget.Views;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Application entry point and orchestrator.
/// Manages the lifecycle: single instance → auth → taskbar embedding → polling → tray icon.
/// </summary>
public partial class App : Application
{
    // ─── Configuration ───────────────────────────────────────────────────
    private static readonly string SpotifyClientId =
        Config.Get("SPOTIFY_CLIENT_ID");

    // ─── Core Components ─────────────────────────────────────────────────

    private Mutex? _singleInstanceMutex;
    private TaskbarNotificationIcon? _trayIcon;
    private TaskbarHost? _taskbarHost;
    private ShellHook? _shellHook;
    private ThemeService? _themeService;
    private StartupService? _startupService;
    private PlaybackPollingService? _pollingService;
    private SpotifyClient? _spotifyClient;
    private SpotifyAuth? _spotifyAuth;
    private TokenStore? _tokenStore;
    private Window? _widgetWindow;
    private Thread? _trayThread;
    private Dispatcher? _trayDispatcher;
    private WidgetViewModel? _viewModel;
    private HttpClient? _httpClient;

    // ─── Startup ─────────────────────────────────────────────────────────

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Single Instance Check ────────────────────────────────────
        _singleInstanceMutex = new Mutex(true, "SpotifyTaskbarWidget_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Spotify Taskbar Widget is already running.",
                "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] Startup failed: {ex}");
            MessageBox.Show($"Failed to start: {ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task InitializeAsync()
    {
        // ── Initialize Services ──────────────────────────────────────
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget/1.0 (Windows NT 10.0; Win64; x64)");
        _tokenStore = new TokenStore();
        _spotifyAuth = new SpotifyAuth(SpotifyClientId, _tokenStore, _httpClient);
        _startupService = new StartupService();
        _themeService = new ThemeService();
        _themeService.Initialize();

        // ── Authenticate ─────────────────────────────────────────────
        var tokens = await _spotifyAuth.TryLoadExistingTokensAsync();

        if (tokens == null)
        {
            // Show auth window
            var authWindow = new AuthWindow(_spotifyAuth);
            var result = authWindow.ShowDialog();

            if (result != true || authWindow.ResultTokens == null)
            {
                Debug.WriteLine("[App] Auth cancelled or failed. Shutting down.");
                Shutdown();
                return;
            }

            tokens = authWindow.ResultTokens;
        }

        // ── Initialize Spotify Client ────────────────────────────────
        _spotifyClient = new SpotifyClient(_spotifyAuth, tokens, _httpClient);

        // ── Log User Profile for Diagnostics ─────────────────────────
        try
        {
            var profile = await _spotifyClient.GetUserProfileAsync();
            if (profile != null)
            {
                Logger.Log($"[App] Authorized User: ID={profile.Id}, Name={profile.DisplayName}, Product={profile.Product ?? "unknown"}");
            }
            else
            {
                Logger.Log("[App] Failed to fetch authorized user profile.");

                _ = Dispatcher.BeginInvoke(() => MessageBox.Show(
                    "Spotify rejected API access for this account (403 Forbidden).\n\n" +
                    "If this app is in Development Mode, add this Spotify account's name and email " +
                    "under Settings → User Management in the Spotify Developer Dashboard " +
                    "(developer.spotify.com/dashboard), then use Re-authenticate from the tray menu.",
                    "Spotify Access Denied", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"[App] User profile fetch exception: {ex.Message}");
        }

        // ── Initialize Polling ───────────────────────────────────────
        _pollingService = new PlaybackPollingService(_spotifyClient);

        // ── Create ViewModel ─────────────────────────────────────────
        _viewModel = new WidgetViewModel(_spotifyClient, _pollingService);

        // ── Create Widget Window ─────────────────────────────────────
        _widgetWindow = CreateWidgetWindow();

        // ── Update Theme Resources ───────────────────────────────────
        _themeService.UpdateApplicationResources();

        // ── Embed in Taskbar ─────────────────────────────────────────
        _taskbarHost = new TaskbarHost();
        _taskbarHost.EmbeddingLost += OnEmbeddingLost;

        Logger.Log("[App] Attempting to embed widget window in taskbar...");
        bool embedded = _taskbarHost.Embed(_widgetWindow);
        if (!embedded)
        {
            Logger.Log("[App] Failed to embed in taskbar. Running as floating window fallback.");
            _widgetWindow.WindowStyle = WindowStyle.None;
            _widgetWindow.AllowsTransparency = true;
            _widgetWindow.Background = System.Windows.Media.Brushes.Transparent;
            _widgetWindow.Topmost = true;
            _widgetWindow.Width = 320;
            _widgetWindow.Height = 48;
            _widgetWindow.Left = SystemParameters.WorkArea.Right - 340;
            _widgetWindow.Top = SystemParameters.WorkArea.Bottom - 10;
            _widgetWindow.Show();
        }
        else
        {
            Logger.Log("[App] Widget successfully embedded.");
        }

        // ── Shell Hook (explorer restart detection) ──────────────────
        _shellHook = new ShellHook();
        _shellHook.TaskbarRecreated += OnTaskbarRecreated;
        _shellHook.Initialize();

        // ── System Tray Icon ─────────────────────────────────────────
        SetupTrayIcon();

        // ── Start Polling ────────────────────────────────────────────
        _pollingService.Start();

        // ── Auto-start (first run) ───────────────────────────────────
        if (!_startupService.IsAutoStartEnabled)
        {
            _startupService.EnableAutoStart();
        }

        Logger.Log("[App] Initialization complete.");
    }

    /// <summary>
    /// Creates the host window for the widget control.
    /// This window will be reparented into the taskbar.
    /// </summary>
    private Window CreateWidgetWindow()
    {
        var widgetControl = new TaskbarWidgetControl
        {
            DataContext = _viewModel
        };

        var window = new Window
        {
            Content = widgetControl,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 320,
            Height = 48,
            // Start offscreen — will be repositioned by TaskbarHost
            Left = -9999,
            Top = -9999,
        };

        // Force the window to create its handle and render the visual tree.
        // We do NOT call window.Hide() here, because doing so makes WPF stop rendering the visual tree.
        window.Show();

        return window;
    }

    // ─── System Tray Icon ────────────────────────────────────────────────
    private void SetupTrayIcon()
    {
        var ready = new ManualResetEventSlim();

        _trayThread = new Thread(() =>
        {
            try
            {
                _trayDispatcher = Dispatcher.CurrentDispatcher;
                _trayIcon = new TaskbarNotificationIcon
                {
                    ContextMenu = BuildTrayMenu(),
                    ToolTipText = "Spotify Taskbar Widget",
                    Visible = true,
                };
            }
            catch (Exception ex)
            {
                // Never let this kill the process — the app is still usable without a tray icon.
                Logger.Log($"[Tray] Tray icon setup failed: {ex}");
                return;
            }
            finally
            {
                // Must always fire, or startup blocks forever on ready.Wait().
                ready.Set();
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "TrayIcon",
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();
        ready.Wait();
    }

    /// <summary>Runs an action on the main UI thread (menu handlers fire on the tray thread).</summary>
    private void OnUiThread(Action action) => Dispatcher.BeginInvoke(action);

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();

        var settingsItem = new Forms.ToolStripMenuItem("Settings");
        settingsItem.Click += (s, e) => OnUiThread(ShowSettings);
        menu.Items.Add(settingsItem);

        var autoStartItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = _startupService?.IsAutoStartEnabled ?? false
        };
        autoStartItem.Click += (s, e) =>
        {
            _startupService?.ToggleAutoStart();
            autoStartItem.Checked = _startupService?.IsAutoStartEnabled ?? false;
        };
        menu.Items.Add(autoStartItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var reauthItem = new Forms.ToolStripMenuItem("Re-authenticate");
        reauthItem.Click += (s, e) => OnUiThread(() => _ = ReauthenticateAsync());
        menu.Items.Add(reauthItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var aboutItem = new Forms.ToolStripMenuItem("About");
        aboutItem.Click += (s, e) => OnUiThread(() =>
            MessageBox.Show("Spotify Taskbar Widget\nVersion 1.0\n\nEmbeds Spotify controls directly in your Windows 11 taskbar.",
                "About", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(aboutItem);

        var quitItem = new Forms.ToolStripMenuItem("Quit");
        quitItem.Click += (s, e) =>
        {
            Logger.Log("[Tray] Quit clicked.");
            OnUiThread(Shutdown);
        };
        menu.Items.Add(quitItem);

        return menu;
    }

    // ─── Event Handlers ──────────────────────────────────────────────────

    private void OnEmbeddingLost(object? sender, EventArgs e)
    {
        Debug.WriteLine("[App] Taskbar embedding lost. Waiting for shell hook to re-embed...");
    }

    private void OnTaskbarRecreated(object? sender, EventArgs e)
    {
        Debug.WriteLine("[App] Taskbar recreated. Re-embedding widget...");
        Dispatcher.Invoke(() =>
        {
            _taskbarHost?.TryReembed();
        });
    }

    private async Task ReauthenticateAsync()
    {
        _pollingService?.Stop();
        _tokenStore?.Delete();

        if (_spotifyAuth != null)
        {
            var authWindow = new AuthWindow(_spotifyAuth);
            var result = authWindow.ShowDialog();

            if (result == true && authWindow.ResultTokens != null)
            {
                _spotifyClient?.SetTokens(authWindow.ResultTokens);
                _pollingService?.Start();
            }
        }
    }

    private void ShowSettings()
    {
        // Placeholder for future settings window
        MessageBox.Show("Settings will be available in a future update.",
            "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ─── Shutdown ────────────────────────────────────────────────────────

    protected override void OnExit(ExitEventArgs e)
    {
        Debug.WriteLine("[App] Shutting down...");

        _pollingService?.Dispose();
        _taskbarHost?.Dispose();
        _shellHook?.Dispose();
        _themeService?.Dispose();

        // The tray icon lives on its own thread — dispose it there so the icon is
        // removed from the notification area instead of lingering as a ghost.
        _trayDispatcher?.Invoke(() => _trayIcon?.Dispose());
        _trayDispatcher?.InvokeShutdown();

        _httpClient?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _widgetWindow?.Close();

        base.OnExit(e);
    }
}

/// <summary>
/// Wrapper around the WinForms NotifyIcon.
/// WinForms is used rather than a WPF tray library because this icon is created on a
/// non-main thread (see App.SetupTrayIcon), and WPF tray libraries touch
/// Application.Current, which throws off the main thread.
/// </summary>
internal class TaskbarNotificationIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TaskbarNotificationIcon()
    {
        _icon = new Forms.NotifyIcon { Icon = LoadIcon() };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            // The .ico is embedded as the executable's application icon.
            var path = Environment.ProcessPath;
            if (path != null)
                return System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            // Fall through to the stock icon.
        }
        return System.Drawing.SystemIcons.Application;
    }

    public Forms.ContextMenuStrip? ContextMenu
    {
        get => _icon.ContextMenuStrip;
        set => _icon.ContextMenuStrip = value;
    }

    public string ToolTipText
    {
        get => _icon.Text;
        set => _icon.Text = value;
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    public void Dispose()
    {
        // Hide before disposing, or the icon lingers in the tray as a ghost.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
