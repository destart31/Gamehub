using System.IO;
using System.Text;
using Microsoft.Win32;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Tempo jogado e última sessão dos jogos da Steam, lidos do arquivo local da própria Steam
/// (userdata\&lt;conta&gt;\config\localconfig.vdf). Vale para jogos instalados e para os
/// "adquiridos" que só vieram do GOG Galaxy, e não depende de chave de API.
///
/// O que foi aplicado (e o que faltou) fica em %AppData%\GameHub\steam_stats_log.txt.
/// </summary>
public static class SteamLocalStatsService
{
    private record AppStats(int Minutes, DateTime? LastPlayed);

    public static void Apply(IEnumerable<GameEntry> games)
    {
        var log = new List<string>();

        try
        {
            var stats = LoadStats(log);

            if (stats.Count > 0)
            {
                int steamEntries = 0, updated = 0;

                foreach (var game in games)
                {
                    if (game.Platform != Platform.Steam || string.IsNullOrEmpty(game.AppId)) continue;
                    steamEntries++;

                    if (!stats.TryGetValue(game.AppId, out var s)) continue;

                    bool changed = false;

                    // Tempo só cresce: vale o maior entre o que já havia (ex.: Galaxy, às vezes defasado) e o da Steam
                    int current = game.PlaytimeByPlatform.TryGetValue(Platform.Steam, out var cur) ? cur : 0;
                    int best = Math.Max(current, s.Minutes);
                    if (best > 0 && best != current)
                    {
                        game.PlaytimeByPlatform[Platform.Steam] = best;
                        changed = true;
                    }
                    if (best > game.PlaytimeMinutes) game.PlaytimeMinutes = best;

                    if (s.LastPlayed.HasValue)
                    {
                        var lastPlayed = s.LastPlayed.Value;
                        var currentLast = game.LastPlayedByPlatform.TryGetValue(Platform.Steam, out var d) ? d : DateTime.MinValue;
                        if (lastPlayed > currentLast)
                        {
                            game.LastPlayedByPlatform[Platform.Steam] = lastPlayed;
                            changed = true;
                        }
                        if (game.LastPlayed == null || lastPlayed > game.LastPlayed.Value)
                            game.LastPlayed = lastPlayed;
                    }

                    if (changed) updated++;
                }

                log.Add($"Jogos da Steam na biblioteca: {steamEntries} | atualizados com dados locais: {updated}");
            }
        }
        catch (Exception ex)
        {
            log.Add("ERRO: " + ex.Message);
            Logger.LogError("SteamLocalStatsService", ex);
        }

        WriteLog(log);
    }

    private static Dictionary<string, AppStats> LoadStats(List<string> log)
    {
        var result = new Dictionary<string, AppStats>(StringComparer.OrdinalIgnoreCase);

        var steamPath = GetSteamPath();
        if (steamPath == null)
        {
            log.Add("Pasta da Steam não encontrada no registro.");
            return result;
        }

        var userdata = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userdata))
        {
            log.Add("Pasta userdata não existe: " + userdata);
            return result;
        }

        // Com mais de uma conta na mesma Steam, usa a que gravou o arquivo por último
        var file = Directory.GetDirectories(userdata)
            .Select(d => Path.Combine(d, "config", "localconfig.vdf"))
            .Where(File.Exists)
            .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();

        if (file == null)
        {
            log.Add("Nenhum localconfig.vdf encontrado em " + userdata);
            return result;
        }

        log.Add($"localconfig.vdf lido (gravado pela Steam em {File.GetLastWriteTime(file):dd/MM/yyyy HH:mm})");

        string text;
        // A Steam pode estar aberta com o arquivo: abre permitindo leitura compartilhada
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(fs, Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        var root = VdfParser.Parse(text);
        var apps = Dive(root, "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");
        if (apps == null)
        {
            log.Add("Seção 'apps' não encontrada no arquivo.");
            return result;
        }

        int withTime = 0, withDate = 0;

        foreach (var pair in apps)
        {
            if (pair.Value is not Dictionary<string, object> app) continue;

            int minutes = 0;
            if (app.GetValueOrDefault("Playtime") is string pt && int.TryParse(pt, out var m) && m > 0)
                minutes = m;

            DateTime? last = null;
            if (app.GetValueOrDefault("LastPlayed") is string lp && long.TryParse(lp, out var ts) && ts > 0)
                last = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;

            if (minutes <= 0 && last == null) continue;

            if (minutes > 0) withTime++;
            if (last != null) withDate++;
            result[pair.Key] = new AppStats(minutes, last);
        }

        log.Add($"Jogos no arquivo da Steam: {result.Count} | com tempo jogado: {withTime} | com data de última sessão: {withDate}");
        return result;
    }

    private static Dictionary<string, object>? Dive(Dictionary<string, object> node, params string[] path)
    {
        var current = node;
        foreach (var key in path)
        {
            if (current.TryGetValue(key, out var next) && next is Dictionary<string, object> dict)
                current = dict;
            else
                return null;
        }
        return current;
    }

    private static string? GetSteamPath()
    {
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam"))
        {
            if (key?.GetValue("InstallPath") is string installPath && Directory.Exists(installPath))
                return installPath;
        }

        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
        {
            // O registro do usuário guarda o caminho com "/" em vez de "\"
            if (key?.GetValue("SteamPath") is string steamPath)
            {
                var normalized = steamPath.Replace('/', Path.DirectorySeparatorChar);
                if (Directory.Exists(normalized)) return normalized;
            }
        }

        return null;
    }

    private static void WriteLog(List<string> lines)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
            Directory.CreateDirectory(dir);

            var all = new List<string> { $"Leitura de dados locais da Steam em {DateTime.Now:dd/MM/yyyy HH:mm:ss}" };
            all.AddRange(lines);
            File.WriteAllLines(Path.Combine(dir, "steam_stats_log.txt"), all);
        }
        catch { /* o log é só diagnóstico: nunca pode quebrar o carregamento da biblioteca */ }
    }
}
