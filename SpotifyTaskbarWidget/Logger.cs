using System.IO;

namespace SpotifyTaskbarWidget;

public static class Logger
{
    private static readonly string LogFile = @"c:\Users\massah\Documents\GitHub\spotify-desktop\app_debug.log";
    private static readonly object Gate = new();

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.FFF}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
