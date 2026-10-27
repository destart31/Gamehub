using System.IO;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Dados pessoais do usuário sobre os jogos: favoritos, jogos ocultos e quando cada um
/// foi aberto pelo GameHub. Fica em %AppData%\GameHub\user_data.json (mesma pasta do
/// custom_games.json), então uma atualização do GameHub nunca apaga isso.
/// </summary>
public static class UserDataService
{
    private class UserData
    {
        public HashSet<string> Favorites { get; set; } = new();
        public HashSet<string> Hidden { get; set; } = new();
        public Dictionary<string, DateTime> LastLaunched { get; set; } = new();
    }

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "user_data.json");

    private static readonly object Gate = new();
    private static readonly UserData Data = Load();

    /// <summary>Aplica favoritos, ocultos e último lançamento salvos na lista de jogos recém carregada.</summary>
    public static void Apply(IEnumerable<GameEntry> games)
    {
        lock (Gate)
        {
            foreach (var game in games)
            {
                var key = game.UserKey;
                game.IsFavorite = Data.Favorites.Contains(key);
                game.IsHidden = Data.Hidden.Contains(key);
                game.LastLaunched = Data.LastLaunched.TryGetValue(key, out var when) ? when : null;
            }
        }
    }

    public static void SetFavorite(GameEntry game, bool value)
    {
        lock (Gate)
        {
            if (value) Data.Favorites.Add(game.UserKey); else Data.Favorites.Remove(game.UserKey);
            Save();
        }
    }

    public static void SetHidden(GameEntry game, bool value)
    {
        lock (Gate)
        {
            if (value) Data.Hidden.Add(game.UserKey); else Data.Hidden.Remove(game.UserKey);
            Save();
        }
    }

    public static void RecordLaunch(GameEntry game)
    {
        lock (Gate)
        {
            var now = DateTime.Now;
            game.LastLaunched = now; // já vale na hora, sem esperar recarregar a biblioteca
            Data.LastLaunched[game.UserKey] = now;
            Save();
        }
    }

    private static UserData Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new UserData();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<UserData>(json) ?? new UserData();
        }
        catch
        {
            return new UserData();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });

            // Grava num arquivo temporário e troca: se o programa fechar no meio, o arquivo antigo continua inteiro
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch
        {
            // Sem permissão ou disco cheio: o GameHub segue funcionando, só não guarda desta vez
        }
    }
}
