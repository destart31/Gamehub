using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Mede o tempo jogado dos jogos de emulador e dos jogos custom/modificados abertos pelo GameHub.
/// Esses jogos não têm launcher que guarde o tempo, então o GameHub observa o jogo rodando: depois de
/// abrir, acompanha os processos que rodam da pasta do jogo e soma o tempo até todos fecharem.
///
/// Jogos de loja ficam de fora de propósito (cada loja tem o seu valor). Dados em %AppData%\GameHub\play_sessions.json;
/// diagnóstico em %AppData%\GameHub\playtime_log.txt.
/// </summary>
public static class PlaySessionService
{
    private class Entry
    {
        public Platform Platform { get; set; }
        public long Seconds { get; set; }
        public DateTime LastPlayed { get; set; }
    }

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "play_sessions.json");

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Data = Load();
    private static readonly HashSet<string> Tracking = new();

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaitForStart = TimeSpan.FromMinutes(4);  // atualização/launcher antes do jogo abrir
    private static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(45);     // jogos que reabrem por um launcher próprio

    /// <summary>Só jogos de emulador (rom|...) e custom/modificados (custom|...) têm o tempo medido pelo GameHub.</summary>
    private static bool IsMeasured(string userKey) =>
        userKey.StartsWith("rom|", StringComparison.OrdinalIgnoreCase) ||
        userKey.StartsWith("custom|", StringComparison.OrdinalIgnoreCase);

    // ── Acompanhamento ──────────────────────────────────────────

    /// <summary>Começa a acompanhar o jogo recém aberto. <paramref name="onChanged"/> roda quando a sessão termina.</summary>
    public static void Start(GameEntry game, Action? onChanged = null)
    {
        if (!IsMeasured(game.UserKey)) return; // lojas informam o próprio tempo; aqui só emuladores e jogos custom

        var roots = GetRoots(game, out var exe);
        if (roots.Count == 0 && exe == null) return; // não há como saber onde o jogo roda

        var key = game.UserKey;
        var platform = game.Platform;

        lock (Gate)
        {
            if (!Tracking.Add(key)) return; // já está sendo acompanhado
        }

        _ = Task.Run(async () =>
        {
            try { await TrackAsync(key, platform, roots, exe, onChanged); }
            catch (Exception ex) { Logger.LogError("PlaySessionService", ex); }
            finally { lock (Gate) Tracking.Remove(key); }
        });
    }

    private static async Task TrackAsync(string key, Platform platform, List<string> roots, string? exe, Action? onChanged)
    {
        var begin = DateTime.UtcNow;
        DateTime? lastSeen = null;
        var prevTick = DateTime.UtcNow;
        double pending = 0;                 // segundos ainda não gravados
        var lastSave = DateTime.UtcNow;
        bool recorded = false;

        while (true)
        {
            await Task.Delay(PollInterval);

            var now = DateTime.UtcNow;
            bool running = IsAnyRunning(roots, exe);

            if (running)
            {
                if (lastSeen != null) pending += (now - prevTick).TotalSeconds;
                lastSeen = now;
            }
            else if (lastSeen == null)
            {
                if (now - begin > WaitForStart) return; // o jogo nunca abriu
            }
            else if (now - lastSeen.Value > EndGrace)
            {
                break; // jogo fechou
            }

            prevTick = now;

            // Grava de minuto em minuto: se o GameHub fechar no meio da sessão, quase nada se perde
            if (lastSeen != null && now - lastSave > TimeSpan.FromMinutes(1))
            {
                Commit(key, platform, pending);
                pending = 0;
                lastSave = now;
                recorded = true;
            }
        }

        Commit(key, platform, pending);
        recorded = true;

        if (recorded) onChanged?.Invoke();
    }

    private static void Commit(string key, Platform platform, double seconds)
    {
        lock (Gate)
        {
            if (!Data.TryGetValue(key, out var e))
            {
                e = new Entry { Platform = platform };
                Data[key] = e;
            }
            e.Platform = platform;
            e.Seconds += (long)Math.Round(seconds);
            e.LastPlayed = DateTime.Now;
            Save();
        }
    }

    // ── Aplicação na biblioteca ─────────────────────────────────

    /// <summary>Aplica o tempo medido na lista de jogos e escreve o log de quem ainda está sem dados.</summary>
    public static void Apply(IEnumerable<GameEntry> games)
    {
        var list = games.ToList();
        var log = new List<string>();
        int applied = 0;

        lock (Gate)
        {
            foreach (var game in list)
            {
                if (!IsMeasured(game.UserKey) || !Data.TryGetValue(game.UserKey, out var e)) continue;

                int minutes = e.Seconds > 0 ? (int)((e.Seconds + 59) / 60) : 0;

                // Vale o maior entre o que a loja/Galaxy já informava e o medido aqui (evita contar duas vezes
                // o mesmo jogo quando o Galaxy também o viu rodando)
                if (minutes > 0)
                {
                    int current = game.PlaytimeByPlatform.TryGetValue(e.Platform, out var c) ? c : 0;
                    if (minutes > current) game.PlaytimeByPlatform[e.Platform] = minutes;
                    if (minutes > game.PlaytimeMinutes) game.PlaytimeMinutes = minutes;
                }

                var currentLast = game.LastPlayedByPlatform.TryGetValue(e.Platform, out var d) ? d : DateTime.MinValue;
                if (e.LastPlayed > currentLast) game.LastPlayedByPlatform[e.Platform] = e.LastPlayed;
                if (game.LastPlayed == null || e.LastPlayed > game.LastPlayed.Value) game.LastPlayed = e.LastPlayed;

                applied++;
            }
        }

        // Diagnóstico: quem é de outra loja e continua sem tempo e/ou última sessão (só informativo)
        int total = 0, noTime = 0, noDate = 0;
        foreach (var game in list)
        {
            if (game.Platforms.Count == 1 && game.Platforms[0] == Platform.Steam) continue;
            total++;

            bool hasTime = game.PlaytimeByPlatform.Values.Any(v => v > 0) || game.PlaytimeMinutes > 0;
            bool hasDate = game.LastPlayed != null || game.LastPlayedByPlatform.Count > 0;
            if (!hasTime) noTime++;
            if (!hasDate) noDate++;

            if (!hasTime || !hasDate)
            {
                var plats = string.Join("+", game.Platforms);
                var installed = game.InstalledPlatforms.Count > 0 ? "instalado" : "só na biblioteca";
                log.Add($"  sem {(hasTime ? "" : "tempo ")}{(hasDate ? "" : "data ")}| {game.Name} | {plats} | {installed}");
            }
        }

        log.Insert(0, $"Jogos fora da Steam: {total} | sem tempo: {noTime} | sem última sessão: {noDate} | com dado medido pelo GameHub: {applied}");
        WriteLog(log);
    }

    // ── Descoberta dos processos do jogo ────────────────────────

    private static List<string> GetRoots(GameEntry game, out string? exe)
    {
        var roots = new List<string>();
        exe = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchExecutable) && File.Exists(game.LaunchExecutable))
            {
                exe = Path.GetFullPath(game.LaunchExecutable);
                AddRoot(roots, Path.GetDirectoryName(exe));
            }

            if (!string.IsNullOrWhiteSpace(game.InstallPath) && Directory.Exists(game.InstallPath))
                AddRoot(roots, Path.GetFullPath(game.InstallPath));
        }
        catch { /* caminho inválido: segue só com o que deu certo */ }

        return roots;
    }

    private static void AddRoot(List<string> roots, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;

        var full = Path.GetFullPath(dir).TrimEnd('\\', '/');

        // Pasta rasa demais (C:\ ou C:\Games) pegaria programas de outros jogos
        var depth = full.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 3) return;

        var withSep = full + Path.DirectorySeparatorChar;
        if (!roots.Contains(withSep, StringComparer.OrdinalIgnoreCase)) roots.Add(withSep);
    }

    private static bool IsAnyRunning(List<string> roots, string? exe)
    {
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch { return false; }

        try
        {
            foreach (var p in processes)
            {
                string? path;
                try { path = GetImagePath(p.Id); }
                catch { continue; }
                if (path == null) continue;

                if (exe != null && path.Equals(exe, StringComparison.OrdinalIgnoreCase)) return true;

                foreach (var root in roots)
                    if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }

        return false;
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder text, ref int size);

    /// <summary>Caminho do .exe do processo sem lançar exceção (Process.MainModule falha em processos do sistema).</summary>
    private static string? GetImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ── Arquivos ────────────────────────────────────────────────

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new Dictionary<string, Entry>();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? new Dictionary<string, Entry>();
        }
        catch
        {
            return new Dictionary<string, Entry>();
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch { /* sem permissão ou disco cheio: tenta de novo no próximo minuto */ }
    }

    private static void WriteLog(List<string> lines)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var all = new List<string> { $"Tempo de jogo fora da Steam em {DateTime.Now:dd/MM/yyyy HH:mm:ss}" };
            all.AddRange(lines);
            File.WriteAllLines(Path.Combine(ConfigDir, "playtime_log.txt"), all);
        }
        catch { /* log é só diagnóstico */ }
    }
}
