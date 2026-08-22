using System.IO;

namespace SpotifyTaskbarWidget;

public static class Config
{
    private static readonly Dictionary<string, string> Values = Load();

    private static Dictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, ".env");

        if (!File.Exists(path))
        {
            Logger.Log($"[Config] No .env at {path} — using defaults.");
            return values;
        }

        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                    continue;

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim().Trim('"', '\'');
                values[key] = value;
            }
            Logger.Log($"[Config] Loaded {values.Count} setting(s) from .env");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Config] Failed to read .env: {ex.Message}");
        }

        return values;
    }

    public static string Get(string key, string fallback = "")
    {
        if (Values.TryGetValue(key, out var value) && value.Length > 0)
            return value;

        var fromEnvironment = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrEmpty(fromEnvironment) ? fallback : fromEnvironment;
    }
}
