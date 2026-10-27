using System.IO;
using Microsoft.Win32;
using GameHub.Models;
using GameHub.Services;

namespace GameHub.Scanners;

public class SteamScanner : IGameScanner
{
    public Platform Platform => Platform.Steam;

    public List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        var steamPath = GetSteamInstallPath();
        if (steamPath == null || !Directory.Exists(steamPath))
            return games;

        foreach (var libraryFolder in GetLibraryFolders(steamPath!))
        {
            var steamAppsDir = Path.Combine(libraryFolder, "steamapps");
            if (!Directory.Exists(steamAppsDir)) continue;

            foreach (var manifestFile in Directory.GetFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var text = File.ReadAllText(manifestFile);
                    var data = VdfParser.Parse(text);

                    if (!data.TryGetValue("AppState", out var appStateObj) ||
                        appStateObj is not Dictionary<string, object> appState)
                        continue;

                    var appId = appState.GetValueOrDefault("appid") as string;
                    var name = appState.GetValueOrDefault("name") as string;

                    if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name))
                        continue;

                    // Lê tempo jogado (minutesplayed) e última sessão (lastplayed)
                    int playtimeMinutes = 0;
                    DateTime? lastPlayed = null;

                    if (appState.TryGetValue("minutesplayed", out var mpObj) &&
                        int.TryParse(mpObj as string, out var mp))
                        playtimeMinutes = mp;

                    if (appState.TryGetValue("lastplayed", out var lpObj) &&
                        long.TryParse(lpObj as string, out var lpTs) && lpTs > 0)
                        lastPlayed = DateTimeOffset.FromUnixTimeSeconds(lpTs).LocalDateTime;
                    
                    var installDir = appState.GetValueOrDefault("installdir") as string;
                    var gamePath = string.IsNullOrWhiteSpace(installDir)
                        ? Path.Combine(steamAppsDir, "common")
                        : Path.Combine(steamAppsDir, "common", installDir);

                    var entry = new GameEntry
                    {
                        Name = name,
                        Platform = Platform.Steam,
                        Platforms = new List<Platform> { Platform.Steam },
                        InstalledPlatforms = new List<Platform> { Platform.Steam },
                        AppId = appId,
                        InstallPath = gamePath,
                        LaunchUri = $"steam://rungameid/{appId}",
                        PlaytimeMinutes = playtimeMinutes,
                        LastPlayed = lastPlayed
                    };

                    // O merge (GameLibraryService) só olha os dicionários por-plataforma,
                    // não o campo PlaytimeMinutes solto - sem isso o tempo jogado da Steam
                    // local se perdia depois do agrupamento.
                    if (playtimeMinutes > 0)
                        entry.PlaytimeByPlatform[Platform.Steam] = playtimeMinutes;
                    if (lastPlayed.HasValue)
                        entry.LastPlayedByPlatform[Platform.Steam] = lastPlayed.Value;

                    games.Add(entry);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"SteamScanner falhou no manifesto {manifestFile}", ex);
                }
            }
        }

        return games;
    }

    private static string? GetSteamInstallPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                          ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
        return key?.GetValue("InstallPath") as string;
    }

    /// <summary>
    /// O Steam pode ter bibliotecas espalhadas por vários HDs/SSDs.
    /// O libraryfolders.vdf (dentro da instalação principal) lista todas elas.
    /// </summary>
    private static IEnumerable<string> GetLibraryFolders(string steamPath)
    {
        yield return steamPath; // a instalação principal também é uma biblioteca

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        var data = VdfParser.Parse(File.ReadAllText(vdfPath));
        if (!data.TryGetValue("libraryfolders", out var foldersObj) ||
            foldersObj is not Dictionary<string, object> folders)
            yield break;

        foreach (var entry in folders.Values)
        {
            if (entry is Dictionary<string, object> folderData &&
                folderData.GetValueOrDefault("path") is string path &&
                !string.Equals(Path.GetFullPath(path), Path.GetFullPath(steamPath), StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }
}
