using System;
using System.IO;

namespace GameHub.Services;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
    private static readonly string LogFile = Path.Combine(LogDir, "errors.log");
    private static readonly object _lock = new();

    public static void LogError(string context, Exception ex)
    {
        try
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);

            lock (_lock)
            {
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ERROR at {context}:\n{ex}\n----------------------------------------\n";
                File.AppendAllText(LogFile, entry);
            }
        }
        catch { } // Se o próprio logger falhar, desiste em silêncio.
    }
}
