using System.IO;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Jogos adicionados manualmente (baixados fora de loja, remasters de comunidade etc.).
/// Fica salvo em custom_games.json - de propósito NÃO está listado no .csproj como arquivo
/// do projeto, então uma atualização do GameHub nunca sobrescreve/apaga o que você já
/// cadastrou. É o próprio app que cria e mantém esse arquivo em tempo de execução.
/// </summary>
public static class CustomGamesService
{
    private class CustomGameRecord
    {
        public string Name { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
    }

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
    
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "custom_games.json");

    static CustomGamesService()
    {
        if (!Directory.Exists(ConfigDir))
            Directory.CreateDirectory(ConfigDir);

        // Migração do local antigo para o novo (AppData)
        var oldPath = Path.Combine(AppContext.BaseDirectory, "custom_games.json");
        if (File.Exists(oldPath) && !File.Exists(ConfigPath))
        {
            try { File.Move(oldPath, ConfigPath); } catch { }
        }
    }

    public static List<GameEntry> LoadAll()
    {
        var result = new List<GameEntry>();
        var records = LoadRecords();

        foreach (var record in records)
        {
            result.Add(new GameEntry
            {
                Name = record.Name,
                Platform = Platform.Modificado,
                IsCustomGame = true,
                InstalledPlatforms = new List<Platform> { Platform.Modificado },
                LaunchExecutable = record.ExecutablePath,
                InstallPath = Path.GetDirectoryName(record.ExecutablePath) ?? ""
            });
        }

        return result;
    }

    public static void Add(string name, string executablePath)
    {
        var records = LoadRecords();
        records.Add(new CustomGameRecord { Name = name, ExecutablePath = executablePath });
        SaveRecords(records);
    }

    public static void Remove(string name, string executablePath)
    {
        var records = LoadRecords();
        records.RemoveAll(r => r.Name == name && r.ExecutablePath == executablePath);
        SaveRecords(records);
    }

    private static List<CustomGameRecord> LoadRecords()
    {
        if (!File.Exists(ConfigPath)) return new List<CustomGameRecord>();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<List<CustomGameRecord>>(json) ?? new List<CustomGameRecord>();
        }
        catch
        {
            return new List<CustomGameRecord>();
        }
    }

    private static void SaveRecords(List<CustomGameRecord> records)
    {
        var json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
