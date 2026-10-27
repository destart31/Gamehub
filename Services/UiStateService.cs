using System.IO;
using System.Text.Json;

namespace GameHub.Services;

/// <summary>
/// Lembra a aba e a ordenação escolhidas, para o GameHub abrir de onde você parou.
/// Fica em %AppData%\GameHub\ui_state.json.
/// </summary>
public static class UiStateService
{
    private class State
    {
        public string Platform { get; set; } = "Todos";
        public string Sort { get; set; } = "Name";
    }

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "ui_state.json");

    private static readonly State Current = Load();

    public static string Platform => Current.Platform;
    public static string Sort => Current.Sort;

    public static void Save(string platform, string sort)
    {
        Current.Platform = platform;
        Current.Sort = sort;

        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });

            // Arquivo temporário + troca: se o programa fechar no meio, o antigo continua inteiro
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch { /* sem permissão: só não lembra na próxima vez */ }
    }

    private static State Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(ConfigPath)) ?? new State();
        }
        catch
        {
            return new State();
        }
    }
}
