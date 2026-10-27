using System.IO;
using System.Text.Json;
using GameHub.Models;
using GameHub.Services;

namespace GameHub.Scanners;

public class EpicScanner : IGameScanner
{
    public Platform Platform => Platform.Epic;

    // Com o Epic Games Launcher instalado, os manifestos ficam aqui, não importa em qual drive
    // os jogos estão - cada .item já traz o InstallLocation completo.
    private static readonly string ManifestsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "Epic", "EpicGamesLauncher", "Data", "Manifests");

    // A Epic grava uma pasta escondida ".egstore" dentro de cada jogo que instala. Ela continua lá mesmo se
    // o launcher for removido ou mudar de lugar, então serve de segunda fonte.
    private const string EgstoreFolder = ".egstore";

    // Pastas que nunca têm jogo da Epic (e que seria lento ou inútil varrer)
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Users", "AppData",
        "Recovery", "PerfLogs", "Config.Msi", "$WinREAgent", "Documents and Settings", "WindowsApps",
        "node_modules", ".git", "Program Files", "Program Files (x86)"
    };

    private const int MaxDepth = 3; // D:\Games\Epic\Jogo = profundidade 2; C:\Program Files\Epic Games\Jogo é verificado à parte

    private static readonly string[] NotAGameExe =
    {
        "crash", "unins", "setup", "installer", "helper", "redist", "easyanticheat", "eac", "benchmark", "report", "launcher_"
    };

    private readonly List<string> _log = new();

    public List<GameEntry> Scan()
    {
        _log.Clear();
        var games = new List<GameEntry>();
        var knownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Manifestos do Epic Games Launcher (fonte principal quando o launcher existe)
        bool launcherPresent = ScanManifests(games, knownFolders, knownNames);

        // 2. Pastas ".egstore" nos discos (jogos que o launcher não lista, ou launcher ausente)
        ScanEgstoreFolders(games, knownFolders, knownNames, launcherPresent);

        WriteLog(games, launcherPresent);
        return games;
    }

    private bool ScanManifests(List<GameEntry> games, HashSet<string> knownFolders, HashSet<string> knownNames)
    {
        bool launcherPresent = Directory.Exists(ManifestsDir);
        if (!launcherPresent)
        {
            _log.Add("Pasta de manifestos do Epic Games Launcher não existe: " + ManifestsDir);
            return false;
        }

        foreach (var file in Directory.GetFiles(ManifestsDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var installLocation = root.TryGetProperty("InstallLocation", out var i) ? i.GetString() : null;
                var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appName))
                    continue;

                games.Add(new GameEntry
                {
                    Name = name,
                    Platform = Platform.Epic,
                    Platforms = new List<Platform> { Platform.Epic },
                    InstalledPlatforms = new List<Platform> { Platform.Epic },
                    AppId = appName,
                    InstallPath = installLocation ?? "",
                    LaunchUri = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true"
                });

                if (!string.IsNullOrWhiteSpace(installLocation)) knownFolders.Add(NormalizeDir(installLocation));
                knownNames.Add(name);
                _log.Add($"[manifesto] {name} | {installLocation}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"EpicScanner falhou ao ler o manifesto {file}", ex);
            }
        }

        return true;
    }

    private void ScanEgstoreFolders(List<GameEntry> games, HashSet<string> knownFolders, HashSet<string> knownNames, bool launcherPresent)
    {
        List<string> gameDirs;
        try { gameDirs = FindEgstoreGameDirs(); }
        catch (Exception ex)
        {
            Logger.LogError("EpicScanner falhou ao procurar pastas .egstore", ex);
            return;
        }

        foreach (var dir in gameDirs)
        {
            try
            {
                if (knownFolders.Contains(NormalizeDir(dir))) continue; // o manifesto já trouxe este jogo

                var folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folderName) || knownNames.Contains(folderName)) continue;

                var exe = FindGameExecutable(dir, folderName);
                if (exe == null)
                {
                    _log.Add($"[.egstore] IGNORADO, sem executável do jogo: {dir}");
                    continue;
                }

                var appName = ReadAppName(dir);

                var entry = new GameEntry
                {
                    Name = folderName,
                    Platform = Platform.Epic,
                    Platforms = new List<Platform> { Platform.Epic },
                    InstalledPlatforms = new List<Platform> { Platform.Epic },
                    AppId = appName ?? folderName,
                    InstallPath = dir,
                    LaunchExecutable = exe
                };

                // Só abre pela Epic se o launcher existe; sem ele o jogo é aberto direto pelo executável
                if (launcherPresent && !string.IsNullOrWhiteSpace(appName))
                    entry.LaunchUri = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true";

                games.Add(entry);
                knownFolders.Add(NormalizeDir(dir));
                knownNames.Add(folderName);
                _log.Add($"[.egstore] {folderName} | {dir} | exe: {Path.GetFileName(exe)} | abre por: {(entry.LaunchUri != null ? "launcher" : "executável")}");
            }
            catch (Exception ex)
            {
                Logger.LogError($"EpicScanner falhou ao ler a pasta {dir}", ex);
            }
        }
    }

    /// <summary>Procura, nos discos fixos, pastas de jogo que contêm ".egstore" (sem descer dentro de jogos nem de pastas de sistema).</summary>
    private List<string> FindEgstoreGameDirs()
    {
        var found = new List<string>();

        // Hidden e ReparsePoint fora: evita pastas de sistema, links e arquivos do OneDrive só na nuvem
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                var root = drive.RootDirectory.FullName;
                var level = new List<string> { root };

                for (int depth = 1; depth <= MaxDepth && level.Count > 0; depth++)
                {
                    var next = new List<string>();
                    foreach (var parent in level)
                    {
                        List<string> children;
                        try { children = Directory.EnumerateDirectories(parent, "*", options).ToList(); }
                        catch { continue; }

                        foreach (var child in children)
                        {
                            var name = Path.GetFileName(child);
                            if (SkipDirs.Contains(name)) continue;

                            if (Directory.Exists(Path.Combine(child, EgstoreFolder)))
                                found.Add(child);          // é um jogo: não desce dentro dele
                            else
                                next.Add(child);
                        }
                    }
                    level = next;
                }

                // Pasta padrão da Epic no Program Files (que foi pulada acima por ser enorme)
                foreach (var pf in new[] { "Program Files", "Program Files (x86)" })
                {
                    var epicDir = Path.Combine(root, pf, "Epic Games");
                    if (!Directory.Exists(epicDir)) continue;

                    foreach (var child in Directory.EnumerateDirectories(epicDir, "*", options))
                        if (Directory.Exists(Path.Combine(child, EgstoreFolder)))
                            found.Add(child);
                }
            }
            catch { /* disco indisponível: segue para o próximo */ }
        }

        return found;
    }

    /// <summary>O .mancpn de dentro de .egstore guarda o AppName do jogo na Epic (JSON).</summary>
    private static string? ReadAppName(string gameDir)
    {
        try
        {
            var egstore = Path.Combine(gameDir, EgstoreFolder);
            foreach (var file in Directory.GetFiles(egstore, "*.mancpn"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("AppName", out var a))
                {
                    var value = a.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
            }
        }
        catch { /* sem AppName: abre pelo executável */ }
        return null;
    }

    private static string? FindGameExecutable(string dir, string folderName)
    {
        try
        {
            var exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);

            var exact = exes.FirstOrDefault(e =>
                Path.GetFileNameWithoutExtension(e).Equals(folderName, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            return exes
                .Where(e => !NotAGameExe.Any(bad => Path.GetFileName(e).Contains(bad, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(e => new FileInfo(e).Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeDir(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    private void WriteLog(List<GameEntry> games, bool launcherPresent)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");
            Directory.CreateDirectory(dir);

            var lines = new List<string>
            {
                $"Varredura Epic em {DateTime.Now:dd/MM/yyyy HH:mm:ss} - {games.Count} jogo(s) instalado(s) | launcher da Epic encontrado: {(launcherPresent ? "sim" : "não")}"
            };
            lines.AddRange(_log);
            File.WriteAllLines(Path.Combine(dir, "epic_scan_log.txt"), lines);
        }
        catch { /* log é só diagnóstico */ }
    }
}
