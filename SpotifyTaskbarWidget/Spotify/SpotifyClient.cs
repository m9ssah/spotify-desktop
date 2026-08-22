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
        
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget/1.0 (Windows NT 10.0; Win64; x64)");
        }
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
                Logger.Log($"[SpotifyClient] GetPlayback failed: {response.StatusCode}");
                
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Logger.Log("[SpotifyClient] Got 403 Forbidden on player state. Retrying with /currently-playing fallback...");
                    return await GetCurrentlyPlayingOnlyAsync();
                }

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
            Logger.Log($"[SpotifyClient] GetPlayback exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fallback to get only the currently playing track (supported for Free accounts).
    /// </summary>
    private async Task<PlaybackState?> GetCurrentlyPlayingOnlyAsync()
    {
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/me/player/currently-playing");

            if (response == null)
                return null;

            if (response.StatusCode == HttpStatusCode.NoContent)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                Logger.Log($"[SpotifyClient] GetCurrentlyPlayingOnly failed: {response.StatusCode}");
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
            Logger.Log($"[SpotifyClient] GetCurrentlyPlayingOnly exception: {ex.Message}");
            return null;
        }
    }

    // ─── Playback Controls ───────────────────────────────────────────────

    /// <summary>Resumes playback on the active device.</summary>
    public Task<bool> PlayAsync() => ControlAsync(HttpMethod.Put, "/me/player/play");

    /// <summary>Pauses playback on the active device.</summary>
    public Task<bool> PauseAsync() => ControlAsync(HttpMethod.Put, "/me/player/pause");

    /// <summary>Skips to the next track.</summary>
    public Task<bool> NextTrackAsync() => ControlAsync(HttpMethod.Post, "/me/player/next");

    /// <summary>Skips to the previous track.</summary>
    public Task<bool> PreviousTrackAsync() => ControlAsync(HttpMethod.Post, "/me/player/previous");

    /// <summary>Sets the volume (0–100).</summary>
    public Task<bool> SetVolumeAsync(int volumePercent) => ControlAsync(HttpMethod.Put,
        $"/me/player/volume?volume_percent={Math.Clamp(volumePercent, 0, 100)}");

    /// <summary>
    /// Issues a playback control command. Failures here are otherwise invisible —
    /// the button just does nothing — so the API's reason is logged.
    /// Spotify returns 403 PREMIUM_REQUIRED for Free accounts and
    /// 404 NO_ACTIVE_DEVICE when nothing is currently playing.
    /// </summary>
    private async Task<bool> ControlAsync(HttpMethod method, string endpoint)
    {
        var response = await SendAuthenticatedRequestAsync(method, endpoint);

        if (response == null)
        {
            Logger.Log($"[SpotifyClient] Control {endpoint} failed: no response (no tokens or network error).");
            return false;
        }

        if (response.IsSuccessStatusCode)
        {
            Logger.Log($"[SpotifyClient] Control {endpoint} OK ({(int)response.StatusCode}).");
            return true;
        }

        var body = await response.Content.ReadAsStringAsync();
        Logger.Log($"[SpotifyClient] Control {endpoint} failed: {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        return false;
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
                Logger.Log("[SpotifyClient] Got 401, refreshing token...");
                await RefreshTokensAsync();
                return await SendAuthenticatedRequestAsync(method, endpoint, content, isRetry: true);
            }

            // Handle 429 — rate limited
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                {
                    Logger.Log($"[SpotifyClient] Rate limited. Retry after: {retryAfter.TotalSeconds}s");
                    await Task.Delay(retryAfter);
                    return await SendAuthenticatedRequestAsync(method, endpoint, content, isRetry: true);
                }
            }

            return response;
        }
        catch (HttpRequestException ex)
        {
            Logger.Log($"[SpotifyClient] Request exception: {ex.Message}");
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
                    Logger.Log("[SpotifyClient] Tokens refreshed successfully.");
                }
                else
                {
                    Logger.Log("[SpotifyClient] Token refresh failed.");
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Gets the profile of the authorized user.
    /// </summary>
    public async Task<SpotifyUserProfile?> GetUserProfileAsync()
    {
        try
        {
            var response = await SendAuthenticatedRequestAsync(HttpMethod.Get, "/me");
            if (response == null || !response.IsSuccessStatusCode)
            {
                Logger.Log($"[SpotifyClient] GetUserProfile failed: {response?.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var profile = JsonSerializer.Deserialize<SpotifyUserProfile>(json);
            return profile;
        }
        catch (Exception ex)
        {
            Logger.Log($"[SpotifyClient] GetUserProfile exception: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Represents the user's profile details.
/// </summary>
public class SpotifyUserProfile
{
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("product")]
    public string? Product { get; set; }
}
