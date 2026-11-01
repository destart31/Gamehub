using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameHub.Models;
using GameHub.Services;
using System.IO;

namespace GameHub;

/// <summary>
/// Comportamento da janela: tela cheia de verdade (cobre a barra de tarefas), bandeja do sistema
/// ao fechar, e sair de cena (minimizar) quando um jogo abre.
/// </summary>
public partial class MainWindow
{
    // ──────────────────────────────────────────────────────────────
    // Win32
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativePoint ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_RESTORE = 9;
    private const int WM_GETMINMAXINFO = 0x0024;

    // ──────────────────────────────────────────────────────────────
    // Tela cheia (F11)
    // Antes: maximizada sem borda, que respeita a barra de tarefas (ela ficava por cima dos botões
    // de baixo). Agora: a janela ocupa o monitor inteiro, sem borda, e fica na frente enquanto ativa.
    // ──────────────────────────────────────────────────────────────

    // Regra: maximizada (normal) = ocupa só a área de trabalho, a barra de tarefas fica visível e nada fica
    // escondido atrás dela. Tela cheia (F11) = ocupa o monitor inteiro e cobre a barra.
    // O Windows pergunta o tamanho "maximizado" à janela (WM_GETMINMAXINFO); aqui respondemos conforme o modo.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProcLimites);
    }

    private IntPtr WndProcLimites(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var area = _fullscreen ? info.rcMonitor : info.rcWork;   // tela cheia: monitor todo; senão: sem a barra de tarefas
            mmi.ptMaxPosition.X = area.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = area.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = area.Right - area.Left;
            mmi.ptMaxSize.Y = area.Bottom - area.Top;

            // como respondemos no lugar do WPF, mantém o tamanho mínimo da janela
            var dpi = VisualTreeHelper.GetDpi(this);
            if (MinWidth > 0) mmi.ptMinTrackSize.X = (int)Math.Ceiling(MinWidth * dpi.DpiScaleX);
            if (MinHeight > 0) mmi.ptMinTrackSize.Y = (int)Math.Ceiling(MinHeight * dpi.DpiScaleY);

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        catch { /* se falhar, o Windows usa o comportamento padrão */ }

        return IntPtr.Zero;
    }

    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private Rect _boundsBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen = ResizeMode.CanResize;
    private Thickness _resizeBorderBeforeFullscreen = new(8);

    private void ToggleFullscreen()
    {
        var titleRow = ((Grid)TitleBar.Parent).RowDefinitions[0];
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);

        if (!_fullscreen)
        {
            _fullscreen = true;
            _stateBeforeFullscreen = WindowState;
            _boundsBeforeFullscreen = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            _resizeModeBeforeFullscreen = ResizeMode;

            TitleBar.Visibility = Visibility.Collapsed;
            titleRow.Height = new GridLength(0);
            if (chrome != null)
            {
                _resizeBorderBeforeFullscreen = chrome.ResizeBorderThickness;
                chrome.CaptionHeight = 0;                          // sem área invisível de arrastar no topo
                chrome.ResizeBorderThickness = new Thickness(0);   // sem borda: a janela fica exatamente do tamanho do monitor
            }

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;                      // precisa estar normal para receber o tamanho do monitor
            ApplyFullscreenBounds();
            Topmost = true;                                        // por cima da barra de tarefas (tirado quando perde o foco)
        }
        else
        {
            _fullscreen = false;
            Topmost = false;

            TitleBar.Visibility = Visibility.Visible;
            titleRow.Height = new GridLength(35);
            if (chrome != null)
            {
                chrome.CaptionHeight = 35;
                chrome.ResizeBorderThickness = _resizeBorderBeforeFullscreen;
            }

            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = _resizeModeBeforeFullscreen;
            WindowState = WindowState.Normal;
            Left = _boundsBeforeFullscreen.Left;
            Top = _boundsBeforeFullscreen.Top;
            Width = _boundsBeforeFullscreen.Width;
            Height = _boundsBeforeFullscreen.Height;
            if (_stateBeforeFullscreen == WindowState.Maximized) WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Põe a janela exatamente sobre o monitor em que ela está (barra de tarefas incluída).</summary>
    private void ApplyFullscreenBounds()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };

        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            WindowState = WindowState.Maximized;   // não deu para medir o monitor: volta ao jeito antigo
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);   // o monitor vem em pixels; a janela trabalha em unidades do WPF
        Left = info.rcMonitor.Left / dpi.DpiScaleX;
        Top = info.rcMonitor.Top / dpi.DpiScaleY;
        Width = (info.rcMonitor.Right - info.rcMonitor.Left) / dpi.DpiScaleX;
        Height = (info.rcMonitor.Bottom - info.rcMonitor.Top) / dpi.DpiScaleY;
    }

    // Em tela cheia a janela só fica "por cima de tudo" enquanto está ativa: quando o jogo (ou outro
    // programa) toma o foco, ela sai da frente sozinha.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_fullscreen) Topmost = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_fullscreen) Topmost = false;
    }

    // ──────────────────────────────────────────────────────────────
    // Bandeja do sistema: fechar (X) esconde a janela; "Sair" no ícone encerra de verdade
    // ──────────────────────────────────────────────────────────────

    private TrayService? _tray;
    private bool _exitRequested;

    private void InitTray()
    {
        try
        {
            _tray = new TrayService();
            _tray.OpenRequested += () => Dispatcher.BeginInvoke(new Action(ShowFromTray));
            _tray.ExitRequested += () => Dispatcher.BeginInvoke(new Action(ExitApp));
        }
        catch (Exception ex)
        {
            _tray = null;   // sem ícone na bandeja: fechar volta a encerrar o programa
            Logger.LogError("Bandeja do sistema", ex);
        }

        // Desligar / sair do Windows não pode ser barrado pelo "fechar vai para a bandeja"
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exitRequested && _tray != null)
        {
            e.Cancel = true;   // fechar não encerra: vai para a bandeja
            Hide();
            return;
        }

        // Saída de verdade: guarda o tamanho normal da janela (e não o de tela cheia)
        if (_fullscreen) ToggleFullscreen();
        WindowStateService.Save(this);

        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>Encerra o GameHub de verdade (menu "Sair" da bandeja).</summary>
    private void ExitApp()
    {
        _exitRequested = true;
        Close();
    }

    /// <summary>Traz a janela de volta (bandeja, segundo .exe aberto, fim do jogo). Mantém maximizada / tela cheia como estava.</summary>
    public void ShowFromTray()
    {
        if (!IsVisible) Show();

        if (WindowState == WindowState.Minimized)
            ShowWindow(new WindowInteropHelper(this).Handle, SW_RESTORE);   // volta ao estado de antes (maximizada, normal...)

        Activate();
        Topmost = true;               // truque para realmente passar para a frente de outros programas
        Topmost = _fullscreen;
        Focus();
    }

    // ──────────────────────────────────────────────────────────────
    // Jogo aberto: o GameHub sai de cena (minimiza) e volta quando o jogo fecha
    // ──────────────────────────────────────────────────────────────

    /// <summary>Ponha false para só minimizar e nunca voltar sozinho.</summary>
    private static readonly bool RestoreWhenGameCloses = true;

    private bool _minimizedForGame;
    private int _gamePid;
    private DispatcherTimer? _gameWatch;
    private int _gameGone;

    /// <summary>Chamado quando o jogo (processo <paramref name="pid"/>) tomou o foco.</summary>
    private void MinimizeForGame(int pid)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        _minimizedForGame = true;
        _gamePid = pid;
        WindowState = WindowState.Minimized;
        StartGameWatch();
    }

    private void RestoreAfterGame()
    {
        _gameWatch?.Stop();
        if (!_minimizedForGame) return;   // você já voltou para o GameHub por conta própria
        _minimizedForGame = false;
        if (!IsVisible) return;           // mandou para a bandeja nesse meio tempo: não aparece sozinho
        ShowFromTray();
    }

    // Se alguém restaurar a janela (barra de tarefas, bandeja, Alt+Tab), não há mais o que "voltar"
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized) _minimizedForGame = false;
    }

    /// <summary>Acompanha o processo do jogo; quando ele fecha, o GameHub reaparece.</summary>
    private void StartGameWatch()
    {
        if (!RestoreWhenGameCloses) return;

        _gameWatch?.Stop();
        _gameGone = 0;
        _gameWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _gameWatch.Tick += (_, _) =>
        {
            if (!_minimizedForGame) { _gameWatch?.Stop(); return; }
            if (IsProcessAlive(_gamePid)) { _gameGone = 0; return; }

            // O processo acabou. Espera um pouco: alguns jogos abrem por um "iniciador" que fecha logo
            // depois. Se outro programa de jogo está na frente, passa a acompanhar esse.
            if (++_gameGone < 2) return;
            var next = FindGameProcess();
            if (next != 0) { _gamePid = next; _gameGone = 0; return; }

            RestoreAfterGame();
        };
        _gameWatch.Start();
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // ──────────────────────────────────────────────────────────────
    // Regras: o que é "o jogo" e o que é launcher / instalador / janela do Windows
    // ──────────────────────────────────────────────────────────────
    //
    // Cada janela que toma o foco depois do clique em JOGAR é classificada:
    //  • Launcher: ignorada. É launcher de loja (Steam, Epic, GOG, EA, Ubisoft, Rockstar), instalador de
    //    componentes (VC++, DirectX), relatório de erro, ou janela do Windows. Nunca minimiza o GameHub.
    //  • Jogo: o .exe está dentro da pasta do jogo (InstallPath) ou da pasta do executável configurado
    //    (o emulador, o jogo modificado). Minimiza depois de 1 s com a janela na frente.
    //  • Desconhecido: não é launcher conhecido, mas também não prova ser o jogo (ex.: o .exe está em outra
    //    pasta, ou o Windows não deixou ler o caminho). Só minimiza se ficar na frente por 6 s seguidos.

    private enum ProcKind { Launcher, Game, Unknown }

    private GameEntry? _launchingGame;      // jogo do último clique em JOGAR (não é limpo ao terminar o lançamento)
    private int _fgPid;                     // quem está na frente
    private int _fgTicks;                   // há quantos ticks (500 ms) ele está na frente

    private const int TicksGame = 2;        // 1 s
    private const int TicksUnknown = 12;    // 6 s

    private void ResetForegroundTracking() { _fgPid = 0; _fgTicks = 0; }

    /// <summary>True quando a janela da frente (pid) já está confirmada como sendo o jogo.</summary>
    private bool ForegroundIsGame(int pid)
    {
        if (pid != _fgPid) { _fgPid = pid; _fgTicks = 0; }

        var kind = Classify(pid, _launchingGame);
        if (kind == ProcKind.Launcher) { _fgTicks = 0; return false; }

        _fgTicks++;
        return _fgTicks >= (kind == ProcKind.Game ? TicksGame : TicksUnknown);
    }

    // Nomes exatos de processos que nunca são o jogo
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "shellexperiencehost", "searchhost", "searchapp", "startmenuexperiencehost",
        "textinputhost", "lockapp", "dwm", "taskmgr", "msiexec", "werfault", "dllhost", "conhost",
        "upc", "rgl",
        "vc_redist.x64", "vc_redist.x86", "vcredist_x64", "vcredist_x86", "dxsetup", "dxwebsetup",
        "dotnetfx", "oalinst", "physx", "crashreporter", "crashhandler", "unitycrashhandler64", "unitycrashhandler32"
    };

    // Prefixos de launchers de loja e de seus processos auxiliares (steam*, epicgames*, galaxy*...)
    private static readonly string[] IgnoredPrefixes =
    {
        "steam", "gameoverlayui", "epicgames", "epicweb", "epiconline", "eoslauncher",
        "galaxy", "goggalaxy", "gogalaxy",
        "eadesktop", "eabackground", "ealauncher", "eaanticheat", "origin",
        "ubisoft", "uplay", "ubisoftconnect",
        "rockstar", "socialclub",
        "unins", "setup", "vcredist", "vc_redist", "dxsetup", "installer"
    };

    // Trechos de caminho onde moram launchers e componentes (só valem se o .exe não estiver na pasta do jogo)
    private static readonly string[] LauncherPathParts =
    {
        @"\steam\", @"\steamcmd\", @"\_commonredist\", @"\steamworks shared\",
        @"\epic games\launcher\", @"\epic games\epicgameslauncher\", @"\epic online services\",
        @"\gog galaxy\", @"\ea desktop\", @"\electronic arts\ea desktop\", @"\ea app\", @"\origin\",
        @"\ubisoft game launcher\", @"\ubisoft\ubisoft game launcher\",
        @"\rockstar games\launcher\", @"\rockstar games\social club\",
        @"\windows\system32\", @"\windows\syswow64\", @"\windows\systemapps\", @"\windows\immersivecontrolpanel\"
    };

    private ProcKind Classify(int pid, GameEntry? game)
    {
        string name;
        try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
        catch { return ProcKind.Launcher; }   // já fechou: não é o jogo

        var path = GetProcessPath(pid);

        // 1) O .exe está na pasta do jogo: é o jogo (mesmo se o nome lembrar um launcher, ex.: "steam_api_helper")
        if (path != null && IsInsideGameFolders(path, game)) return ProcKind.Game;

        // 2) Launcher / instalador / Windows (por nome ou por pasta)
        if (IgnoredNames.Contains(name) || IgnoredNames.Contains(name + ".exe")) return ProcKind.Launcher;
        foreach (var prefix in IgnoredPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return ProcKind.Launcher;
        if (path != null && IsLauncherPath(path)) return ProcKind.Launcher;

        // 3) Não dá para provar nem descartar
        return ProcKind.Unknown;
    }

    /// <summary>Pastas onde o .exe do jogo pode estar: a pasta de instalação e a pasta do executável configurado.</summary>
    private static List<string> GameFolders(GameEntry? game)
    {
        var list = new List<string>();
        if (game == null) return list;

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
                if (full.Length < 4) return;                                   // "C:" / raiz do disco: genérico demais
                if (!list.Contains(full, StringComparer.OrdinalIgnoreCase)) list.Add(full);
            }
            catch { }
        }

        Add(game.InstallPath);
        if (!string.IsNullOrWhiteSpace(game.LaunchExecutable)) Add(Path.GetDirectoryName(game.LaunchExecutable));
        return list;
    }

    private static bool IsInsideGameFolders(string exePath, GameEntry? game)
    {
        foreach (var dir in GameFolders(game))
            if (exePath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsLauncherPath(string exePath)
    {
        var p = exePath.ToLowerInvariant();
        foreach (var part in LauncherPathParts)
            if (p.Contains(part)) return true;
        return false;
    }

    /// <summary>Procura o processo do jogo: a janela da frente (se for o jogo) ou qualquer processo da pasta do jogo. 0 se não houver.</summary>
    private int FindGameProcess()
    {
        try
        {
            var game = _launchingGame;

            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hwnd, out var fg);
                if (fg != 0 && fg != (uint)Environment.ProcessId && Classify((int)fg, game) == ProcKind.Game)
                    return (int)fg;
            }

            if (GameFolders(game).Count == 0) return 0;
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId) continue;
                    var path = GetProcessPath(p.Id);
                    if (path != null && IsInsideGameFolders(path, game)) return p.Id;
                }
            }
        }
        catch { }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder text, ref int size);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;   // funciona até com jogos abertos como administrador

    /// <summary>Caminho do .exe de um processo. null se o Windows não deixar ler.</summary>
    private static string? GetProcessPath(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            var size = sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
}
