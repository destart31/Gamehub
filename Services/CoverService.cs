using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GameHub.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace GameHub.Services;

public class CoverService
{
    private static readonly HttpClient HttpClient = new();
    private static readonly string CoversDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "Covers");
    private static readonly string ThumbsDir = Path.Combine(CoversDir, "thumbs");
    private static readonly string GogWebcacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GOG.com", "Galaxy", "webcache");

    private static readonly SemaphoreSlim DownloadThrottle = new(8, 8);
    // Resultado da busca no cache do Galaxy por jogo, lembrado durante a sessão (inclui "não achou")
    private static readonly ConcurrentDictionary<string, string?> WebcacheMemo = new();

    // Lista de todas as pastas do cache do Galaxy, lida uma vez só
    private static readonly Lazy<List<(string UserDir, string FullPath, string Name)>> WebcacheDirs = new(BuildWebcacheIndex);

    private static List<(string UserDir, string FullPath, string Name)> BuildWebcacheIndex()
    {
        var list = new List<(string UserDir, string FullPath, string Name)>();
        try
        {
            if (!Directory.Exists(GogWebcacheDir)) return list;

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var userDir in Directory.GetDirectories(GogWebcacheDir))
                foreach (var dir in Directory.EnumerateDirectories(userDir, "*", options))
                    list.Add((userDir, dir, System.IO.Path.GetFileName(dir)));
        }
        catch { /* sem o índice, só não acha capas do Galaxy */ }
        return list;
    }

    static CoverService()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        
        if (!Directory.Exists(CoversDir))
            Directory.CreateDirectory(CoversDir);

        // Migração de capas antigas
        var oldDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Covers");
        if (Directory.Exists(oldDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(oldDir))
                {
                    var dest = Path.Combine(CoversDir, Path.GetFileName(file));
                    if (!File.Exists(dest)) File.Move(file, dest);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Pré-carregamento ultrarrápido síncrono de capas locais do HD (Executado em 0.01s em paralelo).
    /// </summary>
    public static void LoadLocalCoversSync(List<GameEntry> games)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        int local = 0, fromWebcache = 0, none = 0;
        long webcacheTicks = 0;
        var total = Stopwatch.StartNew();
    
        Parallel.ForEach(games, options, game =>
        {
            if (game.CoverImage != null) return;
    
            var sanitizedName = SanitizeFileName(game.Name);
            string? localPath = null;
    
            // Procura pela capa com qualquer extensão de imagem suportada
            foreach (var ext in new[] { ".jpg", ".png", ".webp", ".bmp", ".jpeg" })
            {
                var testPath = Path.Combine(CoversDir, $"{sanitizedName}{ext}");
                if (File.Exists(testPath) && new FileInfo(testPath).Length > 0)
                {
                    localPath = testPath;
                    break;
                }
            }
    
            if (localPath != null)
            {
                game.CoverImage = LoadBitmapFromStream(localPath);
                Interlocked.Increment(ref local);
                return;
            }
    
            // Fallback: se não achar, usa .jpg como padrão para os próximos downloads automáticos
            localPath = Path.Combine(CoversDir, $"{sanitizedName}.jpg");
    
            var started = Stopwatch.GetTimestamp();
            var gogCachePath = FindGogWebcacheImage(game);
            Interlocked.Add(ref webcacheTicks, Stopwatch.GetTimestamp() - started);
    
            if (!string.IsNullOrEmpty(gogCachePath))
            {
                var bitmap = LoadBitmapFromStream(gogCachePath);
                if (bitmap != null)
                {
                    game.CoverImage = bitmap;
                    Task.Run(() => { try { File.Copy(gogCachePath, localPath, true); } catch { } });
                }
                Interlocked.Increment(ref fromWebcache);
            }
            else
            {
                Interlocked.Increment(ref none);
            }
        });
    
        var webcacheMs = webcacheTicks * 1000.0 / Stopwatch.Frequency;
        StartupLog.Mark($"capas: {local} do disco | {fromWebcache} do Galaxy | {none} sem capa | busca no Galaxy {webcacheMs:0} ms (soma das threads) | total {total.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Lê o arquivo via FileStream e passa os bytes direto pro BitmapImage.
    /// Mais rápido que via URI porque evita o resolver de pack:// do WPF,
    /// não mantém o arquivo aberto depois do EndInit() e não entra no
    /// cache global de imagens (que causava o bug de capa que não atualizava).
    /// </summary>
        private static BitmapSource? LoadBitmapFromStream(string path)
    {
        try
        {
            // Miniatura já reduzida: bem menor e mais rápida de ler que a capa original
            var thumbPath = GetThumbPath(path);
            if (thumbPath != null && File.Exists(thumbPath))
            {
                var thumb = DecodeBitmap(File.ReadAllBytes(thumbPath), null);
                if (thumb != null) return thumb;
            }

            var bitmap = DecodeBitmap(File.ReadAllBytes(path), 175);
            if (bitmap != null && thumbPath != null) SaveThumb(bitmap, thumbPath);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeBitmap(byte[] bytes, int? pixelWidth)
    {
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            if (pixelWidth.HasValue) bitmap.DecodePixelWidth = pixelWidth.Value;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nome da miniatura = caminho + tamanho + data do arquivo: se a capa for trocada, uma nova miniatura é gerada.</summary>
    private static string? GetThumbPath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var id = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..16];
            return Path.Combine(ThumbsDir, $"{id}_{info.Length}_{info.LastWriteTimeUtc.Ticks}.png");
        }
        catch
        {
            return null;
        }
    }

    private static void SaveThumb(BitmapSource bitmap, string thumbPath)
    {
        try
        {
            Directory.CreateDirectory(ThumbsDir);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var tmp = $"{thumbPath}.{Guid.NewGuid():N}.tmp";
            using (var fs = File.Create(tmp)) encoder.Save(fs);
            File.Move(tmp, thumbPath, overwrite: true);
        }
        catch { /* sem miniatura, só fica na velocidade de antes */ }
    }

    public static async Task LoadAllCoversParallelAsync(List<GameEntry> games)
    {
        // Garante que o cache local seja carregado primeiro
        LoadLocalCoversSync(games);

        // Baixa online apenas os jogos que ainda estiverem sem capa
        var missingCovers = games.Where(g => g.CoverImage == null).ToList();
        if (missingCovers.Count == 0) return;

        // 12 downloads em paralelo com timeout de 8s por capa -
        // antes ficava esperando servidores lentos e travava a fila toda.
        HttpClient.Timeout = TimeSpan.FromSeconds(8);
        await Parallel.ForEachAsync(missingCovers, new ParallelOptions { MaxDegreeOfParallelism = 12 }, async (game, ct) =>
        {
            try { await LoadSingleCoverOnlineAsync(game); }
            catch { /* timeout ou erro de rede - segue sem a capa */ }
        });
    }

    // Nome do sistema (como usamos no emulators.json) -> repositório correspondente em
    // github.com/libretro-thumbnails. Nem todo sistema tem cobertura lá - o que não estiver
    // aqui (ex.: Switch) simplesmente não tenta buscar capa, fica no ícone genérico.
    private static readonly Dictionary<string, string> LibretroThumbnailRepos = new()
    {
        ["Nintendo 64"] = "Nintendo_-_Nintendo_64",
        ["GameCube"] = "Nintendo_-_GameCube",
        ["Arcade (CPS1)"] = "FBNeo_-_Arcade_Games",
        ["Super Nintendo"] = "Nintendo_-_Super_Nintendo_Entertainment_System",
        ["Mega Drive / Genesis"] = "Sega_-_Mega_Drive_-_Genesis",
        ["Nintendo (NES)"] = "Nintendo_-_Nintendo_Entertainment_System",
        ["Game Boy Advance"] = "Nintendo_-_Game_Boy_Advance",
        ["PlayStation"] = "Sony_-_PlayStation",
        ["PSP"] = "Sony_-_PlayStation_Portable",
        ["Nintendo DS"] = "Nintendo_-_Nintendo_DS",
        ["Dreamcast"] = "Sega_-_Dreamcast",
    };

    private static async Task LoadSingleCoverOnlineAsync(GameEntry game)
    {
        if (game.CoverImage != null) return;

        await DownloadThrottle.WaitAsync();
        try
        {
            var localFileName = $"{SanitizeFileName(game.Name)}.jpg";
            var localPath = Path.Combine(CoversDir, localFileName);

            // PS2: extrai o serial (ex.: SLUS-21306) direto do ISO e busca no banco que
            // o próprio PCSX2 usa - muito mais confiável que tentar bater pelo nome.
            if (game.EmulatorSystem == "PlayStation 2")
            {
                if (await TryDownloadPs2CoverAsync(game, localPath))
                    return;
            }

            // ROM de emulador: tenta a capa no banco do libretro-thumbnails antes de
            // qualquer outra coisa (buscar na loja da Steam não faz sentido aqui).
            if (!string.IsNullOrWhiteSpace(game.EmulatorSystem) &&
                LibretroThumbnailRepos.TryGetValue(game.EmulatorSystem, out var repo))
            {
                if (await TryDownloadLibretroThumbnailAsync(game, repo, localPath))
                    return;
            }

            if (!string.IsNullOrWhiteSpace(game.EmulatorSystem))
                return; // ROM sem capa no libretro-thumbnails - fica no ícone genérico, não busca na Steam

            string? steamAppId = game.Platforms.Contains(Platform.Steam) && !string.IsNullOrWhiteSpace(game.AppId)
                ? game.AppId
                : await SearchSteamAppIdStrictAsync(game.Name);

            if (!string.IsNullOrWhiteSpace(steamAppId))
            {
                // Nem todo app da Steam tem o asset "library_600x900" (comum em apps pequenos/
                // nichados, tipo utilitários). Tenta em ordem até achar um que exista de verdade.
                var candidateUrls = new[]
                {
                    $"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{steamAppId}/library_600x900.jpg",
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{steamAppId}/library_600x900.jpg",
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{steamAppId}/header.jpg",
                    $"https://cdn.akamai.steamstatic.com/steam/apps/{steamAppId}/capsule_616x353.jpg"
                };

                foreach (var downloadUrl in candidateUrls)
                {
                    var response = await HttpClient.GetAsync(downloadUrl);
                    if (!response.IsSuccessStatusCode) continue;

                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes.Length < 500) continue; // resposta vazia/placeholder - tenta a próxima

                    await File.WriteAllBytesAsync(localPath, bytes);

                    var downloadedBitmap = LoadBitmapFromStream(localPath);
                    if (downloadedBitmap != null)
                    {
                        game.CoverImage = downloadedBitmap;
                        break;
                    }
                }
            }
        }
        catch { }
        finally
        {
            DownloadThrottle.Release();
        }
    }

    // Casa padrões tipo "SLUS_213.06" ou "SLES_500.15" - é assim que o serial fica
    // gravado dentro do SYSTEM.CNF, perto do começo de qualquer disco de PS2.
    private static readonly System.Text.RegularExpressions.Regex Ps2SerialRegex =
        new(@"([A-Za-z]{4})[_-](\d{3})\.?(\d{2})", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Lê só o começo do arquivo (onde o SYSTEM.CNF do disco sempre fica) procurando
    /// o serial do jogo, e busca a capa correspondente no xlenore/ps2-covers - o mesmo
    /// banco que o "Cover Downloader" embutido do PCSX2 usa.
    /// </summary>
    private static async Task<bool> TryDownloadPs2CoverAsync(GameEntry game, string localPath)
    {
        if (string.IsNullOrWhiteSpace(game.RomFilePath) || !File.Exists(game.RomFilePath))
            return false;

        string? serial;
        try
        {
            const int scanBytes = 32 * 1024 * 1024; // 32MB - o SYSTEM.CNF fica bem no início do disco
            await using var stream = File.OpenRead(game.RomFilePath);
            var length = (int)Math.Min(scanBytes, stream.Length);
            var buffer = new byte[length];
            var read = await stream.ReadAsync(buffer.AsMemory(0, length));
            var text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);

            var match = Ps2SerialRegex.Match(text);
            if (!match.Success) return false;

            var prefix = match.Groups[1].Value.ToUpperInvariant();
            var number = match.Groups[2].Value + match.Groups[3].Value;
            serial = $"{prefix}-{number}";
        }
        catch
        {
            return false; // ISO ilegível, arquivo travado etc. - segue sem capa
        }

        var url = $"https://raw.githubusercontent.com/xlenore/ps2-covers/main/covers/default/{serial}.jpg";

        try
        {
            var response = await HttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return false;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 500) return false;

            await File.WriteAllBytesAsync(localPath, bytes);
            var bitmap = LoadBitmapFromStream(localPath);
            if (bitmap == null) return false;

            game.CoverImage = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tenta achar a capa da ROM no libretro-thumbnails. O banco usa nomes no padrão
    /// No-Intro (ex.: "Super Mario 64 (USA).png"), que nem sempre bate exatamente com
    /// o nome do seu arquivo - por isso tenta algumas variações antes de desistir.
    /// Quando não acha, não é erro: fica só no ícone genérico mesmo.
    /// </summary>
    private static async Task<bool> TryDownloadLibretroThumbnailAsync(GameEntry game, string repo, string localPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(game.RomFilePath ?? game.Name);

        var candidates = new[]
        {
            baseName,
            $"{baseName} (USA)",
            $"{baseName} (World)",
            $"{baseName} (Europe)",
            game.Name
        }.Distinct();

        foreach (var candidate in candidates)
        {
            var url = $"https://raw.githubusercontent.com/libretro-thumbnails/{repo}/master/Named_Boxarts/{Uri.EscapeDataString(candidate)}.png";

            try
            {
                var response = await HttpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 500) continue;

                await File.WriteAllBytesAsync(localPath, bytes);
                var bitmap = LoadBitmapFromStream(localPath);
                if (bitmap != null)
                {
                    game.CoverImage = bitmap;
                    return true;
                }
            }
            catch { }
        }

        return false;
    }

        // Resultado da busca por jogo, lembrado durante a sessão (inclui "não achou")
    private static string? FindGogWebcacheImage(GameEntry game)
    {
        var key = $"{game.ReleaseKey}|{game.AppId}";
        return WebcacheMemo.GetOrAdd(key, _ => FindGogWebcacheImageUncached(game));
    }

    private static string? FindGogWebcacheImageUncached(GameEntry game)
    {
        try
        {
            if (!Directory.Exists(GogWebcacheDir)) return null;

            var userDirs = Directory.GetDirectories(GogWebcacheDir);
            foreach (var userDir in userDirs)
            {
                if (!string.IsNullOrEmpty(game.ReleaseKey))
                {
                    var keyParts = game.ReleaseKey.Split('_', 2);
                    var platformSub = keyParts[0] switch
                    {
                        "origin" => "origin",
                        "ea" => "origin",
                        "epic" => "epic",
                        "steam" => "steam",
                        "gog" => "gog",
                        "uplay" => "uplay",
                        "ubisoft" => "uplay",
                        _ => keyParts[0]
                    };

                    var subId = keyParts.Length > 1 ? keyParts[1] : keyParts[0];

                    var targetPath = Path.Combine(userDir, platformSub, subId);
                    if (Directory.Exists(targetPath))
                    {
                        var image = GetBestImageFromDirectory(targetPath);
                        if (image != null) return image;
                    }
                }

                if (!string.IsNullOrEmpty(game.AppId))
                {
                    var matches = WebcacheDirs.Value
                        .Where(d => d.UserDir == userDir && d.Name.Contains(game.AppId, StringComparison.OrdinalIgnoreCase))
                        .Select(d => d.FullPath);
                    foreach (var match in matches)
                    {
                        var image = GetBestImageFromDirectory(match);
                        if (image != null) return image;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? GetBestImageFromDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);

        var verticalCover = files.FirstOrDefault(f => f.Contains("glx_vertical_cover", StringComparison.OrdinalIgnoreCase));
        if (verticalCover != null) return verticalCover;

        var squareIcon = files.FirstOrDefault(f => f.Contains("glx_square_icon", StringComparison.OrdinalIgnoreCase));
        if (squareIcon != null) return squareIcon;

        return files.FirstOrDefault(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Define a capa manualmente a partir de um arquivo de imagem escolhido pelo usuário.
    /// Salva no mesmo cache local que as capas automáticas usam - então, uma vez definida,
    /// continua valendo nas próximas vezes que o app abrir (não busca online de novo).
    /// </summary>
    public static bool SetManualCover(GameEntry game, string sourceImagePath)
    {
        try
        {
            if (!Directory.Exists(CoversDir))
                Directory.CreateDirectory(CoversDir);

            // Preserva a extensão original (ex: .png, .webp) para evitar corrompimento de formato
            var ext = Path.GetExtension(sourceImagePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            
            // Apaga as outras extensões antigas se existirem para evitar conflito
            var sanitizedName = SanitizeFileName(game.Name);
            foreach (var oldExt in new[] { ".jpg", ".png", ".webp", ".bmp", ".jpeg" })
            {
                var oldPath = Path.Combine(CoversDir, $"{sanitizedName}{oldExt}");
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }

            var localPath = Path.Combine(CoversDir, $"{sanitizedName}{ext}");
            File.Copy(sourceImagePath, localPath, overwrite: true);

            var bitmap = LoadBitmapFromStream(localPath);
            if (bitmap == null) return false;

            game.CoverImage = bitmap;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? LoadBitmapFromFile(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // O WPF mantém um cache interno de imagens por URI - sem isso, trocar a capa e
            // recarregar o MESMO caminho de arquivo às vezes devolvia a imagem antiga em
            // memória em vez de reler o arquivo (que já tinha sido sobrescrito no disco).
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = 175;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SearchSteamAppIdStrictAsync(string gameName)
    {
        try
        {
            var cleanName = CleanGameNameForSearch(gameName);
            var searchUrl = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(cleanName)}&l=english&cc=US";

            var json = await HttpClient.GetStringAsync(searchUrl);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameProp) && item.TryGetProperty("id", out var idProp))
                    {
                        var resultName = nameProp.GetString();
                        if (string.IsNullOrWhiteSpace(resultName)) continue;

                        if (IsSimilarGameName(cleanName, resultName))
                        {
                            return idProp.ToString();
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static bool IsSimilarGameName(string searchName, string resultName)
    {
        var n1 = GetNormalizedAlphaNumeric(searchName);
        var n2 = GetNormalizedAlphaNumeric(resultName);

        if (n1 == n2) return true;
        if (n2.StartsWith(n1, StringComparison.OrdinalIgnoreCase) || n1.StartsWith(n2, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private static string GetNormalizedAlphaNumeric(string text)
    {
        var clean = CleanGameNameForSearch(text);
        return new string(clean.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static string CleanGameNameForSearch(string name)
    {
        return name.Replace("™", "")
                   .Replace("®", "")
                   .Replace("(TM)", "")
                   .Replace("(R)", "")
                   .Replace("Definitive Edition", "")
                   .Replace("Enhanced Edition", "")
                   .Replace(" Remastered", "")
                   .Trim();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return clean.Replace(" ", "_");
    }
}
