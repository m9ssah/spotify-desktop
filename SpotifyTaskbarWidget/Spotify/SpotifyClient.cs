using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SpotifyTaskbarWidget.Spotify;

/// <summary>
/// Spotify Web API client — handles playback state queries and control commands.
/// Automatically refreshes tokens on 401 responses.
/// </summary>
public class SpotifyClient
{
    private const string BaseUrl = "https://api.spotify.com/v1";

    private readonly HttpClient _httpClient;
    private readonly SpotifyAuth _auth;
    private TokenData? _tokens;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public SpotifyClient(SpotifyAuth auth, TokenData? initialTokens, HttpClient? httpClient = null)
    {
        _auth = auth;
        _tokens = initialTokens;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Updates the tokens (e.g., after a fresh login).
    /// </summary>
    public void SetTokens(TokenData tokens)
    {
        _tokens = tokens;
    }

    /// <summary>
    /// Returns true if we have valid tokens available.
    /// </summary>
    public bool HasValidTokens => _tokens != null && !string.IsNullOrEmpty(_tokens.AccessToken);

    // ─── Playback State ──────────────────────────────────────────────────

    /// <summary>
    /// Gets the current playback state from Spotify.
    /// Returns null if no active playback session or on error.
    /// </summary>
    public async Task<PlaybackState?> GetCurrentPlaybackAsync()
    {
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/me/player");

            if (response == null)
                return null;

            // 204 = no active device / nothing playing
            if (response.StatusCode == HttpStatusCode.NoContent)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"[SpotifyClient] GetPlayback failed: {response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var apiResponse = JsonSerializer.Deserialize<SpotifyPlaybackResponse>(json);

            if (apiResponse == null)
                return null;

            return PlaybackState.FromApiResponse(apiResponse);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SpotifyClient] GetPlayback exception: {ex.Message}");
            return null;
        }
    }

    // ─── Playback Controls ───────────────────────────────────────────────

    /// <summary>
    /// Resumes playback on the active device.
    /// </summary>
    public async Task<bool> PlayAsync()
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, "/me/player/play");
        return response?.IsSuccessStatusCode ?? false;
    }

    /// <summary>
    /// Pauses playback on the active device.
    /// </summary>
    public async Task<bool> PauseAsync()
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put, "/me/player/pause");
        return response?.IsSuccessStatusCode ?? false;
    }

    /// <summary>
    /// Skips to the next track.
    /// </summary>
    public async Task<bool> NextTrackAsync()
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/me/player/next");
        return response?.IsSuccessStatusCode ?? false;
    }

    /// <summary>
    /// Skips to the previous track.
    /// </summary>
    public async Task<bool> PreviousTrackAsync()
    {
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Post, "/me/player/previous");
        return response?.IsSuccessStatusCode ?? false;
    }

    /// <summary>
    /// Sets the volume (0–100).
    /// </summary>
    public async Task<bool> SetVolumeAsync(int volumePercent)
    {
        volumePercent = Math.Clamp(volumePercent, 0, 100);
        var response = await SendAuthenticatedRequestAsync(HttpMethod.Put,
            $"/me/player/volume?volume_percent={volumePercent}");
        return response?.IsSuccessStatusCode ?? false;
    }

    // ─── Authenticated Request Handler ───────────────────────────────────

    /// <summary>
    /// Sends an authenticated request to the Spotify API.
    /// Handles token refresh on 401 and retry-after on 429.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAuthenticatedRequestAsync(
        HttpMethod method, string endpoint, HttpContent? content = null, bool isRetry = false)
    {
        if (_tokens == null)
        {
            Debug.WriteLine("[SpotifyClient] No tokens available.");
            return null;
        }

        // Auto-refresh if expired
        if (_tokens.IsExpired)
        {
            await RefreshTokensAsync();
            if (_tokens == null || _tokens.IsExpired)
                return null;
        }

        var request = new HttpRequestMessage(method, $"{BaseUrl}{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);

        if (content != null)
            request.Content = content;

        try
        {
            var response = await _httpClient.SendAsync(request);

            // Handle 401 — token expired mid-request
            if (response.StatusCode == HttpStatusCode.Unauthorized && !isRetry)
            {
                Debug.WriteLine("[SpotifyClient] Got 401, refreshing token...");
                await RefreshTokensAsync();
                return await SendAuthenticatedRequestAsync(method, endpoint, content, isRetry: true);
            }

            // Handle 429 — rate limited
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                {
                    Debug.WriteLine($"[SpotifyClient] Rate limited. Retry after: {retryAfter.TotalSeconds}s");
                    await Task.Delay(retryAfter);
                    return await SendAuthenticatedRequestAsync(method, endpoint, content, isRetry: true);
                }
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            Debug.WriteLine($"[SpotifyClient] Request exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Thread-safe token refresh.
    /// </summary>
    private async Task RefreshTokensAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (_tokens != null && !_tokens.IsExpired)
                return; // Already refreshed by another thread

            if (_tokens?.RefreshToken != null)
            {
                var refreshed = await _auth.RefreshTokenAsync(_tokens.RefreshToken);
                if (refreshed != null)
                {
                    _tokens = refreshed;
                    Debug.WriteLine("[SpotifyClient] Tokens refreshed successfully.");
                }
                else
                {
                    Debug.WriteLine("[SpotifyClient] Token refresh failed.");
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
