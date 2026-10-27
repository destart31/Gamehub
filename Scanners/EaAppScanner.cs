using System.IO;
using Microsoft.Win32;
using GameHub.Models;

namespace GameHub.Scanners;

public class EaAppScanner : IGameScanner
{
    public Platform Platform => Platform.EaApp;

    private static readonly string[] EaRegistryPaths = new[]
    {
        @"SOFTWARE\WOW6432Node\EA Games",
        @"SOFTWARE\EA Games"
    };

    private static readonly string[] UninstallRegistryPaths = new[]
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    };

    /// <summary>
    /// Trechos de caminho que NÃO provam que o jogo está instalado: desinstaladores, instaladores,
    /// anti-cheat, redistribuíveis. Uma pasta que sobrou depois de desinstalar costuma ter só isso.
    /// </summary>
    private static readonly string[] NotAGameExe = new[]
    {
        "unins", "setup", "installer", "crash", "helper", "activation",
        "touchup", "redist", "dxsetup", "dotnet", "anticheat", "cleanup"
    };

    // Registro do que foi aceito/ignorado em cada varredura - ajuda a descobrir por que um jogo aparece instalado
    private readonly List<string> _log = new();

    public List<GameEntry> Scan()
    {
        _log.Clear();
        var games = new List<GameEntry>();
        var addedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Chaves específicas de EA Games no registro
        ScanEaRegistryKeys(games, addedNames);

        // 2. Chaves de desinstalação (onde EA SPORTS FC, NFS, Battlefield etc. ficam registrados)
        ScanUninstallRegistryKeys(games, addedNames);

        // 3. Pastas de ProgramData\EA Desktop\InstallData: só registradas no log.
        //    Antes cada pasta virava um jogo "instalado", mas ela continua lá depois de desinstalar
        //    e não diz onde o jogo está - então não dá pra confirmar que ele existe.
        LogInstallDataFolders();

        WriteLog(games);
        return games;
    }

    private void ScanEaRegistryKeys(List<GameEntry> games, HashSet<string> addedNames)
    {
        foreach (var regPath in EaRegistryPaths)
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(regPath);
            if (rootKey == null) continue;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                using var gameKey = rootKey.OpenSubKey(subKeyName);
                if (gameKey == null) continue;

                var rawName = gameKey.GetValue("DisplayName") as string
                              ?? gameKey.GetValue("gameName") as string
                              ?? subKeyName;

                var name = CleanGameTitle(rawName);
                var installDir = gameKey.GetValue("Install Dir") as string
                                 ?? gameKey.GetValue("path") as string;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir))
                    continue;

                if (addedNames.Contains(name))
                    continue;

                var exePath = FindPrimaryExecutable(installDir, null);

                // A pasta existir não basta: ela pode ter sobrado de uma desinstalação
                if (!HasInstalledFiles(installDir, exePath))
                {
                    _log.Add($"[registro EA] IGNORADO, sem arquivos do jogo: {name} | {installDir}");
                    continue;
                }

                addedNames.Add(name);
                _log.Add($"[registro EA] instalado: {name} | {installDir}");

                games.Add(new GameEntry
                {
                    Name = name,
                    Platform = Platform.EaApp,
                    InstalledPlatforms = new List<Platform> { Platform.EaApp },
                    AppId = subKeyName,
                    InstallPath = installDir,
                    LaunchExecutable = exePath
                });
            }
        }
    }

    private void ScanUninstallRegistryKeys(List<GameEntry> games, HashSet<string> addedNames)
    {
        foreach (var regPath in UninstallRegistryPaths)
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(regPath)
                                ?? Registry.CurrentUser.OpenSubKey(regPath);
            if (rootKey == null) continue;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                using var gameKey = rootKey.OpenSubKey(subKeyName);
                if (gameKey == null) continue;

                var publisher = gameKey.GetValue("Publisher") as string ?? "";
                var uninstallString = gameKey.GetValue("UninstallString") as string ?? "";
                var displayIcon = gameKey.GetValue("DisplayIcon") as string ?? "";

                // Verifica se é publicado pela Electronic Arts ou gerido pelo EA App / Origin
                bool isEaGame = publisher.Contains("Electronic Arts", StringComparison.OrdinalIgnoreCase)
                             || publisher.Equals("EA", StringComparison.OrdinalIgnoreCase)
                             || uninstallString.Contains("EADesktop", StringComparison.OrdinalIgnoreCase)
                             || uninstallString.Contains("Origin", StringComparison.OrdinalIgnoreCase);

                if (!isEaGame) continue;

                var rawName = gameKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(rawName)) continue;

                // Ignora o próprio launcher ("EA App", "Electronic Arts Background Service")
                if (rawName.Equals("EA App", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Contains("Background Service", StringComparison.OrdinalIgnoreCase) ||
                    rawName.Contains("Origin", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = CleanGameTitle(rawName);
                if (addedNames.Contains(name))
                    continue;

                var installDir = gameKey.GetValue("InstallLocation") as string ?? "";
                var exePath = ParseExeFromDisplayIcon(displayIcon);

                if (string.IsNullOrWhiteSpace(installDir) && !string.IsNullOrWhiteSpace(exePath))
                {
                    installDir = Path.GetDirectoryName(exePath) ?? "";
                }

                if (string.IsNullOrWhiteSpace(exePath) && !string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir))
                {
                    exePath = FindPrimaryExecutable(installDir, name);
                }

                // Chave de desinstalação que sobrou no registro depois de remover o jogo é comum:
                // só vale se os arquivos do jogo ainda existem no disco
                if (!HasInstalledFiles(installDir, exePath))
                {
                    _log.Add($"[desinstalação] IGNORADO, sem arquivos do jogo: {name} | {installDir}");
                    continue;
                }

                addedNames.Add(name);
                _log.Add($"[desinstalação] instalado: {name} | {installDir}");

                games.Add(new GameEntry
                {
                    Name = name,
                    Platform = Platform.EaApp,
                    InstalledPlatforms = new List<Platform> { Platform.EaApp },
                    AppId = subKeyName,
                    InstallPath = installDir,
                    LaunchExecutable = exePath
                });
            }
        }
    }

    private void LogInstallDataFolders()
    {
        try
        {
            var eaInstallData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "EA Desktop", "InstallData");
            if (!Directory.Exists(eaInstallData)) return;

            foreach (var dir in Directory.GetDirectories(eaInstallData))
                _log.Add($"[InstallData] ignorado (não prova instalação): {Path.GetFileName(dir)}");
        }
        catch { /* só diagnóstico */ }
    }

    /// <summary>
    /// True só se existe de fato um executável do jogo (não instalador/anti-cheat) na pasta de instalação.
    /// </summary>
    private static bool HasInstalledFiles(string? installDir, string? exePath)
    {
        try
        {
            var root = !string.IsNullOrWhiteSpace(installDir) && Directory.Exists(installDir) ? installDir : null;

            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath) && IsGameExe(exePath, root))
                return true;

            if (root == null) return false;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 4,
                IgnoreInaccessible = true
            };

            // Any() para no primeiro .exe válido: jogo instalado responde na hora, pasta sobrada é pequena
            return Directory.EnumerateFiles(root, "*.exe", options).Any(f => IsGameExe(f, root));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsGameExe(string fullPath, string? root)
    {
        var relative = root != null ? Path.GetRelativePath(root, fullPath) : Path.GetFileName(fullPath);
        relative = relative.ToLowerInvariant();
        return !NotAGameExe.Any(bad => relative.Contains(bad));
    }

    private void WriteLog(List<GameEntry> games)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
            Directory.CreateDirectory(dir);

            var lines = new List<string> { $"Varredura EA App em {DateTime.Now:dd/MM/yyyy HH:mm:ss} - {games.Count} jogo(s) instalado(s)" };
            lines.AddRange(_log);
            File.WriteAllLines(Path.Combine(dir, "ea_scan_log.txt"), lines);
        }
        catch { /* log é só diagnóstico: nunca pode quebrar a varredura */ }
    }

    private static string CleanGameTitle(string rawName)
    {
        return rawName.Replace("™", "")
                      .Replace("®", "")
                      .Replace("(TM)", "")
                      .Replace("(R)", "")
                      .Trim();
    }

    private static string? ParseExeFromDisplayIcon(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var parts = displayIcon.Split(',');
        var path = parts[0].Trim('"').Trim();
        return File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string? FindPrimaryExecutable(string dir, string? hintName)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);

            if (!string.IsNullOrWhiteSpace(hintName))
            {
                var match = exes.FirstOrDefault(e => Path.GetFileNameWithoutExtension(e).Equals(hintName, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }

            var primary = exes.FirstOrDefault(e =>
            {
                var filename = Path.GetFileName(e).ToLowerInvariant();
                return !filename.Contains("unins") &&
                       !filename.Contains("setup") &&
                       !filename.Contains("installer") &&
                       !filename.Contains("crash") &&
                       !filename.Contains("helper") &&
                       !filename.Contains("activation");
            });

            return primary ?? exes.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}
