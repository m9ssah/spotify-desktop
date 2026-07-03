# Spotify Taskbar Widget — Implementation Plan

A C# WPF application that embeds a Spotify playback control widget directly inside the Windows 11 taskbar using Win32 interop (P/Invoke). The widget shows album art, song info, and playback controls inline, styled to match the native Windows 11 taskbar theme.

## User Review Required

> [!WARNING]
> **Spotify Developer App Registration Required**: You will need to create a Spotify Developer application at [developer.spotify.com](https://developer.spotify.com/dashboard) and provide a **Client ID**. The redirect URI should be set to `http://localhost:5543/callback`. This is mandatory for the OAuth PKCE flow.

> [!IMPORTANT]
> **Taskbar Injection Risk**: Injecting a child window into `explorer.exe`'s taskbar via `SetParent` is an undocumented technique. Windows Updates may break this approach. We'll implement defensive coding with automatic fallback (re-inject on explorer restart), but this is inherently fragile.

## Proposed Changes

### Solution Structure

The project will be organized as a single WPF application with clean separation of concerns:

```
spotify-desktop/
├── SpotifyTaskbarWidget/
│   ├── SpotifyTaskbarWidget.csproj          # .NET 8, WPF, single-file publish
│   ├── App.xaml / App.xaml.cs               # Application entry, single instance, tray icon
│   ├── Interop/
│   │   ├── Win32.cs                         # P/Invoke declarations (FindWindow, SetParent, etc.)
│   │   ├── TaskbarHost.cs                   # Taskbar discovery, child window injection, positioning
│   │   └── ShellHook.cs                     # Shell hook for explorer.exe restart detection
│   ├── Spotify/
│   │   ├── SpotifyAuth.cs                   # OAuth 2.0 PKCE flow with localhost redirect listener
│   │   ├── SpotifyClient.cs                 # Web API client (playback state, controls, volume)
│   │   ├── PlaybackState.cs                 # Model for current playback data
│   │   └── TokenStore.cs                    # Secure token persistence (DPAPI-encrypted)
│   ├── ViewModels/
│   │   ├── WidgetViewModel.cs               # Main VM: track info, playback state, commands
│   │   └── MarqueeTextViewModel.cs          # Scrolling text logic for long titles
│   ├── Views/
│   │   ├── TaskbarWidgetControl.xaml/.cs     # The inline taskbar widget (48px tall)
│   │   └── AuthWindow.xaml/.cs              # OAuth login window (shown on first launch)
│   ├── Services/
│   │   ├── PlaybackPollingService.cs        # Adaptive polling (1s/10s/30s)
│   │   ├── ThemeService.cs                  # Windows 11 theme detection (dark/light/accent)
│   │   └── StartupService.cs               # Registry auto-start management
│   ├── Converters/
│   │   └── BoolToVisibilityConverter.cs     # Standard WPF converters
│   ├── Resources/
│   │   ├── Icons/                           # App icon, tray icon, playback control icons
│   │   └── Styles.xaml                      # Windows 11-matching styles and templates
│   └── Assets/
│       └── spotify-icon.ico                 # Application icon
├── README.md                                # Updated documentation
└── .gitignore                               # Updated for .NET
```

---

### Win32 Interop Layer

#### [NEW] [Win32.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Interop/Win32.cs)

P/Invoke declarations for all required Win32 APIs:
- `FindWindow` / `FindWindowEx` — Locate taskbar windows (`Shell_TrayWnd`, `ReBarWindow32`, `MSTaskSwWClass`, `TrayNotifyWnd`)
- `SetParent` — Reparent our WPF window as a child of the taskbar
- `SetWindowLong` / `GetWindowLong` — Modify window styles (`WS_CHILD`, remove `WS_POPUP`, etc.)
- `SetWindowPos` — Position and size the widget within the taskbar
- `GetWindowRect` — Query taskbar and tray notification area bounds for positioning
- `SendMessage` — Communication with shell windows
- `RegisterShellHookWindow` / `RegisterWindowMessage("SHELLHOOK")` — Detect explorer.exe restarts
- `DwmSetWindowAttribute` — Apply Mica/dark mode attributes

#### [NEW] [TaskbarHost.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Interop/TaskbarHost.cs)

Core embedding logic:
1. **Discovery**: Walk the taskbar window hierarchy:
   - `Shell_TrayWnd` → top-level taskbar
   - `ReBarWindow32` → rebar container
   - `MSTaskSwWClass` → task button area
   - `TrayNotifyWnd` → system tray area
2. **Injection**: 
   - Create a WPF `HwndSource` or use `WindowInteropHelper` to get our HWND
   - Call `SetWindowLong` to set `WS_CHILD` style and remove `WS_POPUP` / `WS_CAPTION`
   - Call `SetParent(ourHwnd, taskbarHwnd)` to reparent
3. **Positioning**: Calculate position between `MSTaskSwWClass` right edge and `TrayNotifyWnd` left edge
4. **Monitoring**: Timer-based repositioning check (handles taskbar resize, resolution changes)

#### [NEW] [ShellHook.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Interop/ShellHook.cs)

Handles `explorer.exe` crash/restart scenarios:
- Register for shell hook messages
- Detect `HSHELL_WINDOWCREATED` for new `Shell_TrayWnd`
- Re-trigger the injection pipeline on explorer restart

---

### Spotify API Layer

#### [NEW] [SpotifyAuth.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Spotify/SpotifyAuth.cs)

OAuth 2.0 PKCE implementation:
- Generate `code_verifier` and `code_challenge` (SHA256)
- Open system browser to Spotify `/authorize` endpoint with scopes: `user-read-playback-state`, `user-modify-playback-state`, `user-read-currently-playing`
- Start a temporary `HttpListener` on `http://localhost:5543/callback` to capture the auth code
- Exchange code for access/refresh tokens
- Auto-refresh tokens before expiry using the refresh token

#### [NEW] [SpotifyClient.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Spotify/SpotifyClient.cs)

Spotify Web API wrapper using `HttpClient`:
- `GetCurrentPlaybackAsync()` → GET `/v1/me/player` — returns full playback state
- `PauseAsync()` / `PlayAsync()` → PUT `/v1/me/player/pause` and `/play`
- `NextTrackAsync()` / `PreviousTrackAsync()` → POST `/v1/me/player/next` and `/previous`
- `SetVolumeAsync(int percent)` → PUT `/v1/me/player/volume?volume_percent={percent}`
- Handles 401 → auto-refresh token → retry
- Handles 429 → respect `Retry-After` header

#### [NEW] [PlaybackState.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Spotify/PlaybackState.cs)

Data model:
- `TrackName`, `ArtistName`, `AlbumName`
- `AlbumArtUrl` (smallest image for taskbar, ~64px)
- `IsPlaying`, `ProgressMs`, `DurationMs`
- `VolumePercent`
- `ShuffleState`, `RepeatState`

#### [NEW] [TokenStore.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Spotify/TokenStore.cs)

Secure token persistence:
- Store tokens in `%APPDATA%\SpotifyTaskbarWidget\tokens.dat`
- Encrypt using `ProtectedData` (DPAPI) — ties tokens to the current Windows user
- Load on startup, save on token refresh

---

### UI Layer

#### [NEW] [TaskbarWidgetControl.xaml](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Views/TaskbarWidgetControl.xaml)

The core inline widget — constrained to ~48px height, approximately 300-350px wide:

```
┌──────────────────────────────────────────────────────┐
│ ┌────┐  Song Title >>>  │  ◄◄  ▶/❚❚  ►►  │          │  ← 48px tall
│ │ 🎵 │  Artist Name     │                  │          │
│ └────┘                   │                  │          │
└──────────────────────────────────────────────────────┘
  40x40    ~180px text       ~100px controls
  album    area (marquee)    (3 buttons)
```

Key XAML features:
- `Grid` layout with 3 columns (art | text | controls)
- Album art: `Image` control, 40×40px, rounded corners (4px radius)
- Song title: Custom `TextBlock` with `TranslateTransform` animation for marquee scrolling
- Artist: Single-line `TextBlock`, ellipsis truncation
- Controls: `Button` elements with `Path` geometry icons (no image dependencies)
- All elements use `{DynamicResource}` for theme-aware colors

#### [NEW] [Styles.xaml](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Resources/Styles.xaml)

Windows 11 native-matching resource dictionary:
- **Colors**: Read from `SystemParameters` / Registry for taskbar accent color
- **Fonts**: `Segoe UI Variable` (Windows 11 system font), 12px body, 11px caption
- **Buttons**: Transparent background, subtle hover highlight (`#20FFFFFF` for dark, `#20000000` for light)
- **Animations**: 150ms ease-out transitions for hover states

#### [NEW] [AuthWindow.xaml](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Views/AuthWindow.xaml)

Minimal OAuth window shown on first launch:
- Spotify branding
- "Connect to Spotify" button → opens system browser
- Status text showing auth progress
- Auto-closes on successful authentication

---

### Services

#### [NEW] [PlaybackPollingService.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Services/PlaybackPollingService.cs)

Adaptive polling engine:
- Uses `DispatcherTimer` with dynamic interval
- **Active playback**: Poll every 1 second
- **Paused**: Poll every 10 seconds
- **Idle/No player**: Poll every 30 seconds
- Emits `PlaybackStateChanged` event consumed by the ViewModel
- Handles API errors gracefully (exponential backoff on repeated failures)

#### [NEW] [ThemeService.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Services/ThemeService.cs)

Windows 11 theme integration:
- Read `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`:
  - `SystemUsesLightTheme` — overall system theme
  - `AppsUseLightTheme` — app-specific theme
  - `ColorPrevalence` — whether accent color is applied to taskbar
- Read `HKCU\Software\Microsoft\Windows\DWM\AccentColor` for accent color
- Listen for `WM_SETTINGCHANGE` to detect live theme switches
- Expose `IsDarkMode`, `AccentColor`, `TaskbarBackground` properties

#### [NEW] [StartupService.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/Services/StartupService.cs)

Auto-start management:
- Read/write `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key
- `EnableAutoStart()` / `DisableAutoStart()` / `IsAutoStartEnabled`
- Uses the published executable path as the value

---

### Application Entry

#### [NEW] [App.xaml.cs](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/SpotifyTaskbarWidget/App.xaml.cs)

Application orchestrator:
- **Single instance enforcement** via named `Mutex`
- **System tray icon** using `System.Windows.Forms.NotifyIcon` (WPF doesn't have native tray support):
  - Context menu: Settings | Re-authenticate | About | Quit
- **Startup flow**:
  1. Check for existing tokens → if valid, skip auth
  2. If no tokens / expired refresh token → show `AuthWindow`
  3. Initialize `TaskbarHost` → find and inject into taskbar
  4. Start `PlaybackPollingService`
  5. Show system tray icon
- **Shutdown**: Cleanup `SetParent(ourHwnd, IntPtr.Zero)`, dispose tray icon, save state

---

### Supporting Files

#### [MODIFY] [README.md](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/README.md)

Update to reflect the C# WPF architecture, setup instructions (Spotify Developer app, Client ID configuration), build & run instructions.

#### [MODIFY] [.gitignore](file:///c:/Users/massah/Documents/GitHub/spotify-desktop/.gitignore)

Update for .NET/C# project (bin/, obj/, *.user, .vs/, etc.).

#### [NEW] SpotifyTaskbarWidget.sln

Solution file for the project.

---

## Key Technical Details

### Taskbar Window Hierarchy (Windows 11)

```
Shell_TrayWnd                          ← Main taskbar window
├── TrayNotifyWnd                      ← System tray (clock, icons)
│   ├── TrayClockWClass
│   └── SysPager
├── ReBarWindow32                      ← Rebar container
│   └── MSTaskSwWClass                 ← Task buttons (pinned + running apps)
├── Windows.UI.Composition.DesktopWindowContentBridge  ← XAML Island
└── Start                             ← Start button
```

Our widget will be injected as a child of `Shell_TrayWnd` and positioned using absolute coordinates calculated from the bounds of `MSTaskSwWClass` and `TrayNotifyWnd`.

### Window Style Manipulation

```csharp
// Remove top-level styles
var style = GetWindowLong(hwnd, GWL_STYLE);
style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME);
style |= WS_CHILD;
SetWindowLong(hwnd, GWL_STYLE, style);

// Remove extended styles
var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
exStyle &= ~(WS_EX_APPWINDOW);
exStyle |= WS_EX_TOOLWINDOW; // Hide from Alt+Tab
SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

// Reparent
SetParent(hwnd, taskbarHwnd);
```

### NuGet Dependencies

| Package | Purpose |
|---|---|
| `System.Text.Json` | JSON deserialization of Spotify API responses (included in .NET 8) |
| `System.Security.Cryptography.ProtectedData` | DPAPI token encryption |
| `Hardcodet.NotifyIcon.Wpf` | Better WPF-native tray icon (alternative to WinForms interop) |

---

## Verification Plan

### Build Verification
```bash
dotnet build SpotifyTaskbarWidget/SpotifyTaskbarWidget.csproj
```

### Manual Verification
1. **First launch**: Auth window appears → browser opens Spotify login → token captured → widget appears in taskbar
2. **Playback controls**: Play/pause/next/prev correctly control Spotify playback
3. **Theme matching**: Switch Windows from dark to light mode → widget updates live
4. **Explorer restart**: Kill `explorer.exe` → restart → widget re-injects automatically
5. **Idle state**: Stop Spotify → widget shows last track greyed out
6. **Auto-start**: Reboot → widget appears automatically in taskbar
7. **System tray**: Right-click tray icon → all menu items work

### Automated Tests
- Unit tests for `SpotifyClient` response parsing
- Unit tests for `TokenStore` encrypt/decrypt roundtrip
- Unit tests for adaptive polling interval logic
