using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyTaskbarWidget.Spotify;

/// <summary>
/// Handles Spotify OAuth 2.0 Authorization Code with PKCE flow.
/// Opens the system browser for login and captures the callback via a localhost HTTP listener.
/// </summary>
public class SpotifyAuth
{
    // ─── Configuration ───────────────────────────────────────────────────
    // Users must set their Client ID from https://developer.spotify.com/dashboard
    private const string AuthorizeUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string RedirectUri = "http://127.0.0.1:5543/callback";

    private static readonly string[] Scopes =
    {
        "user-read-playback-state",
        "user-modify-playback-state",
        "user-read-currently-playing",
        "user-read-private"
    };

    private readonly string _clientId;
    private readonly TokenStore _tokenStore;
    private readonly HttpClient _httpClient;

    private string? _codeVerifier;

    public SpotifyAuth(string clientId, TokenStore tokenStore, HttpClient? httpClient = null)
    {
        _clientId = clientId;
        _tokenStore = tokenStore;
        _httpClient = httpClient ?? new HttpClient();
        
        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyTaskbarWidget/1.0 (Windows NT 10.0; Win64; x64)");
        }
    }

    /// <summary>
    /// Attempts to load existing tokens. Returns true if valid tokens are available.
    /// If the access token is expired but a refresh token exists, it will auto-refresh.
    /// </summary>
    public async Task<TokenData?> TryLoadExistingTokensAsync()
    {
        var tokens = _tokenStore.Load();
        if (tokens == null)
            return null;

        if (!tokens.IsExpired)
            return tokens;

        // Try refreshing
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
        {
            var refreshed = await RefreshTokenAsync(tokens.RefreshToken);
            return refreshed;
        }

        return null;
    }

    /// <summary>
    /// Initiates the full OAuth PKCE login flow:
    /// 1. Generate code verifier/challenge
    /// 2. Open browser to Spotify authorize
    /// 3. Listen for callback on localhost
    /// 4. Exchange code for tokens
    /// </summary>
    public async Task<TokenData?> LoginAsync(CancellationToken cancellationToken = default)
    {
        // Generate PKCE pair
        _codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(_codeVerifier);

        // Build authorize URL
        var state = Guid.NewGuid().ToString("N");
        var authorizeUri = $"{AuthorizeUrl}" +
                           $"?client_id={Uri.EscapeDataString(_clientId)}" +
                           $"&response_type=code" +
                           $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                           $"&scope={Uri.EscapeDataString(string.Join(" ", Scopes))}" +
                           $"&state={state}" +
                           $"&code_challenge_method=S256" +
                           $"&code_challenge={codeChallenge}" +
                           $"&show_dialog=true";

        // Start the localhost HTTP listener BEFORE opening the browser
        var authCode = await ListenForCallbackAsync(state, cancellationToken, () =>
        {
            // Open system browser
            Process.Start(new ProcessStartInfo(authorizeUri) { UseShellExecute = true });
            Logger.Log("[SpotifyAuth] Opened browser for authorization.");
        });

        if (string.IsNullOrEmpty(authCode))
        {
            Logger.Log("[SpotifyAuth] No auth code received.");
            return null;
        }

        // Exchange auth code for tokens
        return await ExchangeCodeAsync(authCode);
    }

    /// <summary>
    /// Refreshes the access token using the refresh token.
    /// </summary>
    public async Task<TokenData?> RefreshTokenAsync(string refreshToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken },
                { "client_id", _clientId }
            });

            var response = await _httpClient.PostAsync(TokenUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Logger.Log($"[SpotifyAuth] Token refresh failed: {response.StatusCode} - {error}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(json);

            if (tokenResponse == null)
                return null;

            var tokens = new TokenData
            {
                AccessToken = tokenResponse.AccessToken ?? string.Empty,
                // Spotify may or may not return a new refresh token
                RefreshToken = tokenResponse.RefreshToken ?? refreshToken,
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
            };

            _tokenStore.Save(tokens);
            Logger.Log("[SpotifyAuth] Token refreshed successfully.");
            return tokens;
        }
        catch (Exception ex)
        {
            Logger.Log($"[SpotifyAuth] Token refresh exception: {ex.Message}");
            return null;
        }
    }

    // ─── Private Methods ─────────────────────────────────────────────────

    /// <summary>
    /// Starts a temporary HTTP listener and waits for the OAuth callback.
    /// </summary>
    private async Task<string?> ListenForCallbackAsync(string expectedState,
        CancellationToken cancellationToken, Action onListenerReady)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:5543/");

        try
        {
            listener.Start();
            Logger.Log("[SpotifyAuth] Listening for callback on http://127.0.0.1:5543/");

            // Notify caller that the listener is ready (trigger browser open)
            onListenerReady();

            // Wait for the callback (with timeout)
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var context = await listener.GetContextAsync().WaitAsync(linkedCts.Token);
            var request = context.Request;
            var response = context.Response;

            // Parse query parameters
            var query = request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];

            // Send response to browser
            string responseHtml;
            if (!string.IsNullOrEmpty(error))
            {
                responseHtml = "<html><body><h2>Authentication Failed</h2>" +
                               $"<p>Error: {error}</p>" +
                               "<p>You can close this tab.</p></body></html>";
                Logger.Log($"[SpotifyAuth] Auth error: {error}");
            }
            else if (state != expectedState)
            {
                responseHtml = "<html><body><h2>Authentication Failed</h2>" +
                               "<p>State mismatch — possible CSRF attack.</p></body></html>";
                Logger.Log("[SpotifyAuth] State mismatch.");
                code = null;
            }
            else
            {
                responseHtml = "<html><body style='font-family:Segoe UI;text-align:center;padding-top:60px'>" +
                               "<h2 style='color:#1DB954'>✓ Connected to Spotify!</h2>" +
                               "<p>You can close this tab and return to the app.</p></body></html>";
            }

            var buffer = Encoding.UTF8.GetBytes(responseHtml);
            response.ContentType = "text/html";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, cancellationToken);
            response.Close();

            return code;
        }
        catch (OperationCanceledException)
        {
            Logger.Log("[SpotifyAuth] Callback listener timed out or was cancelled.");
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Exchanges the authorization code for access and refresh tokens.
    /// </summary>
    private async Task<TokenData?> ExchangeCodeAsync(string code)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "grant_type", "authorization_code" },
            { "code", code },
            { "redirect_uri", RedirectUri },
            { "client_id", _clientId },
            { "code_verifier", _codeVerifier! }
        });

        var response = await _httpClient.PostAsync(TokenUrl, content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Logger.Log($"[SpotifyAuth] Token exchange failed: {response.StatusCode} - {error}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<SpotifyTokenResponse>(json);

        if (tokenResponse == null)
            return null;

        var tokens = new TokenData
        {
            AccessToken = tokenResponse.AccessToken ?? string.Empty,
            RefreshToken = tokenResponse.RefreshToken ?? string.Empty,
            ExpiresAtUtc = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn)
        };

        _tokenStore.Save(tokens);
        Logger.Log($"[SpotifyAuth] Tokens exchanged and saved successfully. Scopes returned: {tokenResponse.Scope ?? "none"}");
        return tokens;
    }

    // ─── PKCE Helpers ────────────────────────────────────────────────────

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(codeVerifier));
        return Base64UrlEncode(challengeBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

// ─── Token Response DTO ──────────────────────────────────────────────────

internal class SpotifyTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("scope")]
    public string? Scope { get; set; }
}
