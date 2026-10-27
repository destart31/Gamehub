using System.Diagnostics;
using System.IO;

namespace GameHub.Services;

/// <summary>Diagnóstico de abertura: anota quanto tempo cada etapa levou. Arquivo: %AppData%\GameHub\startup_log.txt</summary>
public static class StartupLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "startup_log.txt");

    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;
    private static readonly object Gate = new();
    private static bool _started;

    public static void Mark(string step)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var line = $"{(DateTime.Now - ProcessStart).TotalMilliseconds,7:0} ms | thread {Environment.CurrentManagedThreadId,2} | {step}{Environment.NewLine}";
                if (!_started) { File.WriteAllText(LogPath, line); _started = true; }
                else File.AppendAllText(LogPath, line);
            }
        }
        catch { /* diagnóstico: nunca pode atrapalhar o programa */ }
    }
}