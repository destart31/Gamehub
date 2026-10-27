using System.Net.Http;
using System.Text.Json;
using GameHub.Models;

namespace GameHub.Services;

/// <summary>
/// Busca resumo, gêneros, desenvolvedora e publicadora direto da API pública
/// da loja da Steam (store.steampowered.com/api/appdetails) - não precisa de
/// login nem chave, é a mesma que a página da loja usa.
///
/// Serve pra duas coisas:
///  1) Preencher jogos que o GOG Galaxy não catalogou (ex.: DmC, Devil May Cry 5).
///  2) Trocar o resumo em inglês (vindo do Galaxy) por um em português, já que
///     aqui a gente pede explicitamente l=portuguese&cc=br.
/// </summary>
public static class SteamStoreMetadataService
{
    private static readonly HttpClient HttpClient = new();

    // A Steam não documenta um limite oficial pra esse endpoint, mas ele pode
    // começar a recusar pedidos se bater rápido demais - 4 por vez é conservador.
    private static readonly SemaphoreSlim Throttle = new(4, 4);

    public static async Task EnrichAllAsync(List<GameEntry> games)
    {
        var steamGames = games
            .Where(g => g.Platforms.Contains(Platform.Steam) && !string.IsNullOrWhiteSpace(g.AppId))
            .ToList();

        await Parallel.ForEachAsync(steamGames, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (game, _) => await EnrichOneAsync(game));
    }

    private static async Task EnrichOneAsync(GameEntry game)
    {
        await Throttle.WaitAsync();
        try
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={game.AppId}&l=portuguese&cc=br";
            var json = await HttpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(game.AppId!, out var appEntry)) return;
            if (!appEntry.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) return;
            if (!appEntry.TryGetProperty("data", out var data)) return;

            // Resumo em português substitui o que veio do Galaxy (geralmente em inglês)
            if (data.TryGetProperty("short_description", out var descProp))
            {
                var desc = descProp.GetString();
                if (!string.IsNullOrWhiteSpace(desc))
                    game.Summary = System.Net.WebUtility.HtmlDecode(desc);
            }

            if (string.IsNullOrWhiteSpace(game.Developer) &&
                data.TryGetProperty("developers", out var devs) &&
                devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0)
                game.Developer = devs[0].GetString();

            if (string.IsNullOrWhiteSpace(game.Publisher) &&
                data.TryGetProperty("publishers", out var pubs) &&
                pubs.ValueKind == JsonValueKind.Array && pubs.GetArrayLength() > 0)
                game.Publisher = pubs[0].GetString();

            if (game.Genres.Count == 0 &&
                data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var g in genres.EnumerateArray())
                {
                    if (g.TryGetProperty("description", out var gd))
                    {
                        var s = gd.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                    }
                }
                if (list.Count > 0) game.Genres = list;
            }

            if (game.CriticsScore == null &&
                data.TryGetProperty("metacritic", out var meta) &&
                meta.TryGetProperty("score", out var scoreProp))
            {
                game.CriticsScore = scoreProp.GetDouble();
            }
        }
        catch
        {
            // Jogo sem página pública na loja, endpoint fora do ar, etc. - só não enriquece esse
        }
        finally
        {
            Throttle.Release();
        }
    }
}
