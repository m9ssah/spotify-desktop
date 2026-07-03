using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
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
    // TODO: Move to a settings file or environment variable
    private const string SpotifyClientId = "b00d5b38887d4e9b9d7fb78df1cf86a2";

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
    private Window? _dummyWindow;
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

        // ── Create Dummy Window for Tray Icon Focus ──────────────────
        _dummyWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Left = -9999,
            Top = -9999,
        };
        _dummyWindow.Show();
        MainWindow = _dummyWindow;

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
        if (_dummyWindow == null) return;
        _trayIcon = new TaskbarNotificationIcon(_dummyWindow);

        // Context menu
        var contextMenu = new ContextMenu();

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (s, e) => ShowSettings();
        contextMenu.Items.Add(settingsItem);

        var autoStartItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = _startupService?.IsAutoStartEnabled ?? false
        };
        autoStartItem.Click += (s, e) =>
        {
            _startupService?.ToggleAutoStart();
            autoStartItem.IsChecked = _startupService?.IsAutoStartEnabled ?? false;
        };
        contextMenu.Items.Add(autoStartItem);

        contextMenu.Items.Add(new Separator());

        var reauthItem = new MenuItem { Header = "Re-authenticate" };
        reauthItem.Click += async (s, e) => await ReauthenticateAsync();
        contextMenu.Items.Add(reauthItem);

        contextMenu.Items.Add(new Separator());

        var aboutItem = new MenuItem { Header = "About" };
        aboutItem.Click += (s, e) =>
        {
            MessageBox.Show("Spotify Taskbar Widget\nVersion 1.0\n\nEmbeds Spotify controls directly in your Windows 11 taskbar.",
                "About", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        contextMenu.Items.Add(aboutItem);

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (s, e) => Shutdown();
        contextMenu.Items.Add(quitItem);

        _trayIcon.ContextMenu = contextMenu;
        _trayIcon.ToolTipText = "Spotify Taskbar Widget";
        _trayIcon.Visibility = Visibility.Visible;
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
        _trayIcon?.Dispose();
        _httpClient?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _widgetWindow?.Close();
        _dummyWindow?.Close();

        base.OnExit(e);
    }
}

/// <summary>
/// Lightweight wrapper around Hardcodet TaskbarIcon to handle the tray notification icon.
/// Uses WPF-native approach instead of WinForms NotifyIcon.
/// </summary>
internal class TaskbarNotificationIcon : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TaskbarNotificationIcon(Window owner)
    {
        _icon = new TaskbarIcon();

        // Attach the TaskbarIcon to the dummy window's visual tree.
        // This gives the tray ContextMenu a valid top-level window parent
        // so it can receive focus properly and won't flash/close instantly.
        if (owner.Content is Panel panel)
        {
            panel.Children.Add(_icon);
        }
        else
        {
            var grid = new Grid();
            grid.Children.Add(_icon);
            owner.Content = grid;
        }

        // Try to load custom icon via WPF ImageSource
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/spotify-icon.ico", UriKind.Absolute);
            var iconBitmap = new System.Windows.Media.Imaging.BitmapImage(iconUri);
            _icon.IconSource = iconBitmap;
        }
        catch
        {
            // No custom icon available — Hardcodet will use a default
        }
    }

    public ContextMenu? ContextMenu
    {
        get => _icon.ContextMenu;
        set => _icon.ContextMenu = value;
    }

    public string? ToolTipText
    {
        get => _icon.ToolTipText;
        set => _icon.ToolTipText = value;
    }

    public Visibility Visibility
    {
        get => _icon.Visibility;
        set => _icon.Visibility = value;
    }

    public void Dispose()
    {
        _icon.Dispose();
    }
}
