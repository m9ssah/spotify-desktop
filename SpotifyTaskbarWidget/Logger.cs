using System.IO;

namespace SpotifyTaskbarWidget;

public static class Logger
{
    // %APPDATA%\SpotifyTaskbarWidget\app_debug.log — same folder as the token store.
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotifyTaskbarWidget",
        "app_debug.log");

    private static readonly object Gate = new();

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.FFF}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
