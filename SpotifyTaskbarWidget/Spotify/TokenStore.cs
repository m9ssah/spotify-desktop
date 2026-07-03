using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyTaskbarWidget.Spotify;

/// <summary>
/// Securely stores Spotify OAuth tokens using DPAPI (tied to the current Windows user).
/// Tokens are stored in %APPDATA%\SpotifyTaskbarWidget\tokens.dat.
/// </summary>
public class TokenStore
{
    private readonly string _filePath;

    public TokenStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "SpotifyTaskbarWidget");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "tokens.dat");
    }

    /// <summary>
    /// Saves tokens encrypted with DPAPI.
    /// </summary>
    public void Save(TokenData tokens)
    {
        var json = JsonSerializer.Serialize(tokens);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, encryptedBytes);
    }

    /// <summary>
    /// Loads and decrypts tokens. Returns null if no tokens exist or decryption fails.
    /// </summary>
    public TokenData? Load()
    {
        if (!File.Exists(_filePath))
            return null;

        try
        {
            var encryptedBytes = File.ReadAllBytes(_filePath);
            var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<TokenData>(json);
        }
        catch (CryptographicException)
        {
            // Token file is corrupted or from a different user
            Delete();
            return null;
        }
        catch (JsonException)
        {
            Delete();
            return null;
        }
    }

    /// <summary>
    /// Deletes stored tokens.
    /// </summary>
    public void Delete()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    /// <summary>
    /// Returns true if a token file exists.
    /// </summary>
    public bool HasTokens => File.Exists(_filePath);
}

/// <summary>
/// Stores the OAuth token pair with expiry information.
/// </summary>
public class TokenData
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Returns true if the access token has expired (with a 60s buffer).
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAtUtc.AddSeconds(-60);
}
