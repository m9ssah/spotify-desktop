using System.Text.Json.Serialization;

namespace SpotifyTaskbarWidget.Spotify;

/// <summary>
/// Represents the current Spotify playback state displayed by the widget.
/// </summary>
public class PlaybackState
{
    public string TrackName { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string AlbumName { get; set; } = string.Empty;
    public string? AlbumArtUrl { get; set; }
    public bool IsPlaying { get; set; }
    public int ProgressMs { get; set; }
    public int DurationMs { get; set; }
    public int VolumePercent { get; set; }
    public bool ShuffleState { get; set; }
    public string RepeatState { get; set; } = "off";
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }

    /// <summary>
    /// Creates a PlaybackState from the Spotify Web API response DTO.
    /// </summary>
    public static PlaybackState FromApiResponse(SpotifyPlaybackResponse response)
    {
        var track = response.Item;
        var state = new PlaybackState
        {
            IsPlaying = response.IsPlaying,
            ProgressMs = response.ProgressMs,
            ShuffleState = response.ShuffleState,
            RepeatState = response.RepeatState ?? "off",
            VolumePercent = response.Device?.VolumePercent ?? 50,
            DeviceId = response.Device?.Id,
            DeviceName = response.Device?.Name,
        };

        if (track != null)
        {
            state.TrackName = track.Name ?? string.Empty;
            state.DurationMs = track.DurationMs;
            state.AlbumName = track.Album?.Name ?? string.Empty;

            // Join multiple artists
            if (track.Artists is { Count: > 0 })
            {
                state.ArtistName = string.Join(", ", track.Artists.Select(a => a.Name));
            }

            // Pick the smallest album art image (closest to 64px for taskbar)
            if (track.Album?.Images is { Count: > 0 })
            {
                state.AlbumArtUrl = track.Album.Images
                    .OrderBy(i => i.Width)
                    .First().Url;
            }
        }

        return state;
    }
}

// ─── Spotify Web API Response DTOs ───────────────────────────────────────

public class SpotifyPlaybackResponse
{
    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("progress_ms")]
    public int ProgressMs { get; set; }

    [JsonPropertyName("shuffle_state")]
    public bool ShuffleState { get; set; }

    [JsonPropertyName("repeat_state")]
    public string? RepeatState { get; set; }

    [JsonPropertyName("device")]
    public SpotifyDevice? Device { get; set; }

    [JsonPropertyName("item")]
    public SpotifyTrack? Item { get; set; }
}

public class SpotifyDevice
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("volume_percent")]
    public int VolumePercent { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }
}

public class SpotifyTrack
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("artists")]
    public List<SpotifyArtist>? Artists { get; set; }

    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }
}

public class SpotifyArtist
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public class SpotifyAlbum
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("images")]
    public List<SpotifyImage>? Images { get; set; }
}

public class SpotifyImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}
