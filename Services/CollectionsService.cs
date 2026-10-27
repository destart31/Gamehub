using System.IO;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Coleções próprias do usuário (ex.: "Zerar neste ano", "Com amigos"): cada coleção é um nome
/// e um conjunto de jogos (GameEntry.UserKey, a mesma chave dos favoritos).
/// Fica em %AppData%\GameHub\collections.json, então uma atualização do GameHub nunca apaga isso.
/// </summary>
public static class CollectionsService
{
    private class Collection
    {
        public string Name { get; set; } = "";
        public HashSet<string> Games { get; set; } = new();
    }

    private class Store
    {
        public List<Collection> Collections { get; set; } = new();
    }

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "collections.json");

    private static readonly object Gate = new();
    private static readonly Store Data = Load();

    private static Collection? Find(string name)
        => Data.Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Nomes das coleções, na ordem em que foram criadas.</summary>
    public static List<string> Names()
    {
        lock (Gate) return Data.Collections.Select(c => c.Name).ToList();
    }

    public static bool Exists(string name)
    {
        lock (Gate) return Find(name.Trim()) != null;
    }

    /// <summary>Cria a coleção. Devolve false se o nome estiver vazio ou já existir.</summary>
    public static bool Create(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return false;
        lock (Gate)
        {
            if (Find(name) != null) return false;
            Data.Collections.Add(new Collection { Name = name });
            Save();
            return true;
        }
    }

    /// <summary>Renomeia (mantém os jogos). Devolve false se o novo nome for inválido ou já existir.</summary>
    public static bool Rename(string oldName, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return false;
        lock (Gate)
        {
            var c = Find(oldName);
            if (c == null) return false;
            var other = Find(newName);
            if (other != null && !ReferenceEquals(other, c)) return false;
            c.Name = newName;
            Save();
            return true;
        }
    }

    /// <summary>Exclui só a coleção - os jogos continuam na biblioteca.</summary>
    public static void Delete(string name)
    {
        lock (Gate)
        {
            var c = Find(name);
            if (c == null) return;
            Data.Collections.Remove(c);
            Save();
        }
    }

    public static bool Contains(string name, GameEntry game)
    {
        lock (Gate) return Find(name)?.Games.Contains(game.UserKey) ?? false;
    }

    /// <summary>Põe o jogo na coleção, ou tira se já estava.</summary>
    public static void Toggle(string name, GameEntry game)
    {
        lock (Gate)
        {
            var c = Find(name);
            if (c == null) return;
            if (!c.Games.Remove(game.UserKey)) c.Games.Add(game.UserKey);
            Save();
        }
    }

    private static Store Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new Store();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Store>(json) ?? new Store();
        }
        catch
        {
            return new Store();
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
