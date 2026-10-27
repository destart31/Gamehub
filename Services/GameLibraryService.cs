using System.Linq;
using GameHub.Models;
using GameHub.Scanners;

namespace GameHub.Services;

public class GameLibraryService
{
    private readonly List<IGameScanner> _installedScanners = new()
    {
        new SteamScanner(),
        new EpicScanner(),
        new GogScanner(),
        new EaAppScanner(),
        new UbisoftScanner()
    };

    private readonly GogGalaxyDbScanner _acquiredScanner = new();
    private readonly EmulatorScanner _emulatorScanner = new();

    public List<GameEntry> LoadAllGames()
    {
        var rawGames = new List<GameEntry>();

        // 1. Escanear jogos instalados no sistema
        foreach (var scanner in _installedScanners)
        {
            try
            {
                var scanned = scanner.Scan();
                foreach (var game in scanned)
                {
                    if (ShouldExcludeGame(game)) continue;
                    // InstalledPlatforms já vem preenchido pelo scanner
                    if (!game.Platforms.Contains(game.Platform))
                        game.Platforms.Add(game.Platform);
                    rawGames.Add(game);
                }
            }
            catch
            {
                // Scanner falhou ou plataforma não instalada
            }
        }

        // 2. Escanear biblioteca de jogos adquiridos (via banco do GOG Galaxy)
        var acquiredGames = _acquiredScanner.ScanAcquiredGames();
        foreach (var acquired in acquiredGames)
        {
            if (ShouldExcludeGame(acquired)) continue;
            if (!acquired.Platforms.Contains(acquired.Platform))
                acquired.Platforms.Add(acquired.Platform);
            rawGames.Add(acquired);
        }

        SteamLocalStatsService.Apply(rawGames);

        // 2.5. Steam: o mesmo AppId é o mesmo jogo, mesmo que a Steam mude o nome
        // (ex.: "The Witcher 3: Wild Hunt" na conta x "... — Remastered" na pasta instalada).
        bool IsSteamWithId(GameEntry g) => g.Platform == Platform.Steam && !string.IsNullOrEmpty(g.AppId);

        var steamMerged = new List<GameEntry>();
        foreach (var grp in rawGames.Where(IsSteamWithId).GroupBy(g => g.AppId!))
        {
            var list = grp.ToList();
            // O nome "limpo" é o da entrada da conta (a que tem ReleaseKey)
            var cleanName = list.FirstOrDefault(g => !string.IsNullOrEmpty(g.ReleaseKey))?.Name;
            var merged = MergeGroupEntries(list);
            if (!string.IsNullOrWhiteSpace(cleanName)) merged.Name = cleanName;
            steamMerged.Add(merged);
        }
        rawGames = rawGames.Where(g => !IsSteamWithId(g)).Concat(steamMerged).ToList();
        // 3. Agrupamento e Fusão Inteligente por Título
        var groupedGames = rawGames
            .GroupBy(g => GetNormalizedTitle(g.Name), StringComparer.OrdinalIgnoreCase)
            .Select(group => MergeGroupEntries(group.ToList()))
            .OrderBy(g => g.Name)
            .ToList();

        // 4. Regras especiais (The Witcher 3 GOTY, Battlefield 4, etc.)
        ApplySpecialRules(groupedGames);

        // 4.5. ROMs de emulador entram DEPOIS da fusão por título - não fazem parte da
        // lógica de "mesmo jogo em lojas diferentes" (ex.: Sonic no Mega Drive não deve
        // se misturar com um Sonic qualquer comprado na Steam).
        try
        {
            groupedGames.AddRange(_emulatorScanner.Scan());
        }
        catch
        {
            // emulators.json ausente/mal formatado - segue sem a aba de emuladores
        }

        // 4.6. Jogos adicionados manualmente (aba "Jogos Modificados")
        try
        {
            groupedGames.AddRange(CustomGamesService.LoadAll());
        }
        catch
        {
            // custom_games.json ausente/mal formatado - segue sem eles
        }

        // 5. Capas carregadas em segundo plano (ver MainWindow.FetchCoversInBackground)
        // - removido daqui pra não bloquear o startup: o app aparece imediatamente
        //   e as capas preenchem enquanto o usuário já está navegando.

        return groupedGames;
    }

    private static bool ShouldExcludeGame(GameEntry game)
    {
        var name = game.Name ?? "";
        var appId = game.AppId ?? "";

        // Ocultar variações de "Amazon Prime" e "Prime Giveaway"
        if (name.Contains("Amazon Prime", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Prime Giveaway", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Prime-Giveaway", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Ocultar redistribuíveis e ferramentas Steam
        if (name.Equals("Steamworks Common Redistributables", StringComparison.OrdinalIgnoreCase) || appId == "228980")
            return true;

        // Ocultar Betas e Servidores dedicados
        if (name.Equals("KillingFloor2Beta", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Killing Floor 2 Beta", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Beta", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" Tech Beta", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
                // Itens de pré-venda do GOG Galaxy (ex.: "Ghostrunner pre-order KATANA DLC") não são jogos
        if (name.Contains("pre-order", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("preorder", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void ApplySpecialRules(List<GameEntry> games)
    {
        // Regra The Witcher 3: prefere a edição GOTY/Complete, mas SÓ se ela estiver instalada.
        // Antes, removia o base mesmo quando só ele estava instalado e o GOTY estava só na
        // biblioteca - resultado: o jogo sumia da lista de instalados.
        var witcherGoty = games.FirstOrDefault(g => g.Name.Contains("Witcher 3", StringComparison.OrdinalIgnoreCase) &&
                                                   (g.Name.Contains("Game of the Year", StringComparison.OrdinalIgnoreCase) ||
                                                    g.Name.Contains("GOTY", StringComparison.OrdinalIgnoreCase) ||
                                                    g.Name.Contains("Complete Edition", StringComparison.OrdinalIgnoreCase)));
        if (witcherGoty != null && witcherGoty.IsInstalled)
        {
            // GOTY instalado: remove as outras versões (o base vira redundante)
            games.RemoveAll(g => g != witcherGoty && g.Name.Contains("Witcher 3", StringComparison.OrdinalIgnoreCase));
        }

        // Regra Battlefield 4: Unificar Battlefield 4 Premium Edition no Battlefield 4 principal
        var bf4Base = games.FirstOrDefault(g => g.Name.Equals("Battlefield 4", StringComparison.OrdinalIgnoreCase));
        var bf4Premium = games.FirstOrDefault(g => g.Name.Contains("Battlefield 4 Premium", StringComparison.OrdinalIgnoreCase));
        if (bf4Base != null && bf4Premium != null)
        {
            foreach (var p in bf4Premium.InstalledPlatforms)
                if (!bf4Base.InstalledPlatforms.Contains(p)) bf4Base.InstalledPlatforms.Add(p);
            foreach (var p in bf4Premium.Platforms)
                if (!bf4Base.Platforms.Contains(p)) bf4Base.Platforms.Add(p);
            games.Remove(bf4Premium);
        }
    }

    private static GameEntry MergeGroupEntries(List<GameEntry> group)
    {
        if (group.Count == 1) return group[0];

        // Escolhe a entrada primária: prefere a que tem instalação real com URI de lançamento
        var primary = group.FirstOrDefault(g => g.InstalledPlatforms.Count > 0 && (!string.IsNullOrEmpty(g.LaunchUri) || !string.IsNullOrEmpty(g.LaunchExecutable)))
                   ?? group.FirstOrDefault(g => g.InstalledPlatforms.Count > 0)
                   ?? group.FirstOrDefault(g => !string.IsNullOrEmpty(g.ReleaseKey))
                   ?? group[0];

        // Fusão de plataformas possuídas
        var allPlatforms = group
            .SelectMany(g => g.Platforms.Count > 0 ? g.Platforms.AsEnumerable() : new[] { g.Platform })
            .Distinct()
            .ToList();

        // Fusão de plataformas INSTALADAS — mantém cada plataforma separada (não funde is-installed)
        var allInstalledPlatforms = group
            .SelectMany(g => g.InstalledPlatforms)
            .Distinct()
            .ToList();

        primary.Platforms = allPlatforms;
        primary.InstalledPlatforms = allInstalledPlatforms;

        // Junta o tempo jogado de CADA plataforma separadamente - Steam e EA App, por
        // exemplo, são contas/saves diferentes, então nunca devem virar um número só
        // (isso é o que causava o "tempo errado" no Apex Legends e jogos parecidos).
        foreach (var g in group)
        {
            foreach (var (platform, minutes) in g.PlaytimeByPlatform)
                primary.PlaytimeByPlatform[platform] = minutes;

            foreach (var (platform, date) in g.LastPlayedByPlatform)
                primary.LastPlayedByPlatform[platform] = date;
        }

        // "Última sessão" pode ser só uma data (a mais recente entre as plataformas),
        // isso não mistura progresso, só responde "quando joguei isso pela última vez".
        if (primary.LastPlayedByPlatform.Count > 0)
            primary.LastPlayed = primary.LastPlayedByPlatform.Values.Max();

        primary.ReleaseKey ??= group.FirstOrDefault(g => !string.IsNullOrEmpty(g.ReleaseKey))?.ReleaseKey;
        primary.AppId ??= group.FirstOrDefault(g => !string.IsNullOrEmpty(g.AppId))?.AppId;
        primary.LaunchUri ??= group.FirstOrDefault(g => !string.IsNullOrEmpty(g.LaunchUri))?.LaunchUri;
        primary.LaunchExecutable ??= group.FirstOrDefault(g => !string.IsNullOrEmpty(g.LaunchExecutable))?.LaunchExecutable;

        // Copia resumo/metadata do GOG se a entrada primária não tiver
        var withMeta = group.FirstOrDefault(g => !string.IsNullOrEmpty(g.Summary) || g.Genres.Count > 0);
        if (withMeta != null)
        {
            primary.Summary ??= withMeta.Summary;
            if (primary.Genres.Count == 0) primary.Genres = withMeta.Genres;
            primary.Developer ??= withMeta.Developer;
            primary.Publisher ??= withMeta.Publisher;
            primary.CriticsScore ??= withMeta.CriticsScore;
        }

        return primary;
    }

        private static string GetNormalizedTitle(string title) => TitleAliasService.GroupKey(title);
}
