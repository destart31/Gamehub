using System.IO;
using System.Media;

namespace GameHub.Services;

/// <summary>
/// Sons curtos de navegação (mover, confirmar, voltar, trocar aba, favoritar, iniciar jogo).
/// Não usa arquivos de áudio: os sons são gerados na memória (tons suaves), então não pesa no
/// projeto nem depende de pasta. Ctrl+M liga/desliga; a escolha fica em %AppData%\GameHub\sound_off.flag.
/// </summary>
public static class NavSounds
{
    private const double Volume = 0.22; // baixo de propósito: é um "clique", não música
    private const int SampleRate = 44100;

    private static readonly string MuteFlag = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "sound_off.flag");

    private static readonly SoundPlayer? MoveSound = Make((1500, 22));
    private static readonly SoundPlayer? TabSound = Make((950, 40));
    private static readonly SoundPlayer? ConfirmSound = Make((660, 55), (990, 80));
    private static readonly SoundPlayer? BackSound = Make((720, 55), (480, 80));
    private static readonly SoundPlayer? FavoriteSound = Make((880, 60), (1320, 110));
    private static readonly SoundPlayer? LaunchSound = Make((523, 70), (659, 70), (784, 70), (1047, 150));

    private static bool _enabled = !File.Exists(MuteFlag);
    private static long _lastMove;

    public static bool Enabled => _enabled;

    public static void Move()
    {
        // Segurar o direcional não pode virar uma metralhadora de cliques
        var now = Environment.TickCount64;
        if (now - _lastMove < 45) return;
        _lastMove = now;
        Play(MoveSound);
    }

    public static void Tab() => Play(TabSound);
    public static void Confirm() => Play(ConfirmSound);
    public static void Back() => Play(BackSound);
    public static void Favorite() => Play(FavoriteSound);
    public static void Launch() => Play(LaunchSound);

    /// <summary>Liga/desliga e lembra da escolha. Retorna o novo estado.</summary>
    public static bool Toggle()
    {
        _enabled = !_enabled;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MuteFlag)!);
            if (_enabled) File.Delete(MuteFlag);
            else File.WriteAllText(MuteFlag, "");
        }
        catch { /* sem permissão: vale só até fechar o programa */ }

        if (_enabled) Play(ConfirmSound);
        return _enabled;
    }

    private static void Play(SoundPlayer? player)
    {
        if (!_enabled || player == null) return;
        try { player.Play(); } // toca em thread própria, não trava a interface
        catch { /* sem dispositivo de áudio: segue sem som */ }
    }

    private static SoundPlayer? Make(params (double Freq, int Ms)[] notes)
    {
        try
        {
            var player = new SoundPlayer(new MemoryStream(BuildWav(notes)));
            player.Load();
            return player;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>WAV 16 bits mono: cada nota é um seno com ataque rápido e queda suave (sem estalo).</summary>
    private static byte[] BuildWav((double Freq, int Ms)[] notes)
    {
        var samples = new List<short>();
        foreach (var (freq, ms) in notes)
        {
            int n = SampleRate * ms / 1000;
            int attack = Math.Max(1, SampleRate * 3 / 1000);
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)SampleRate;
                double env = Math.Min(1.0, i / (double)attack) * Math.Exp(-4.0 * i / n);
                samples.Add((short)(Math.Sin(2 * Math.PI * freq * t) * env * Volume * short.MaxValue));
            }
        }

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        int dataLen = samples.Count * 2;
        w.Write("RIFF".ToCharArray());
        w.Write(36 + dataLen);
        w.Write("WAVE".ToCharArray());
        w.Write("fmt ".ToCharArray());
        w.Write(16);
        w.Write((short)1);            // PCM
        w.Write((short)1);            // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);      // bytes por segundo
        w.Write((short)2);            // bytes por amostra
        w.Write((short)16);
        w.Write("data".ToCharArray());
        w.Write(dataLen);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
    private static readonly SoundPlayer? HoverSound = Make((1700, 14));
    private static long _lastHover;

    /// <summary>Tique bem curto ao passar o mouse nos cards da Início (limitado, para não virar chuva de cliques).</summary>
    public static void Hover()
    {
        var now = Environment.TickCount64;
        if (now - _lastHover < 90) return;
        _lastHover = now;
        Play(HoverSound);
    }
}
