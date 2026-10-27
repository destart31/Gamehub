using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using GameHub.Models;

namespace GameHub.Services;

public static class GameLauncher
{
    public static void Launch(GameEntry game)
    {
        if (!game.IsInstalled)
        {
            throw new InvalidOperationException($"O jogo '{game.Name}' não está instalado no computador. Abra a loja correspondente ({game.PlatformDisplayName}) para realizar o download.");
        }

        if (!string.IsNullOrWhiteSpace(game.LaunchUri))
        {
            // Jogo da Steam com a Steam fechada: inicia ela direto na bandeja (sem abrir a janela principal).
            // Em qualquer outro caso, segue o caminho normal.
            if (!TryLaunchSteamSilently(game.LaunchUri))
                Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
        }
        else if (!string.IsNullOrWhiteSpace(game.LaunchExecutable)
                 && game.LaunchExecutable.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            // Apps da Microsoft Store/Xbox: abrem pelo explorer, não pelo .exe
            Process.Start(new ProcessStartInfo("explorer.exe", game.LaunchExecutable)
            {
                UseShellExecute = true
            });
        }
        else if (!string.IsNullOrWhiteSpace(game.LaunchExecutable) && File.Exists(game.LaunchExecutable))
        {
            Process.Start(new ProcessStartInfo(game.LaunchExecutable)
            {
                Arguments = game.LaunchArguments ?? "",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(game.LaunchExecutable)
            });
        }
        else
        {
            throw new InvalidOperationException($"Não foi possível iniciar '{game.Name}': o executável ou protocolo de inicialização não foi encontrado.");
        }
    }

    /// <summary>
    /// Se o link é steam:// e a Steam ainda não está rodando, abre ela com -silent
    /// (mesma forma que o Windows usa para o protocolo steam://, só que sem mostrar a janela principal).
    /// Retorna false quando não é o caso, e o chamador usa o caminho normal.
    /// </summary>
    private static bool TryLaunchSteamSilently(string uri)
    {
        if (!uri.StartsWith("steam://", StringComparison.OrdinalIgnoreCase)) return false;
        if (Process.GetProcessesByName("steam").Length > 0) return false; // já está aberta

        var steamExe = FindSteamExe();
        if (steamExe == null) return false;

        Process.Start(new ProcessStartInfo(steamExe)
        {
            Arguments = $"-silent -- \"{uri}\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(steamExe)
        });
        return true;
    }

    private static string? FindSteamExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");

            var exe = key?.GetValue("SteamExe") as string;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                exe = Path.GetFullPath(exe); // o registro guarda com "/" em vez de "\"
                if (File.Exists(exe)) return exe;
            }

            var dir = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var candidate = Path.Combine(Path.GetFullPath(dir), "steam.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* sem acesso ao registro: usa o caminho normal */ }

        return null;
    }
}
