using System.IO;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>Par de jogos que provavelmente são o mesmo, com as chaves de agrupamento de cada um.</summary>
public record DuplicateCandidate(GameEntry A, GameEntry B, string KeyA, string KeyB);

/// <summary>
/// Chave de agrupamento dos títulos (mesmo jogo em lojas diferentes vira um card só), com:
///  - equivalências fixas no código (BuiltIn),
///  - equivalências que o usuário confirmou (%AppData%\GameHub\title_aliases.json),
///  - pares que o usuário marcou como "são diferentes" (title_ignored.json).
/// Também sugere pares de nomes parecidos que ainda não foram juntados.
/// </summary>
public static class TitleAliasService
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
    private static readonly string AliasPath = Path.Combine(Dir, "title_aliases.json");
    private static readonly string IgnoredPath = Path.Combine(Dir, "title_ignored.json");
    private static readonly object Lock = new();

    // Nomes diferentes entre lojas para o mesmo jogo: chave normalizada -> chave principal
    private static readonly Dictionary<string, string> BuiltIn = new()
    {
        // Epic chama de "Complete Collection", GOG chama de "Complete Edition"
        ["bioshockinfinitecompletecollection"] = "bioshockinfinitecompleteedition",
    };

    private static readonly Dictionary<string, string> Aliases = LoadJson<Dictionary<string, string>>(AliasPath) ?? new();
    private static readonly HashSet<string> Ignored = new(LoadJson<List<string>>(IgnoredPath) ?? new());

    // ── Chaves ──────────────────────────────────────────────────────────────

    private static string CleanTitle(string title) => title
        .Replace("™", "").Replace("®", "").Replace("(TM)", "").Replace("(R)", "").Trim();

    private static string Normalize(string title)
        => new string(CleanTitle(title).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Chave final de agrupamento: dois jogos com a mesma chave viram um card só.</summary>
    public static string GroupKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var key = Normalize(title);
        if (BuiltIn.TryGetValue(key, out var main)) key = main;
        lock (Lock) return Aliases.TryGetValue(key, out var user) ? user : key;
    }

    // ── Escolhas do usuário ──────────────────────────────────────────────────

    /// <summary>Junta: tudo que tinha a chave "keyFrom" passa a ter a chave "keyTo".</summary>
    public static void AddAlias(string keyFrom, string keyTo)
    {
        if (string.IsNullOrEmpty(keyFrom) || string.IsNullOrEmpty(keyTo) || keyFrom == keyTo) return;
        lock (Lock)
        {
            foreach (var k in Aliases.Where(kv => kv.Value == keyFrom).Select(kv => kv.Key).ToList())
                Aliases[k] = keyTo;
            Aliases[keyFrom] = keyTo;
            SaveJson(AliasPath, Aliases);
        }
    }

    /// <summary>"São diferentes": não sugere mais esse par.</summary>
    public static void Ignore(string keyA, string keyB)
    {
        lock (Lock)
        {
            Ignored.Add(PairKey(keyA, keyB));
            SaveJson(IgnoredPath, Ignored.ToList());
        }
    }

    private static string PairKey(string a, string b)
        => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    private static bool IsIgnored(string a, string b)
    {
        lock (Lock) return Ignored.Contains(PairKey(a, b));
    }

    // ── Sugestões ───────────────────────────────────────────────────────────

    private class Info
    {
        public GameEntry Game = null!;
        public string Key = "";
        public HashSet<string> Words = new();
        public HashSet<string> Numbers = new();
        public HashSet<Platform> Platforms = new();
    }

    private static HashSet<string> WordsOf(string title)
    {
        var words = new HashSet<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var c in CleanTitle(title).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) cur.Append(c);
            else if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        return words;
    }

    /// <summary>
    /// Pares com nomes parecidos, em lojas diferentes (nenhuma loja em comum - dois jogos da mesma
    /// loja são jogos distintos, como as expansões) e que ainda não viraram um card só.
    /// </summary>
    public static List<DuplicateCandidate> FindCandidates(IEnumerable<GameEntry> games)
    {
        var items = new List<Info>();
        foreach (var g in games)
        {
            if (g.Platform == Platform.Emulador || g.Platform == Platform.Modificado) continue;
            if (string.IsNullOrWhiteSpace(g.Name)) continue;

            var words = WordsOf(g.Name);
            if (words.Count == 0) continue;
            var plats = (g.Platforms.Count > 0 ? g.Platforms : new List<Platform> { g.Platform }).ToHashSet();
            items.Add(new Info
            {
                Game = g,
                Key = GroupKey(g.Name),
                Words = words,
                Numbers = words.Where(w => w.All(char.IsDigit)).ToHashSet(),
                Platforms = plats
            });
        }

        var result = new List<DuplicateCandidate>();
        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i];
                var b = items[j];
                if (a.Key == b.Key) continue;
                if (a.Platforms.Overlaps(b.Platforms)) continue;
                // Os números precisam coincidir (Far Cry 3 x Far Cry 5, Battlefield 1 x 4)
                if (!a.Numbers.SetEquals(b.Numbers)) continue;

                int inter = a.Words.Intersect(b.Words).Count();
                int union = a.Words.Union(b.Words).Count();
                if (union == 0 || (double)inter / union < 0.6) continue;
                if (IsIgnored(a.Key, b.Key)) continue;

                result.Add(new DuplicateCandidate(a.Game, b.Game, a.Key, b.Key));
            }
        }
        return result.OrderBy(c => c.A.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ── Arquivos ────────────────────────────────────────────────────────────

    private static T? LoadJson<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize<T>(fs);
        }
        catch (Exception ex)
        {
            Logger.LogError("Ler " + Path.GetFileName(path), ex);
            return default;
        }
    }

    private static void SaveJson<T>(string path, T data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Logger.LogError("Gravar " + Path.GetFileName(path), ex); }
    }
}
