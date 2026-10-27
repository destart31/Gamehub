using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using GameHub.Models;
 
namespace GameHub.Services;
 
/// <summary>
/// Pesquisa tolerante: além de procurar o texto no nome, entende siglas e apelidos.
///   • "gta5", "rdr2", "tlou", "mk11": siglas montadas a partir das iniciais do título (automático).
///   • "fifa", "cod", "bf4", "cs2": apelidos da lista abaixo, que apontam para o nome real.
///   • Seus próprios apelidos: %AppData%\GameHub\search_aliases.json
///       { "apelido": ["parte do nome", "outra parte"], "w3": ["witcher 3"] }
///     (valem em cima da lista padrão; o arquivo é lido uma vez, ao abrir o programa).
/// </summary>
public static class SearchAliasService
{
    private static readonly HashSet<string> Stop = new()
        { "the", "of", "a", "an", "and", "de", "da", "do", "das", "dos", "e", "o", "os", "as" };
 
    private static readonly HashSet<string> Roman = new()
        { "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii", "xiii" };
 
    // apelido -> pedaços do nome real (já sem acento e pontuação). "bf4" funciona sozinho: apelido + número.
    private static readonly Dictionary<string, string[]> Defaults = new()
    {
        ["fifa"]  = new[] { "ea sports fc", "fifa" },
        ["pes"]   = new[] { "efootball", "pro evolution soccer" },
        ["gta"]   = new[] { "grand theft auto" },
        ["gtasa"] = new[] { "grand theft auto san andreas" },
        ["gtavc"] = new[] { "grand theft auto vice city" },
        ["cod"]   = new[] { "call of duty" },
        ["mw"]    = new[] { "modern warfare" },
        ["bf"]    = new[] { "battlefield" },
        ["cs"]    = new[] { "counter strike" },
        ["csgo"]  = new[] { "counter strike" },
        ["cs2"]   = new[] { "counter strike" },
        ["rdr"]   = new[] { "red dead" },
        ["re"]    = new[] { "resident evil" },
        ["ff"]    = new[] { "final fantasy" },
        ["ds"]    = new[] { "dark souls" },
        ["er"]    = new[] { "elden ring" },
        ["mgs"]   = new[] { "metal gear" },
        ["mk"]    = new[] { "mortal kombat" },
        ["sf"]    = new[] { "street fighter" },
        ["nfs"]   = new[] { "need for speed" },
        ["ac"]    = new[] { "assassins creed" },
        ["hl"]    = new[] { "half life" },
        ["l4d"]   = new[] { "left 4 dead" },
        ["tlou"]  = new[] { "last of us" },
        ["gow"]   = new[] { "god of war" },
        ["hzd"]   = new[] { "horizon zero dawn" },
        ["fh"]    = new[] { "forza horizon" },
        ["fm"]    = new[] { "forza motorsport" },
        ["tw"]    = new[] { "the witcher" },
        ["w3"]    = new[] { "witcher 3" },
        ["lol"]   = new[] { "league of legends" },
        ["wow"]   = new[] { "world of warcraft" },
        ["sc"]    = new[] { "starcraft" },
        ["mc"]    = new[] { "minecraft" },
        ["poke"]  = new[] { "pokemon" },
        ["pkmn"]  = new[] { "pokemon" },
        ["ssb"]   = new[] { "smash bros" },
        ["smash"] = new[] { "smash bros" },
        ["botw"]  = new[] { "breath of the wild" },
        ["totk"]  = new[] { "tears of the kingdom" },
        ["loz"]   = new[] { "legend of zelda" },
        ["smb"]   = new[] { "super mario bros" },
        ["dmc"]   = new[] { "devil may cry" },
        ["cp"]    = new[] { "cyberpunk" },
        ["hp"]    = new[] { "harry potter", "hogwarts" },
        ["tes"]   = new[] { "elder scrolls" },
        ["fo"]    = new[] { "fallout" },
        ["kh"]    = new[] { "kingdom hearts" },
        ["dq"]    = new[] { "dragon quest" },
        ["dbz"]   = new[] { "dragon ball" },
        ["tf"]    = new[] { "team fortress" },
        ["ow"]    = new[] { "overwatch" },
        ["r6"]    = new[] { "rainbow six" },
        ["tr"]    = new[] { "tomb raider" },
        ["wd"]    = new[] { "watch dogs" },
        ["bg"]    = new[] { "baldurs gate" },
    };
 
    private static readonly object Lock = new();
    private static Dictionary<string, string[]>? _all;
    private static readonly ConcurrentDictionary<string, string[]> InitialsCache = new();
 
    // ── API ──────────────────────────────────────────────────────────────────
 
    /// <summary>
    /// Filtra a lista pela pesquisa. Se o que foi digitado é um apelido conhecido ("cs", "fifa", "bf4")
    /// e algum jogo bate com ele, mostra SÓ esses; senão, a busca normal (texto e siglas).
    /// </summary>
    public static IEnumerable<GameEntry> Filter(IEnumerable<GameEntry> games, string query)
    {
        var list = games.ToList();
        var qc = Normalize(query).Replace(" ", "");
        if (qc.Length >= 2)
        {
            var strict = list.Where(g => AliasMatch(Normalize(g.Name), qc)).ToList();
            if (strict.Count > 0) return strict;
        }
        return list.Where(g => Matches(g.Name, query));
    }
 
    /// <summary>true se o nome do jogo combina com o que foi digitado (texto, sigla ou apelido).</summary>
    public static bool Matches(string name, string query)
    {
        var q = Normalize(query);
        if (q.Length == 0) return true;
 
        var n = Normalize(name);
        if (n.Contains(q)) return true;
 
        var qc = q.Replace(" ", "");
        // "fc24" acha "EA SPORTS FC 24". Só quando há número na busca: sem isso, juntar as palavras
        // cria falsos achados ("lego" em "princip(le go)ld", "cs" em "LEGO D(C S)uper")
        if (qc.Length >= 3 && qc.Any(char.IsDigit) && n.Replace(" ", "").Contains(qc)) return true;
        if (qc.Length < 2) return false;
 
        // Siglas pelas iniciais do título (gta5, rdr2, tlou, mk11...)
        foreach (var ini in InitialsOf(n))
            if (ini.StartsWith(qc) || (qc.Length >= 4 && ini.Contains(qc))) return true;
 
        // Apelidos (fifa, cod, bf4...)
        return AliasMatch(n, qc);
    }
 
    // ── Normalização ─────────────────────────────────────────────────────────
 
    private static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        bool lastSpace = true;
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;   // tira acentos
            if (ch is '\'' or '’' or '™' or '®' or '©') continue; // "assassin's" -> "assassins"
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Trim();
    }
 
    private static string RomanToDigit(string t) => t switch
    {
        "ii" => "2", "iii" => "3", "iv" => "4", "v" => "5", "vi" => "6", "vii" => "7",
        "viii" => "8", "ix" => "9", "x" => "10", "xi" => "11", "xii" => "12", "xiii" => "13",
        _ => t
    };
 
    private static bool IsNumber(string t) => t.Length > 0 && t.All(char.IsDigit);
 
    // ── Siglas ───────────────────────────────────────────────────────────────
 
    private static string[] InitialsOf(string normalizedName)
        => InitialsCache.GetOrAdd(normalizedName, BuildInitials);
 
    private static string[] BuildInitials(string n)
    {
        var tokens = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>();
 
        foreach (bool skipStop in new[] { false, true })
        foreach (bool romanAsDigit in new[] { false, true })
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Length; i++)
            {
                var t = tokens[i];
                if (skipStop && i > 0 && Stop.Contains(t)) continue;
                if (skipStop && i == 0 && Stop.Contains(t) && tokens.Length > 1) continue;
 
                if (IsNumber(t)) sb.Append(t);                                   // 2077, 3, 5
                else if (i > 0 && Roman.Contains(t)) sb.Append(romanAsDigit ? RomanToDigit(t) : t); // v / 5
                else sb.Append(t[0]);                                            // iniciais
            }
            if (sb.Length >= 2) set.Add(sb.ToString());
        }
        return set.ToArray();
    }
 
    // ── Apelidos ─────────────────────────────────────────────────────────────
 
    private static Dictionary<string, string[]> All()
    {
        lock (Lock)
        {
            if (_all != null) return _all;
 
            var map = new Dictionary<string, string[]>();
            foreach (var kv in Defaults) map[kv.Key] = kv.Value.Select(Normalize).ToArray();
 
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "search_aliases.json");
                if (File.Exists(path))
                {
                    var user = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
                    if (user != null)
                        foreach (var kv in user)
                        {
                            var key = Normalize(kv.Key).Replace(" ", "");
                            if (key.Length > 0 && kv.Value is { Length: > 0 })
                                map[key] = kv.Value.Select(Normalize).Where(x => x.Length > 0).ToArray();
                        }
                }
            }
            catch { /* arquivo com erro: fica só a lista padrão */ }
 
            return _all = map;
        }
    }
 
    private static bool AliasMatch(string n, string qc)
    {
        string[]? tokens = null;
 
        foreach (var (key, terms) in All())
        {
            if (!qc.StartsWith(key)) continue;
            if (!terms.Any(t => n.Contains(t))) continue;
 
            var rest = qc[key.Length..];
            if (rest.Length == 0) return true;
 
            // apelido + número ("bf4", "gta5", "cp77", "fifav"?): o número precisa existir no nome
            if (!IsNumber(rest) && !Roman.Contains(rest)) continue;
            tokens ??= n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
 
            var cands = new[] { rest, RomanToDigit(rest) };
            foreach (var t in tokens)
            {
                var td = RomanToDigit(t);
                if (cands.Any(c => t == c || td == c || (c.Length >= 2 && t.EndsWith(c)))) return true;
            }
        }
        return false;
    }
}
