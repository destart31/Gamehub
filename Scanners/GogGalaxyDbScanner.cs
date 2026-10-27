using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using GameHub.Models;
using System.Text.RegularExpressions;

namespace GameHub.Scanners;

public class GogGalaxyDbScanner
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GOG.com", "Galaxy", "storage", "galaxy-2.0.db");

    // O Galaxy guarda título nulo para alguns jogos da EA; nomes definidos à mão
    private static readonly Dictionary<string, string> FallbackTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["origin_Origin.OFR.50.0005162"] = "EA SPORTS FC 24",
        ["origin_Origin.OFR.50.0005393"] = "EA SPORTS FC 25",
    };

    // Edições/chaves antigas da EA que são o mesmo jogo: nome exibido
    private static readonly Dictionary<string, string> EaCanonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Sims 4 Digital Deluxe"] = "The Sims 4",
        ["Battlefield V Definitive Edition"] = "Battlefield V",
    };

    public List<GameEntry> ScanAcquiredGames()
    {
        var games = new List<GameEntry>();
        if (!File.Exists(DbPath))
            return games;

        var tempDb = Path.Combine(Path.GetTempPath(), $"galaxy_temp_{Guid.NewGuid():N}.db");
        var activeDbPath = DbPath;

        try
        {
            // Tenta copiar para evitar lock do GOG Galaxy em background
            File.Copy(DbPath, tempDb, overwrite: true);
            activeDbPath = tempDb;
        }
        catch { /* Fallback para o arquivo original se a cópia falhar (permissão, etc) */ }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = activeDbPath,
                Mode = SqliteOpenMode.ReadOnly
            };

            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();

            var gameTimes = LoadGameTimes(connection);
            var lastPlayed = LoadLastPlayed(connection);
            var summaries = LoadSummaries(connection);
            var metaData = LoadMetaData(connection);

            // Seleciona os títulos da biblioteca vinculada do GOG Galaxy
            var query = @"
                SELECT DISTINCT lr.releaseKey, gp.value
                FROM LibraryReleases lr
                JOIN GamePieces gp ON lr.releaseKey = gp.releaseKey
                JOIN GamePieceTypes gpt ON gp.gamePieceTypeId = gpt.id
                WHERE gpt.type IN ('title', 'originalTitle')";

            using var command = new SqliteCommand(query, connection);
            using var reader = command.ExecuteReader();

            var seenEntries = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);

            while (reader.Read())
            {
                var releaseKey = reader.GetString(0);
                var jsonValue = reader.GetString(1);

                if (string.IsNullOrWhiteSpace(releaseKey) || string.IsNullOrWhiteSpace(jsonValue))
                    continue;

                var (platform, appId) = ParseReleaseKey(releaseKey);
                if (platform == null) continue;

                var title = ExtractTitle(jsonValue);
                if (string.IsNullOrWhiteSpace(title) && FallbackTitles.TryGetValue(releaseKey, out var fb))
                    title = fb;
                if (string.IsNullOrWhiteSpace(title)) continue;

                title = CleanTitle(title);

                if (platform == Platform.EaApp && EaCanonical.TryGetValue(title, out var canon))
                    title = canon;

                var dedupeKey = $"{platform}_{title}";
                if (seenEntries.TryGetValue(dedupeKey, out var dup))
                {
                    // Mesmo jogo, outra chave da mesma loja: fica o MAIOR tempo (não soma)
                    if (gameTimes.TryGetValue(releaseKey, out var m2) && m2 > dup.PlaytimeMinutes)
                    {
                        dup.PlaytimeMinutes = m2;
                        dup.PlaytimeByPlatform[platform.Value] = m2;
                    }
                    if (lastPlayed.TryGetValue(releaseKey, out var lp2) &&
                        (!dup.LastPlayedByPlatform.TryGetValue(platform.Value, out var oldLp) || lp2 > oldLp))
                    {
                        dup.LastPlayed = lp2;
                        dup.LastPlayedByPlatform[platform.Value] = lp2;
                    }
                    continue;
                }

                var entry = new GameEntry
                {
                    Name = title,
                    Platform = platform.Value,
                    Platforms = new List<Platform> { platform.Value },
                    AppId = appId,
                    ReleaseKey = releaseKey,
                    IsInstalled = false
                };

                // Associa metadados extras se disponíveis
                if (gameTimes.TryGetValue(releaseKey, out var minutes))
                {
                    entry.PlaytimeMinutes = minutes;
                    entry.PlaytimeByPlatform[platform.Value] = minutes;
                }

                if (lastPlayed.TryGetValue(releaseKey, out var lp))
                {
                    entry.LastPlayed = lp;
                    entry.LastPlayedByPlatform[platform.Value] = lp;
                }

                if (summaries.TryGetValue(releaseKey, out var summaryText))
                    entry.Summary = summaryText;

                if (metaData.TryGetValue(releaseKey, out var meta))
                {
                    entry.Genres = meta.Genres;
                    entry.Developer = meta.Developer;
                    entry.Publisher = meta.Publisher;
                    entry.CriticsScore = meta.CriticsScore;
                }

                games.Add(entry);
                seenEntries[dedupeKey] = entry;
            }
        }
        catch
        {
            // Tratamento de falha de leitura sem crashar a aplicação
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                // Força o Garbage Collector a rodar e fechar a conexão caso SQLite ainda segure o lock na memória
                GC.Collect();
                GC.WaitForPendingFinalizers();
                try { File.Delete(tempDb); } catch { }
            }
        }

        return games;
    }

    private static Dictionary<string, int> LoadGameTimes(SqliteConnection conn)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = new SqliteCommand("SELECT releaseKey, minutesInGame FROM GameTimes", conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // Cada linha é isolada agora: se UM jogo tiver minutesInGame nulo/estranho,
                // só aquele jogo fica de fora - antes, isso derrubava o resto da leitura
                // inteira (todo mundo depois dessa linha no resultado virava "nunca jogado").
                try
                {
                    var key = r.GetString(0);
                    if (r.IsDBNull(1)) continue;
                    result[key] = r.GetInt32(1);
                }
                catch { /* linha com formato inesperado - pula só essa */ }
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, DateTime> LoadLastPlayed(SqliteConnection conn)
    {
        var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // lastSessionDate is stored as Unix timestamp (seconds)
            using var cmd = new SqliteCommand("SELECT releaseKey, lastSessionDate FROM GameTimes WHERE lastSessionDate IS NOT NULL AND lastSessionDate > 0", conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var key = r.GetString(0);
                    var ts = r.GetInt64(1);
                    result[key] = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
                }
                catch { /* linha com formato inesperado - pula só essa */ }
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, string> LoadSummaries(SqliteConnection conn)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var query = @"
                SELECT gp.releaseKey, gp.value 
                FROM GamePieces gp 
                JOIN GamePieceTypes gpt ON gp.gamePieceTypeId = gpt.id 
                WHERE gpt.type = 'summary'";

            using var cmd = new SqliteCommand(query, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var json = r.GetString(1);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("summary", out var prop))
                    {
                        var text = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            result[key] = text;
                    }
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static Dictionary<string, (List<string> Genres, string? Developer, string? Publisher, double? CriticsScore)> LoadMetaData(SqliteConnection conn)
    {
        var result = new Dictionary<string, (List<string> Genres, string? Developer, string? Publisher, double? CriticsScore)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var query = @"
                SELECT gp.releaseKey, gp.value 
                FROM GamePieces gp 
                JOIN GamePieceTypes gpt ON gp.gamePieceTypeId = gpt.id 
                WHERE gpt.type = 'meta'";

            using var cmd = new SqliteCommand(query, conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var json = r.GetString(1);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var genres = new List<string>();
                    if (root.TryGetProperty("genres", out var gProp) && gProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var g in gProp.EnumerateArray())
                        {
                            var gStr = g.GetString();
                            if (!string.IsNullOrWhiteSpace(gStr)) genres.Add(gStr);
                        }
                    }

                    if (root.TryGetProperty("themes", out var tProp) && tProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tProp.EnumerateArray())
                        {
                            var tStr = t.GetString();
                            if (!string.IsNullOrWhiteSpace(tStr) && !genres.Contains(tStr)) genres.Add(tStr);
                        }
                    }

                    string? developer = null;
                    if (root.TryGetProperty("developers", out var devProp) && devProp.ValueKind == JsonValueKind.Array && devProp.GetArrayLength() > 0)
                        developer = devProp[0].GetString();

                    string? publisher = null;
                    if (root.TryGetProperty("publishers", out var pubProp) && pubProp.ValueKind == JsonValueKind.Array && pubProp.GetArrayLength() > 0)
                        publisher = pubProp[0].GetString();

                    double? score = null;
                    if (root.TryGetProperty("criticsScore", out var scoreProp) && scoreProp.TryGetDouble(out var dScore))
                        score = dScore;

                    result[key] = (genres, developer, publisher, score);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static (Platform? platform, string appId) ParseReleaseKey(string releaseKey)
    {
        if (releaseKey.StartsWith("steam_", StringComparison.OrdinalIgnoreCase))
            return (Platform.Steam, releaseKey["steam_".Length..]);

        if (releaseKey.StartsWith("epic_", StringComparison.OrdinalIgnoreCase))
            return (Platform.Epic, releaseKey["epic_".Length..]);

        if (releaseKey.StartsWith("gog_", StringComparison.OrdinalIgnoreCase))
            return (Platform.Gog, releaseKey["gog_".Length..]);

        if (releaseKey.StartsWith("origin_", StringComparison.OrdinalIgnoreCase) ||
            releaseKey.StartsWith("ea_", StringComparison.OrdinalIgnoreCase))
            return (Platform.EaApp, releaseKey);

        if (releaseKey.StartsWith("uplay_", StringComparison.OrdinalIgnoreCase) ||
            releaseKey.StartsWith("ubisoft_", StringComparison.OrdinalIgnoreCase))
            return (Platform.Ubisoft, releaseKey);

        return (null, releaseKey);
    }

    private static string? ExtractTitle(string jsonValue)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonValue);
            foreach (var name in new[] { "title", "originalTitle" })
            {
                if (doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var s = p.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        catch { }
        return null;
    }

    private static string CleanTitle(string rawTitle)
    {
        var t = rawTitle.Replace("™", "")
                        .Replace("®", "")
                        .Replace("(TM)", "")
                        .Replace("(R)", "")
                        .Trim();
        return Regex.Replace(t, @"\s*\(Trial\)\s*$", "", RegexOptions.IgnoreCase).Trim();
    }
}
