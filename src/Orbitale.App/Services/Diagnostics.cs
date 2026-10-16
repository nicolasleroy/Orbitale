namespace Orbitale.App.Services;

/// <summary>A tiny startup/error log next to the settings (%LOCALAPPDATA%\Orbitale\orbitale.log), truncated at launch.</summary>
public static class Diagnostics
{
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbitale", "orbitale.log");
    static readonly Lock Gate = new();
    static bool _started;

    public static void Log(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                if (_started) File.AppendAllText(LogPath, line);
                else File.WriteAllText(LogPath, line);
                _started = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
