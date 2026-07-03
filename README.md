# Spotify Taskbar Widget

A native Windows 11 widget that embeds Spotify playback controls directly inside your taskbar. Built with C# WPF and Win32 interop (P/Invoke).

![widget-preview](https://img.shields.io/badge/Windows_11-Taskbar_Widget-0078D4?style=for-the-badge&logo=windows11&logoColor=white)

## Features

- **Taskbar-Embedded**: Lives inside the Windows 11 taskbar as a native-feeling widget — not a floating window
- **Real-time Playback**: Shows currently playing track with album art, song title (scrolling marquee), and artist name
- **Playback Controls**: Play/Pause, Next, Previous track buttons
- **Adaptive Polling**: Smart API polling — 1s during playback, 10s paused, 30s idle
- **Theme Matching**: Automatically matches Windows 11 dark/light mode and accent colors
- **Explorer Resilience**: Auto-re-embeds when explorer.exe restarts
- **Idle State**: Shows last played track greyed out when nothing is playing
- **Secure Auth**: OAuth 2.0 PKCE flow — no client secret stored locally
- **Auto-Start**: Launches with Windows (configurable via tray icon menu)
- **System Tray**: Right-click tray icon for settings, re-auth, and quit

## Prerequisites

- Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or build from source with .NET SDK)
- A [Spotify Premium](https://www.spotify.com/premium/) account (required for Web API playback control)
- A Spotify Developer application (see Setup)

## Setup

### 1. Create a Spotify Developer App

1. Go to [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
2. Click **Create App**
3. Set the **Redirect URI** to: `http://localhost:5543/callback`
4. Note your **Client ID**

### 2. Configure the Widget

Open `SpotifyTaskbarWidget/App.xaml.cs` and replace the placeholder Client ID:

```csharp
private const string SpotifyClientId = "YOUR_CLIENT_ID_HERE";
```

### 3. Build & Run

```bash
# Restore packages
dotnet restore

# Build
dotnet build

# Run
dotnet run --project SpotifyTaskbarWidget
```

### 4. Publish (Optional)

Create a self-contained single-file executable:

```bash
dotnet publish SpotifyTaskbarWidget -c Release -r win-x64 --self-contained
```

## Architecture

```
SpotifyTaskbarWidget/
├── Interop/           # Win32 P/Invoke: FindWindow, SetParent, shell hooks
├── Spotify/           # OAuth PKCE, Web API client, token storage (DPAPI)
├── Services/          # Adaptive polling, theme detection, auto-start
├── ViewModels/        # MVVM: widget state, marquee text scrolling
├── Views/             # WPF controls: inline widget, auth window
├── Converters/        # Bool→Visibility, Bool→Opacity
└── Resources/         # Styles (Windows 11 Segoe UI Variable, theme brushes)
```

### How the Taskbar Embedding Works

1. **Discovery**: Finds `Shell_TrayWnd` → `TrayNotifyWnd` in the taskbar window hierarchy
2. **Style Change**: Removes `WS_POPUP`, adds `WS_CHILD` and `WS_EX_TOOLWINDOW`
3. **Reparent**: `SetParent(widgetHwnd, taskbarHwnd)` to inject as a child window
4. **Position**: Placed between the task icons and system tray area
5. **Monitor**: Timer checks for detachment; shell hook detects explorer restarts

## System Tray Menu

Right-click the tray icon for:
- **Start with Windows** — Toggle auto-start
- **Re-authenticate** — Log in again with a different Spotify account
- **About** — Version information
- **Quit** — Exit the widget

## License

[MIT](LICENSE)
