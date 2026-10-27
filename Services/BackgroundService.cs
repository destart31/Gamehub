using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Plano de fundo por jogo: manual > Steam (local/CDN) > SteamGridDB (por AppId ou nome) > nada.
/// Quando não há imagem, quem chama usa um degradê com a cor média da capa.
/// </summary>
public static class BackgroundService
{
    private static readonly string AppDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
    private static readonly string Dir = Path.Combine(AppDir, "Backgrounds");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static readonly Queue<string> CacheOrder = new();
    private static readonly HashSet<string> NoArt = new();       // appids sem arte na CDN (nesta sessão)
    private static readonly HashSet<string> SgdbSessionMiss = new(); // falhas de rede (nesta sessão)
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim SgdbGate = new(2);
    private const int CacheMax = 4;

    private const string SgdbBase = "https://www.steamgriddb.com/api/v2/";
    private const string HeroQuery =
        "dimensions=1920x620,3840x1240,1600x650&types=static&mimes=image/png,image/jpeg&nsfw=false&humor=false";

    private static string? _sgdbKey;
    private static bool _sgdbKeyLoaded;

    public static Task<BitmapSource?> GetAsync(GameEntry game) => Task.Run(async () =>
    {
        var key = game.UserKey;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit)) return (BitmapSource?)hit;
        }

        BitmapSource? bmp = null;

        var manual = ManualPath(game);
        if (File.Exists(manual)) bmp = Load(manual);

        var appId = SteamAppId(game);

        if (bmp == null && appId != null)
        {
            var local = FindSteamLocalHero(appId);
            if (local != null) bmp = Load(local);

            if (bmp == null)
            {
                var downloaded = await DownloadSteamHeroAsync(appId);
                if (downloaded != null) bmp = Load(downloaded);
            }
        }

        if (bmp == null)
        {
            var sgdb = await GetSgdbHeroAsync(game, appId);
            if (sgdb != null)
            {
                bmp = Load(sgdb);
                if (bmp == null) { try { File.Delete(sgdb); } catch { } } // arquivo corrompido: baixa de novo depois
            }
        }

        if (bmp != null) Remember(key, bmp);
        return bmp;
    });

    /// <summary>Usa a imagem escolhida pelo usuário. Reduz para no máximo 1920 px de largura.</summary>
    public static bool SetManual(GameEntry game, string sourcePath)
    {
        try
        {
            var probe = new BitmapImage();
            probe.BeginInit();
            probe.CacheOption = BitmapCacheOption.OnLoad;
            probe.UriSource = new Uri(sourcePath);
            probe.EndInit();

            BitmapSource final = probe;
            if (probe.PixelWidth > 3840)
            {
                var small = new BitmapImage();
                small.BeginInit();
                small.CacheOption = BitmapCacheOption.OnLoad;
                small.DecodePixelWidth = 3840;
                small.UriSource = new Uri(sourcePath);
                small.EndInit();
                final = small;
            }

            Directory.CreateDirectory(Dir);
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(final));
            var target = ManualPath(game);
            var tmp = target + ".tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, target, overwrite: true);

            Forget(game.UserKey);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError("Fundo manual", ex);
            return false;
        }
    }

    public static void RemoveManual(GameEntry game)
    {
        try
        {
            var p = ManualPath(game);
            if (File.Exists(p)) File.Delete(p);
        }
        catch (Exception ex) { Logger.LogError("Remover fundo manual", ex); }
        Forget(game.UserKey);
    }

    /// <summary>Cor média da imagem (amostra pequena). Usada no degradê quando o jogo não tem fundo.</summary>
    public static Color? AverageColor(BitmapSource? src)
    {
        if (src == null || src.PixelWidth <= 0 || src.PixelHeight <= 0) return null;
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            long r = 0, g = 0, b = 0;
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                b += px[i * 4];
                g += px[i * 4 + 1];
                r += px[i * 4 + 2];
            }
            if (n == 0) return null;
            return Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
        }
        catch { return null; }
    }
        // ── busca em lote ────────────────────────────────────────────────────────

    public static bool HasSgdbKey => SgdbKey() != null;

    /// <summary>Esquece os "não achou" para tentar de novo (quem chama é o botão Buscar fundos).</summary>
    public static void ResetMisses()
    {
        lock (Gate) { SgdbSessionMiss.Clear(); }
        try
        {
            if (Directory.Exists(Dir))
                foreach (var f in Directory.EnumerateFiles(Dir, "a_*.none")) File.Delete(f);
        }
        catch { }
    }

    private static bool HasBackground(GameEntry g, string? appId)
    {
        if (File.Exists(ManualPath(g))) return true;
        if (appId != null && (FindSteamLocalHero(appId) != null
                              || File.Exists(Path.Combine(Dir, "s_" + appId + ".jpg")))) return true;
        return File.Exists(Path.Combine(Dir, "a_" + Hash(g.UserKey) + ".img"));
    }

    /// <summary>Baixa o fundo de todos os jogos da lista. Devolve (quantos processou, quantos têm fundo).</summary>
    public static async Task<(int Done, int Have)> PrefetchAsync(
        IReadOnlyList<GameEntry> games, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        int done = 0, have = 0, total = games.Count;
        try
        {
            await Parallel.ForEachAsync(games,
                new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (g, token) =>
                {
                    try
                    {
                        var appId = SteamAppId(g);
                        if (!HasBackground(g, appId))
                        {
                            if (appId != null) await DownloadSteamHeroAsync(appId);
                            if (!HasBackground(g, appId)) await GetSgdbHeroAsync(g, appId);
                        }
                        if (HasBackground(g, appId)) Interlocked.Increment(ref have);
                    }
                    catch (Exception ex) { Logger.LogError("Buscar fundos", ex); }
                    progress?.Report((Interlocked.Increment(ref done), total));
                });
        }
        catch (OperationCanceledException) { }
        return (done, have);
    }

    // ── SteamGridDB ──────────────────────────────────────────────────────────

    private static string? SgdbKey()
    {
        if (_sgdbKeyLoaded) return _sgdbKey;
        _sgdbKeyLoaded = true;
        try
        {
            var p = Path.Combine(AppDir, "secrets.json");
            if (File.Exists(p))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty("SteamGridDbKey", out var v))
                {
                    var s = v.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) _sgdbKey = s.Trim();
                }
            }
        }
        catch (Exception ex) { Logger.LogError("secrets.json", ex); }
        return _sgdbKey;
    }

    private static async Task<string?> GetSgdbHeroAsync(GameEntry game, string? steamAppId)
    {
        var apiKey = SgdbKey();
        if (apiKey == null) return null;

        var hash = Hash(game.UserKey);
        var target = Path.Combine(Dir, "a_" + hash + ".img");
        var miss = Path.Combine(Dir, "a_" + hash + ".none");

        if (File.Exists(target)) return target;
        if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < TimeSpan.FromDays(14))
            return null;
        lock (Gate) { if (SgdbSessionMiss.Contains(hash)) return null; }

        await SgdbGate.WaitAsync();
        try
        {
            if (File.Exists(target)) return target; // outro foco pode ter baixado enquanto esperava

            Directory.CreateDirectory(Dir);
            string? url = null;

            if (steamAppId != null)
                url = PickHero(await GetJsonAsync($"heroes/steam/{steamAppId}?{HeroQuery}", apiKey));

            if (url == null)
            {
                var id = await FindGameIdAsync(game.Name, apiKey);
                if (id != null)
                    url = PickHero(await GetJsonAsync($"heroes/game/{id}?{HeroQuery}", apiKey));
            }

            if (url == null)
            {
                File.WriteAllText(miss, DateTime.UtcNow.ToString("O")); // não achou: tenta de novo em 14 dias
                return null;
            }

            using var resp = await Http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            var tmp = target + ".tmp";
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs);
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            lock (Gate) { SgdbSessionMiss.Add(hash); } // sem internet / limite: não insiste nesta sessão
            Logger.LogError("SteamGridDB", ex);
            return null;
        }
        finally { SgdbGate.Release(); }
    }

    /// <summary>GET na API. Devolve o JSON, ou null se for 404. Outros erros viram exceção.</summary>
    private static async Task<string?> GetJsonAsync(string path, string apiKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SgdbBase + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        using var resp = await Http.SendAsync(req);
        if ((int)resp.StatusCode == 404) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>Escolhe o fundo com maior nota (e, no empate, o mais largo).</summary>
    private static string? PickHero(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            string? bestUrl = null;
            long bestRank = long.MinValue;
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("url", out var u) || u.GetString() is not { Length: > 0 } url) continue;
                int score = item.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
                int width = item.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                long rank = (long)score * 100000 + width;
                if (rank > bestRank) { bestRank = rank; bestUrl = url; }
            }
            return bestUrl;
        }
        catch { return null; }
    }

    /// <summary>Procura o jogo pelo nome. Só aceita nome igual (ou que começa igual); senão, nada.</summary>
    private static async Task<long?> FindGameIdAsync(string? rawName, string apiKey)
    {
        var name = CleanName(rawName);
        if (name.Length == 0) return null;

        var queries = new List<string> { name };
        var colon = name.IndexOf(':');
        if (colon > 2) queries.Add(name[..colon].Trim());
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 2) queries.Add(name[..dash].Trim());

        var seen = new HashSet<string>();
        for (int qi = 0; qi < queries.Count; qi++)
        {
            var q = queries[qi];
            if (!seen.Add(Norm(q))) continue;

            var json = await GetJsonAsync("search/autocomplete/" + Uri.EscapeDataString(q), apiKey);
            if (string.IsNullOrEmpty(json)) continue;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                continue;

            var nq = Norm(q);
            int minScore = qi == 0 ? 2 : 3; // nas buscas encurtadas, só aceita nome idêntico
            long? bestId = null;
            int bestScore = 0;

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                var cand = Norm(item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
                if (cand.Length == 0) continue;

                int score = cand == nq ? 3
                          : (cand.StartsWith(nq, StringComparison.Ordinal) || nq.StartsWith(cand, StringComparison.Ordinal)) ? 2
                          : 0;
                if (score > bestScore) { bestScore = score; bestId = idEl.GetInt64(); }
            }

            if (bestId != null && bestScore >= minScore) return bestId;
        }
        return null;
    }

    /// <summary>"Super Mario 64 (USA) [!]" vira "Super Mario 64".</summary>
    private static string CleanName(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "";
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\(.*?\)|\[.*?\]", "");
        n = n.Replace('_', ' ');
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ");
        return n.Trim();
    }

    /// <summary>Minúsculas, só letras e números (ignora : - ™ ® ' etc.).</summary>
    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ── internos ─────────────────────────────────────────────────────────────

    private static string ManualPath(GameEntry game) => Path.Combine(Dir, "m_" + Hash(game.UserKey) + ".jpg");

    private static string Hash(string s)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(s ?? ""));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Só devolve o appid quando dá pra ter certeza de que é da Steam (nunca arte errada).</summary>
    private static string? SteamAppId(GameEntry g)
    {
        const string prefix = "steam://rungameid/";
        var uri = g.LaunchUri;
        if (!string.IsNullOrEmpty(uri) && uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.Substring(prefix.Length).Trim('/');
            if (id.Length > 0 && id.All(char.IsDigit)) return id;
        }
        if (g.Platform == Platform.Steam && !string.IsNullOrEmpty(g.AppId) && g.AppId.All(char.IsDigit))
            return g.AppId;
        return null;
    }

    private static string? SteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var dir = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(dir)) return Path.GetFullPath(dir);
        }
        catch { }
        return null;
    }

    /// <summary>A Steam guarda a arte em appcache\librarycache\{appid}\{hash}\library_hero.jpg.</summary>
    private static string? FindSteamLocalHero(string appId)
    {
        try
        {
            var root = SteamRoot();
            if (root == null) return null;
            var cache = Path.Combine(root, "appcache", "librarycache");

            var old = Path.Combine(cache, appId + "_library_hero.jpg"); // formato antigo
            if (File.Exists(old)) return old;

            var dir = Path.Combine(cache, appId);
            if (!Directory.Exists(dir)) return null;

            return Directory.EnumerateFiles(dir, "library_hero.jpg", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static async Task<string?> DownloadSteamHeroAsync(string appId)
    {
        try
        {
            lock (Gate) { if (NoArt.Contains(appId)) return null; }

            var target = Path.Combine(Dir, "s_" + appId + ".jpg");
            if (File.Exists(target)) return target;

            Directory.CreateDirectory(Dir);
            var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_hero.jpg";
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404) lock (Gate) { NoArt.Add(appId); }
                return null;
            }

            var tmp = target + ".tmp";
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs);
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Logger.LogError("Baixar fundo da Steam", ex); // sem internet etc.: segue sem fundo
            return null;
        }
    }

    private static BitmapSource? Load(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static void Remember(string key, BitmapSource bmp)
    {
        lock (Gate)
        {
            if (!Cache.ContainsKey(key)) CacheOrder.Enqueue(key);
            Cache[key] = bmp;
            while (CacheOrder.Count > CacheMax) Cache.Remove(CacheOrder.Dequeue());
        }
    }

    private static void Forget(string key)
    {
        lock (Gate) { Cache.Remove(key); }
    }
}