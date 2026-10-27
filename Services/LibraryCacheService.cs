using System.IO;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Guarda a biblioteca da última varredura em %AppData%\GameHub\library_cache.json.
/// Ao abrir, o GameHub mostra essa lista na hora e refaz a varredura em segundo plano;
/// só troca a tela se algo mudou. As capas não entram aqui (são lidas da pasta de capas).
/// Favoritos, ocultos e tempo medido pelo GameHub são aplicados por cima, como sempre.
/// </summary>
public static class LibraryCacheService
{
    // Mude este número se os campos abaixo mudarem: o cache antigo é descartado
    private const int Version = 1;

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");

    private static readonly string CachePath = Path.Combine(ConfigDir, "library_cache.json");

    private class CacheFile
    {
        public int Version { get; set; }
        public List<CachedGame> Games { get; set; } = new();
    }

    private class CachedGame
    {
        public string Name { get; set; } = "";
        public Platform Platform { get; set; }
        public List<Platform> Platforms { get; set; } = new();
        public List<Platform> InstalledPlatforms { get; set; } = new();
        public string InstallPath { get; set; } = "";
        public string? AppId { get; set; }
        public string? ReleaseKey { get; set; }
        public string? LaunchUri { get; set; }
        public string? LaunchExecutable { get; set; }
        public string? LaunchArguments { get; set; }
        public string? EmulatorSystem { get; set; }
        public string? RomFilePath { get; set; }
        public bool IsCustomGame { get; set; }
        public int PlaytimeMinutes { get; set; }
        public DateTime? LastPlayed { get; set; }
        public string? Summary { get; set; }
        public List<string> Genres { get; set; } = new();
        public string? Developer { get; set; }
        public string? Publisher { get; set; }
        public double? CriticsScore { get; set; }
        public Dictionary<Platform, int> PlaytimeByPlatform { get; set; } = new();
        public Dictionary<Platform, DateTime> LastPlayedByPlatform { get; set; } = new();
    }

    /// <summary>Lê a biblioteca guardada. Qualquer problema (arquivo ausente, estragado, versão antiga) devolve lista vazia.</summary>
    public static List<GameEntry> Load()
    {
        try
        {
            if (!File.Exists(CachePath)) return new List<GameEntry>();

            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
            if (file == null || file.Version != Version) return new List<GameEntry>();

            return file.Games.Select(FromCached).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError("Ler cache da biblioteca", ex);
            return new List<GameEntry>();
        }
    }

    /// <summary>
    /// Grava a biblioteca. Devolve true se o conteúdo mudou desde o último cache
    /// (ou se não foi possível comparar), false se a lista é idêntica à que já estava na tela.
    /// Lista vazia nunca sobrescreve o cache: provavelmente a varredura falhou.
    /// </summary>
    public static bool Save(List<GameEntry> games)
    {
        if (games.Count == 0) return true;

        try
        {
            var file = new CacheFile { Version = Version, Games = games.Select(ToCached).ToList() };
            var json = JsonSerializer.Serialize(file);

            if (File.Exists(CachePath) && File.ReadAllText(CachePath) == json)
                return false;

            Directory.CreateDirectory(ConfigDir);
            var tmp = CachePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, CachePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("Gravar cache da biblioteca", ex);
            return true;
        }
    }

    private static CachedGame ToCached(GameEntry g) => new()
    {
        Name = g.Name,
        Platform = g.Platform,
        Platforms = g.Platforms.ToList(),
        InstalledPlatforms = g.InstalledPlatforms.ToList(),
        InstallPath = g.InstallPath,
        AppId = g.AppId,
        ReleaseKey = g.ReleaseKey,
        LaunchUri = g.LaunchUri,
        LaunchExecutable = g.LaunchExecutable,
        LaunchArguments = g.LaunchArguments,
        EmulatorSystem = g.EmulatorSystem,
        RomFilePath = g.RomFilePath,
        IsCustomGame = g.IsCustomGame,
        PlaytimeMinutes = g.PlaytimeMinutes,
        LastPlayed = g.LastPlayed,
        Summary = g.Summary,
        Genres = g.Genres.ToList(),
        Developer = g.Developer,
        Publisher = g.Publisher,
        CriticsScore = g.CriticsScore,
        PlaytimeByPlatform = new Dictionary<Platform, int>(g.PlaytimeByPlatform),
        LastPlayedByPlatform = new Dictionary<Platform, DateTime>(g.LastPlayedByPlatform)
    };

    private static GameEntry FromCached(CachedGame c) => new()
    {
        Name = c.Name,
        Platform = c.Platform,
        Platforms = c.Platforms,
        InstalledPlatforms = c.InstalledPlatforms,
        InstallPath = c.InstallPath,
        AppId = c.AppId,
        ReleaseKey = c.ReleaseKey,
        LaunchUri = c.LaunchUri,
        LaunchExecutable = c.LaunchExecutable,
        LaunchArguments = c.LaunchArguments,
        EmulatorSystem = c.EmulatorSystem,
        RomFilePath = c.RomFilePath,
        IsCustomGame = c.IsCustomGame,
        PlaytimeMinutes = c.PlaytimeMinutes,
        LastPlayed = c.LastPlayed,
        Summary = c.Summary,
        Genres = c.Genres,
        Developer = c.Developer,
        Publisher = c.Publisher,
        CriticsScore = c.CriticsScore,
        PlaytimeByPlatform = c.PlaytimeByPlatform,
        LastPlayedByPlatform = c.LastPlayedByPlatform
    };
}
