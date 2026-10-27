using System.IO;
using System.Text.Json;

namespace GameHub.Services;

/// <summary>Jogo escolhido pelo usuário como arte de cada card (console ou atalho da Início).</summary>
public static class CardArtService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "card_art.json");
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _map;

    private static Dictionary<string, string> Load()
    {
        if (_map != null) return _map;
        try
        {
            if (File.Exists(FilePath))
                _map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) { Logger.LogError("card_art.json", ex); }
        return _map ??= new Dictionary<string, string>();
    }

    public static string? Get(string cardKey)
    {
        lock (Gate) return Load().GetValueOrDefault(cardKey);
    }

    public static void Set(string cardKey, string gameKey)
    {
        lock (Gate) { Load()[cardKey] = gameKey; Save(); }
    }

    public static void Clear(string cardKey)
    {
        lock (Gate) { if (Load().Remove(cardKey)) Save(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_map));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Logger.LogError("Salvar card_art.json", ex); }
    }
}