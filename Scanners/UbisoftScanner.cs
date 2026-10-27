using System.IO;
using Microsoft.Win32;
using GameHub.Models;

namespace GameHub.Scanners;

public class UbisoftScanner : IGameScanner
{
    public Platform Platform => Platform.Ubisoft;

    // O Ubisoft Connect lista cada jogo instalado com um ID numérico e o InstallDir
    private const string InstallsKeyPath = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
    private const string InstallsKeyPath32 = @"SOFTWARE\Ubisoft\Launcher\Installs";

    // Nomes legíveis ficam em: HKLM\SOFTWARE\WOW6432Node\Ubisoft\GameLauncher\Installs\{id}\Name
    // OU no DisplayName da chave de desinstalação padrão do Windows
    private const string UninstallPath = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallPath32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        // Carrega mapa de AppId -> DisplayName a partir das chaves de desinstalação (mais confiável)
        var nameMap = BuildNameMap();

        // Lê os jogos instalados pelo Ubisoft Connect
        using var installsKey = Registry.LocalMachine.OpenSubKey(InstallsKeyPath)
                               ?? Registry.LocalMachine.OpenSubKey(InstallsKeyPath32);

        if (installsKey == null) return games;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var subKeyName in installsKey.GetSubKeyNames())
        {
            try
            {
                using var gameKey = installsKey.OpenSubKey(subKeyName);
                if (gameKey == null) continue;

                var installDir = gameKey.GetValue("InstallDir") as string;
                if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
                    continue;

                // Tenta obter o nome pelo mapa de desinstalação, senão usa o nome da pasta
                if (!nameMap.TryGetValue(subKeyName, out var name) || string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileName(installDir.TrimEnd('\\', '/'));

                name = CleanTitle(name);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    continue;

                // Lança pelo protocolo uplay:// (ubisoft-connect://)
                var launchUri = $"uplay://launch/{subKeyName}/0";

                games.Add(new GameEntry
                {
                    Name = name,
                    Platform = Platform.Ubisoft,
                    Platforms = new List<Platform> { Platform.Ubisoft },
                    InstalledPlatforms = new List<Platform> { Platform.Ubisoft },
                    AppId = subKeyName,
                    InstallPath = installDir,
                    LaunchUri = launchUri
                });
            }
            catch
            {
                // Subchave inválida — ignora
            }
        }

        return games;
    }

    /// <summary>
    /// Mapeia AppId numérico do Ubisoft -> DisplayName lendo as chaves de Uninstall do Windows.
    /// O Ubisoft registra cada jogo com Publisher = "Ubisoft" e UninstallString contendo "uplay".
    /// </summary>
    private static Dictionary<string, string> BuildNameMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var regPath in new[] { UninstallPath, UninstallPath32 })
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(regPath);
            if (rootKey == null) continue;

            foreach (var subKeyName in rootKey.GetSubKeyNames())
            {
                try
                {
                    using var key = rootKey.OpenSubKey(subKeyName);
                    if (key == null) continue;

                    var publisher = key.GetValue("Publisher") as string ?? "";
                    var uninstall = key.GetValue("UninstallString") as string ?? "";
                    var displayName = key.GetValue("DisplayName") as string;

                    bool isUbisoft = publisher.Contains("Ubisoft", StringComparison.OrdinalIgnoreCase)
                                  || uninstall.Contains("uplay", StringComparison.OrdinalIgnoreCase)
                                  || uninstall.Contains("ubisoft", StringComparison.OrdinalIgnoreCase);

                    if (!isUbisoft || string.IsNullOrWhiteSpace(displayName)) continue;

                    // Tenta extrair o uplay game ID do UninstallString
                    // Formato típico: "C:\...\UplayR1Uninstall.exe" /product_id 72
                    var idMatch = System.Text.RegularExpressions.Regex.Match(
                        uninstall, @"product_id\s+(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (idMatch.Success)
                        map[idMatch.Groups[1].Value] = displayName;
                    else
                        // Fallback: usa o próprio subKeyName
                        map[subKeyName] = displayName;
                }
                catch { }
            }
        }

        return map;
    }

    private static string CleanTitle(string raw) =>
        raw.Replace("™", "").Replace("®", "").Replace("(TM)", "").Replace("(R)", "").Trim();
}
