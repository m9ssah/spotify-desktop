using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpotifyTaskbarWidget.Services;

/// detects and monitors the Windows 11 system theme (dark/light mode, accent color)
/// listens for WM_SETTINGCHANGE to detect live theme switches
/// provides resources that the widget UI binds to for native-looking styling
public class ThemeService : INotifyPropertyChangedBase, IDisposable
{
    // registry keys for theme settings
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    // state
    private bool _isDarkMode;
    private Color _accentColor;
    private bool _isAccentOnTaskbar;
    private HwndSource? _hwndSource;

    // properties
    public bool IsDarkMode
    {
        get => _isDarkMode;
        private set => SetProperty(ref _isDarkMode, value);
    }

    public Color AccentColor
    {
        get => _accentColor;
        private set => SetProperty(ref _accentColor, value);
    }

    public bool IsAccentOnTaskbar
    {
        get => _isAccentOnTaskbar;
        private set => SetProperty(ref _isAccentOnTaskbar, value);
    }


    public Color ForegroundColor => IsDarkMode ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A);

    public Color SecondaryForegroundColor => IsDarkMode
        ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) // 70% white
        : Color.FromArgb(0xB3, 0x1A, 0x1A, 0x1A); // 70% dark

    public Color HoverColor => IsDarkMode
        ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) // 20% white
        : Color.FromArgb(0x33, 0x00, 0x00, 0x00); // 20% black

    public Color TaskbarBackgroundColor
    {
        get
        {
            if (IsAccentOnTaskbar)
                return AccentColor;

            return IsDarkMode
                ? Color.FromArgb(0x00, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF);
        }
    }

    public void Initialize()
    {
        ReadThemeSettings();

        // register for WM_SETTINGCHANGE via a message-only window
        var parameters = new HwndSourceParameters("SpotifyThemeMonitor")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        Debug.WriteLine($"[ThemeService] Initialized: DarkMode={IsDarkMode}, AccentOnTaskbar={IsAccentOnTaskbar}");
    }

    private void ReadThemeSettings()
    {
        try
        {
            using var personalizeKey = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (personalizeKey != null)
            {
                // SystemUsesLightTheme: 0 = dark, 1 = light
                var systemTheme = personalizeKey.GetValue("SystemUsesLightTheme");
                IsDarkMode = systemTheme is int value && value == 0;

                // ColorPrevalence: 1 = accent color on taskbar
                var prevalence = personalizeKey.GetValue("ColorPrevalence");
                IsAccentOnTaskbar = prevalence is int prevValue && prevValue == 1;
            }

            using var dwmKey = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (dwmKey != null)
            {
                var accentColorValue = dwmKey.GetValue("AccentColor");
                if (accentColorValue is int colorInt)
                {
                    // ABGR format from registry
                    var a = (byte)((colorInt >> 24) & 0xFF);
                    var b = (byte)((colorInt >> 16) & 0xFF);
                    var g = (byte)((colorInt >> 8) & 0xFF);
                    var r = (byte)(colorInt & 0xFF);
                    AccentColor = Color.FromArgb(a, r, g, b);
                }
                else
                {
                    AccentColor = Color.FromRgb(0x00, 0x78, 0xD4);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeService] Error reading theme: {ex.Message}");
            IsDarkMode = true;
            AccentColor = Color.FromRgb(0x00, 0x78, 0xD4);
        }
    }

    /// listens for WM_SETTINGCHANGE and WM_THEMECHANGED to detect theme switches.
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint message = (uint)msg;

        if (message == Interop.Win32.WM_SETTINGCHANGE || message == Interop.Win32.WM_THEMECHANGED)
        {
            Debug.WriteLine("[ThemeService] Theme change detected, re-reading settings...");
            ReadThemeSettings();

            UpdateApplicationResources();
        }

        return IntPtr.Zero;
    }

    public void UpdateApplicationResources()
    {
        var app = Application.Current;
        if (app == null) return;

        app.Resources["WidgetForeground"] = new SolidColorBrush(ForegroundColor);
        app.Resources["WidgetSecondaryForeground"] = new SolidColorBrush(SecondaryForegroundColor);
        app.Resources["WidgetHoverBackground"] = new SolidColorBrush(HoverColor);
        app.Resources["WidgetBackground"] = new SolidColorBrush(TaskbarBackgroundColor);
        app.Resources["WidgetAccentColor"] = new SolidColorBrush(AccentColor);
    }

    public void Dispose()
    {
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }
}
public class INotifyPropertyChangedBase : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
