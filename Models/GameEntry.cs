using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace GameHub.Models;

public enum Platform
{
    Steam,
    Epic,
    Gog,
    EaApp,
    Ubisoft,
    Emulador,
    Modificado,
    Outro
}

/// <summary>Selo de uma plataforma - carrega o logo real (Resource embutido) pra mostrar num chip.</summary>
public class PlatformBadge
{
    public string Letter { get; set; } = "";
    public string ColorHex { get; set; } = "";
    public string IconPath { get; set; } = "";
}

public static class PlatformStyle
{
    public static string ColorHex(Platform platform) => platform switch
    {
        Platform.Steam => "#1B2838",
        Platform.Epic => "#2F2D2E",
        Platform.Gog => "#86328A",
        Platform.EaApp => "#E4002B",
        Platform.Ubisoft => "#0070FF",
        _ => "#4B5563"
    };

    public static string Letter(Platform platform) => platform switch
    {
        Platform.Steam => "S",
        Platform.Epic => "E",
        Platform.Gog => "G",
        Platform.EaApp => "EA",
        Platform.Ubisoft => "U",
        _ => "?"
    };

    /// <summary>
    /// Caminho do logo (Resource embutido no assembly). Usa a URI "pack://" absoluta -
    /// a forma relativa ("/Assets/Icons/x.png") funciona quando escrita direto no XAML,
    /// mas não resolve de forma confiável quando vem de data binding (que era o nosso caso).
    /// </summary>
    public static string IconPath(Platform platform) => platform switch
    {
        Platform.Steam => "pack://application:,,,/Assets/Icons/steam.png",
        Platform.Epic => "pack://application:,,,/Assets/Icons/epic.png",
        Platform.Gog => "pack://application:,,,/Assets/Icons/gog.png",
        Platform.EaApp => "pack://application:,,,/Assets/Icons/ea.png",
        Platform.Ubisoft => "pack://application:,,,/Assets/Icons/ubisoft.png",
        _ => ""
    };
}

public class GameEntry : INotifyPropertyChanged
{
    private string? _coverPath;
    private BitmapSource? _coverImage;

    public string Name { get; set; } = "";
    public Platform Platform { get; set; }

    /// <summary>Lista de todas as plataformas onde o usuário possui o jogo (para agrupamento).</summary>
    public List<Platform> Platforms { get; set; } = new();

    /// <summary>Plataformas onde o jogo está fisicamente instalado (subconjunto de Platforms).</summary>
    public List<Platform> InstalledPlatforms { get; set; } = new();

    public string InstallPath { get; set; } = "";
    public bool IsInstalled
    {
        get => InstalledPlatforms.Count > 0;
        set { /* compatibilidade legada — ignorado; use InstalledPlatforms */ }
    }

    /// <summary>ID interno ou chave da plataforma (ex.: 1091500 na Steam ou UUID na Epic).</summary>
    public string? AppId { get; set; }

    /// <summary>Chave completa do GOG Galaxy (ex.: origin_Origin.OFR.50.0003392).</summary>
    public string? ReleaseKey { get; set; }

    /// <summary>Protocolo de lançamento (ex.: steam://rungameid/123). Usado quando disponível.</summary>
    public string? LaunchUri { get; set; }

    /// <summary>Caminho completo do executável. Usado quando não há protocolo (ex.: GOG).</summary>
    public string? LaunchExecutable { get; set; }

    /// <summary>Argumentos de linha de comando pro LaunchExecutable (ex.: ROMs de emulador).</summary>
    public string? LaunchArguments { get; set; }

    /// <summary>Nome do console/sistema, só pra jogos de emulador (ex.: "Super Nintendo").</summary>
    public string? EmulatorSystem { get; set; }

    /// <summary>Caminho completo do arquivo de ROM (só jogos de emulador) - usado pra apagar o arquivo certo.</summary>
    public string? RomFilePath { get; set; }

    /// <summary>Jogo adicionado manualmente pelo usuário (aba "Jogos Modificados").</summary>
    public bool IsCustomGame { get; set; }

    // --- Dados pessoais do usuário (salvos pelo UserDataService em user_data.json) ---
    private bool _isFavorite;
    private bool _isHidden;

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; OnPropertyChanged(); } }
    }

    public bool IsHidden
    {
        get => _isHidden;
        set { if (_isHidden != value) { _isHidden = value; OnPropertyChanged(); } }
    }

    /// <summary>Última vez que o jogo foi aberto pelo próprio GameHub.</summary>
    public DateTime? LastLaunched { get; set; }

    /// <summary>
    /// Chave estável usada pra guardar favorito/oculto/último lançamento.
    /// ROM e jogo modificado usam o caminho do arquivo; jogo de loja usa o nome.
    /// </summary>
    public string UserKey
    {
        get
        {
            if (!string.IsNullOrEmpty(RomFilePath)) return "rom|" + RomFilePath.ToLowerInvariant();
            if (IsCustomGame && !string.IsNullOrEmpty(LaunchExecutable)) return "custom|" + LaunchExecutable.ToLowerInvariant();
            return "game|" + Name.Trim().ToLowerInvariant();
        }
    }

    /// <summary>Data mais recente entre as plataformas e o último lançamento pelo GameHub (só pra ordenar).</summary>
    public DateTime? SortLastPlayed
    {
        get
        {
            DateTime? best = LastPlayed;
            foreach (var d in LastPlayedByPlatform.Values)
                if (best == null || d > best) best = d;
            if (LastLaunched.HasValue && (best == null || LastLaunched > best)) best = LastLaunched;
            return best;
        }
    }

    /// <summary>Maior tempo em uma única plataforma (só pra ordenar; o tempo mostrado nunca é somado).</summary>
    public int SortPlaytime => PlaytimeByPlatform.Count > 0 ? PlaytimeByPlatform.Values.Max() : PlaytimeMinutes;

    // --- Detalhes Estendidos (Puxados do GOG Galaxy DB) ---
    public int PlaytimeMinutes { get; set; }
    public DateTime? LastPlayed { get; set; }
    public string? Summary { get; set; }
    public List<string> Genres { get; set; } = new();
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public double? CriticsScore { get; set; }

    /// <summary>
    /// Tempo jogado separado POR plataforma (ex.: Steam=12h, EA App=3h).
    /// Contas diferentes não compartilham save, então nunca devem virar um número só.
    /// </summary>
    public Dictionary<Platform, int> PlaytimeByPlatform { get; set; } = new();
    public Dictionary<Platform, DateTime> LastPlayedByPlatform { get; set; } = new();

    public string PlaytimeFormatted
    {
        get
        {
            var withTime = PlaytimeByPlatform.Where(kv => kv.Value > 0).ToList();
            if (withTime.Count == 0)
                return PlaytimeMinutes > 0 ? $"{PlaytimeMinutes / 60}h {PlaytimeMinutes % 60}m" : "Nunca jogado";

            if (withTime.Count == 1)
            {
                var m = withTime[0].Value;
                return $"{m / 60}h {m % 60}m";
            }

            // Mais de uma plataforma com horas: mostra separado, nunca soma nem escolhe "o maior"
            return string.Join("  •  ", withTime
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Value / 60}h ({GetSinglePlatformName(kv.Key)})"));
        }
    }

    public string LastPlayedFormatted
    {
        get
        {
            if (!LastPlayed.HasValue) return "Nunca jogado";
            var diff = DateTime.Now - LastPlayed.Value;
            if (diff.TotalDays < 1) return "Hoje";
            if (diff.TotalDays < 2) return "Ontem";
            if (diff.TotalDays < 7) return $"Há {(int)diff.TotalDays} dias";
            if (diff.TotalDays < 30) return $"Há {(int)(diff.TotalDays / 7)} semana(s)";
            if (diff.TotalDays < 365) return $"Há {(int)(diff.TotalDays / 30)} mês(es)";
            return LastPlayed.Value.ToString("dd/MM/yyyy");
        }
    }

    public string GenresText => Genres.Count > 0 
        ? string.Join(" • ", Genres) 
        : "Gênero não informado";

    public string? CoverPath
    {
        get => _coverPath;
        set
        {
            if (_coverPath != value)
            {
                _coverPath = value;
                OnPropertyChanged();
            }
        }
    }

    public BitmapSource? CoverImage
    {
        get => _coverImage;
        set
        {
            if (_coverImage != value)
            {
                _coverImage = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsInstalledOn(Platform platform) => InstalledPlatforms.Contains(platform);

    /// <summary>Um selo colorido por plataforma que você possui - usado nos cards em vez de texto.</summary>
    public List<PlatformBadge> PlatformBadges => Platforms
        .Distinct()
        .Select(p => new PlatformBadge
        {
            Letter = PlatformStyle.Letter(p),
            ColorHex = PlatformStyle.ColorHex(p),
            IconPath = PlatformStyle.IconPath(p)
        })
        .ToList();

    public string StatusText => IsInstalled ? "Instalado" : "Adquirido (Biblioteca)";

    public string PlatformDisplayName
    {
        get
        {
            if (Platforms != null && Platforms.Count > 1)
            {
                var names = Platforms.Distinct().Select(GetSinglePlatformName);
                return string.Join(" + ", names);
            }
            return GetSinglePlatformName(Platform);
        }
    }

    private static string GetSinglePlatformName(Platform platform) => platform switch
    {
        Platform.Steam => "Steam",
        Platform.Epic => "Epic Games",
        Platform.Gog => "GOG",
        Platform.EaApp => "EA App",
        Platform.Ubisoft => "Ubisoft",
        _ => platform.ToString()
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString() => $"{Name} ({StatusText})";
}
