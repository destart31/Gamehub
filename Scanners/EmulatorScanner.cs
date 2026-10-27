using System.IO;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Scanners;

/// <summary>
/// Lê emulators.json e lista as ROMs que existem em cada pasta configurada.
/// Não instala nem copia nada: aponta para uma pasta base que já existe, no layout do RetroBat/Playbox
/// (subpastas roms e emulators), e usa o que estiver lá.
///
/// Cada sistema que não resolver (pasta de ROM ausente, emulador não encontrado
/// no caminho configurado) é simplesmente pulado, sem derrubar os outros -
/// ajuste o emulators.json e não precisa recompilar nada.
/// </summary>
public class EmulatorScanner
{
    private class EmulatorSystemConfig
    {
        public string Folder { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public List<string> Extensions { get; set; } = new();
        public string EmulatorPath { get; set; } = "";
        public string ArgsTemplate { get; set; } = "";
    }

    private class EmulatorConfigFile
    {
        public string BasePath { get; set; } = "";
        public List<EmulatorSystemConfig> Systems { get; set; } = new();
    }

    private static readonly string ConfigPath =
        Path.Combine(AppContext.BaseDirectory, "emulators.json");

    public class SystemInfo
    {
        public string DisplayName { get; set; } = "";
        public string RomFolder { get; set; } = "";
    }

    /// <summary>Lista todo sistema que está no emulators.json, tenha ROM ou não - usado
    /// pro seletor de console mostrar todo mundo configurado, não só quem já tem jogo.</summary>
    public List<SystemInfo> GetConfiguredSystems()
    {
        var result = new List<SystemInfo>();
        var config = LoadConfig();
        if (config == null) return result;

        foreach (var system in config.Systems)
        {
            result.Add(new SystemInfo
            {
                DisplayName = system.DisplayName,
                RomFolder = Path.Combine(config.BasePath, "roms", system.Folder)
            });
        }
        return result;
    }

    private static EmulatorConfigFile? LoadConfig()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<EmulatorConfigFile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return string.IsNullOrWhiteSpace(config?.BasePath) ? null : config;
        }
        catch
        {
            return null;
        }
    }

    public List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        var config = LoadConfig();
        if (config == null) return games; // emulators.json ausente/mal formatado - segue sem essa aba

        foreach (var system in config.Systems)
        {
            try
            {
                ScanSystem(config.BasePath, system, games);
            }
            catch
            {
                // Um sistema com problema (pasta/emulador ausente, permissão etc.)
                // não deve impedir os outros de aparecer.
            }
        }

        return games;
    }

    private static void ScanSystem(string basePath, EmulatorSystemConfig system, List<GameEntry> games)
    {
        var romsFolder = Path.Combine(basePath, "roms", system.Folder);
        var emulatorFullPath = Path.Combine(basePath, system.EmulatorPath);

        if (!Directory.Exists(romsFolder)) return; // você não tem ROM desse sistema - pula
        if (!File.Exists(emulatorFullPath)) return; // emulador configurado não existe nesse caminho - pula

        foreach (var file in Directory.GetFiles(romsFolder))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!system.Extensions.Contains(ext)) continue;

            var name = Path.GetFileNameWithoutExtension(file);
            var args = system.ArgsTemplate
                .Replace("{rom}", file)
                .Replace("{base}", basePath);

            games.Add(new GameEntry
            {
                Name = name,
                Platform = Platform.Emulador,
                EmulatorSystem = system.DisplayName,
                InstalledPlatforms = new List<Platform> { Platform.Emulador },
                InstallPath = romsFolder,
                RomFilePath = file,
                LaunchExecutable = emulatorFullPath,
                LaunchArguments = args
            });
        }
    }
}
