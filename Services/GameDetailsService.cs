using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameHub.Models;

namespace GameHub.Services;

public class ShotInfo
{
    public string Thumb { get; set; } = "";
    public string Full { get; set; } = "";
}

/// <summary>Dados extras do painel de detalhes (capturas, requisitos, idiomas, site oficial).</summary>
public class GameDetails
{
    public string? SteamAppId { get; set; }          // null = o jogo não foi achado na loja da Steam
    public List<ShotInfo> Screenshots { get; set; } = new();
    public string? Website { get; set; }
    public string? Portuguese { get; set; }          // ex.: "Português do Brasil"; null = sem português
    public bool HasLanguageInfo { get; set; }
    public string? MinRequirements { get; set; }
    public string? RecRequirements { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Busca capturas, requisitos e idiomas na loja pública da Steam (a mesma API que a página da loja usa,
/// sem login nem chave). Jogos de outras lojas só recebem os dados se acharmos na Steam um jogo com
/// EXATAMENTE o mesmo nome - nunca mostramos dados de um jogo parecido.
/// O resultado (inclusive "não achei") fica em %AppData%\GameHub\Details para não repetir a busca.
/// </summary>
public static class GameDetailsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Throttle = new(2, 2);
    private static readonly Dictionary<string, GameDetails> Memory = new();
    private static readonly object Lock = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "Details");

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("GameHub/1.0");
        return c;
    }

    /// <summary>Devolve os detalhes (do cache ou da Steam). null = ROM/modificado, ou sem rede e sem cache.</summary>
    public static async Task<GameDetails?> GetAsync(GameEntry game)
    {
        if (game.Platform == Platform.Emulador || game.Platform == Platform.Modificado) return null;

        var file = Path.Combine(Dir,
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(game.UserKey)))[..16] + ".json");

        lock (Lock)
        {
            if (Memory.TryGetValue(file, out var mem)) return mem;
        }

        var cached = await Task.Run(() => LoadFile(file));
        if (cached != null)
        {
            var ttl = cached.SteamAppId == null ? TimeSpan.FromDays(14) : TimeSpan.FromDays(30);
            if (DateTime.Now - cached.FetchedAt < ttl)
            {
                lock (Lock) Memory[file] = cached;
                return cached;
            }
        }

        await Throttle.WaitAsync();
        try
        {
            var d = new GameDetails();
            d.SteamAppId = SteamAppIdOf(game) ?? await SearchSteamAppIdAsync(game.Name);

            if (d.SteamAppId != null && !await FillFromSteamAsync(d, d.SteamAppId))
                d.SteamAppId = null; // a loja não tem (mais) a página desse jogo

            d.FetchedAt = DateTime.Now;
            await Task.Run(() => SaveFile(file, d));
            lock (Lock) Memory[file] = d;
            return d;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return cached; // sem rede: usa o cache vencido, se houver (e tenta de novo na próxima vez)
        }
        finally
        {
            Throttle.Release();
        }
    }

    private static string? SteamAppIdOf(GameEntry game)
        => game.Platform == Platform.Steam && !string.IsNullOrWhiteSpace(game.AppId) && game.AppId.All(char.IsDigit)
            ? game.AppId : null;

    private static string CleanName(string name)
        => name.Replace("™", "").Replace("®", "").Replace("(TM)", "").Replace("(R)", "").Trim();

    /// <summary>Procura o jogo pelo nome na loja da Steam; só aceita nome idêntico (ignorando símbolos).</summary>
    private static async Task<string?> SearchSteamAppIdAsync(string name)
    {
        var url = "https://store.steampowered.com/api/storesearch/?term="
                  + Uri.EscapeDataString(CleanName(name)) + "&l=portuguese&cc=br";
        var json = await Http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        var wanted = TitleAliasService.GroupKey(name);
        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var n) || !item.TryGetProperty("id", out var id)) continue;
            if (item.TryGetProperty("type", out var t) && t.GetString() != "app") continue;
            if (TitleAliasService.GroupKey(n.GetString()) == wanted)
                return id.GetInt64().ToString();
        }
        return null;
    }

    private static async Task<bool> FillFromSteamAsync(GameDetails d, string appId)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=portuguese&cc=br";
        var json = await Http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(appId, out var entry)) return false;
        if (!entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return false;
        if (!entry.TryGetProperty("data", out var data)) return false;

        // Capturas
        if (data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in shots.EnumerateArray())
            {
                var thumb = s.TryGetProperty("path_thumbnail", out var tp) ? tp.GetString() : null;
                var full = s.TryGetProperty("path_full", out var fp) ? fp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(thumb) && !string.IsNullOrWhiteSpace(full))
                    d.Screenshots.Add(new ShotInfo { Thumb = thumb!, Full = full! });
                if (d.Screenshots.Count >= 12) break;
            }
        }

        // Site oficial
        if (data.TryGetProperty("website", out var web) && web.ValueKind == JsonValueKind.String)
        {
            var w = web.GetString();
            if (!string.IsNullOrWhiteSpace(w) && w!.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                d.Website = w;
        }

        // Requisitos de PC (vêm em HTML; some quando o jogo não informa)
        if (data.TryGetProperty("pc_requirements", out var req) && req.ValueKind == JsonValueKind.Object)
        {
            if (req.TryGetProperty("minimum", out var min) && min.ValueKind == JsonValueKind.String)
                d.MinRequirements = RequirementsToText(min.GetString());
            if (req.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.String)
                d.RecRequirements = RequirementsToText(rec.GetString());
        }

        // Idiomas: guarda só o(s) português(es) da lista
        if (data.TryGetProperty("supported_languages", out var langs) && langs.ValueKind == JsonValueKind.String)
        {
            var raw = langs.GetString() ?? "";
            var list = Regex.Split(raw, @"<br\s*/?>", RegexOptions.IgnoreCase)[0];
            list = System.Net.WebUtility.HtmlDecode(Regex.Replace(list, "<.*?>", "")).Replace("*", "");
            var pt = list.Split(',').Select(x => x.Trim())
                         .Where(x => x.StartsWith("Portugu", StringComparison.OrdinalIgnoreCase)).ToList();
            d.HasLanguageInfo = list.Trim().Length > 0;
            d.Portuguese = pt.Count > 0 ? string.Join(", ", pt) : null;
        }

        return true;
    }

    private static string? RequirementsToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var s = Regex.Replace(html, @"<br\s*/?>|</li>|</p>", "\n", RegexOptions.IgnoreCase);
        s = System.Net.WebUtility.HtmlDecode(Regex.Replace(s, "<.*?>", ""));
        var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // Primeira linha tipo "Mínimos:" / "Recomendados:" - o painel já tem o título da coluna
        if (lines.Count > 0 && lines[0].EndsWith(":") && lines[0].Length < 22) lines.RemoveAt(0);

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private static GameDetails? LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<GameDetails>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void SaveFile(string path, GameDetails d)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* sem permissão ou disco cheio: só não guarda desta vez */ }
    }
}
