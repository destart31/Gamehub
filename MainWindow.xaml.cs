using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using GameHub.Models;
using GameHub.Scanners;
using GameHub.Services;
using System.Windows.Threading;

namespace GameHub;

public partial class MainWindow : Window
{
    private readonly GameLibraryService _libraryService = new();
    private readonly EmulatorScanner _emulatorScannerInfo = new();
    private readonly ControllerService _controller = new();
    private bool _controllerMode = false;
    private Point _lastMousePos;
    private List<GameEntry> _allGames = new();
    private string _selectedPlatform = "Todos";
    private string? _selectedEmulatorSystem = null;
    private class NavTarget
    {
        public bool TopBar { get; set; }
        public Func<FrameworkElement?> Element { get; set; } = () => null;
        public GameEntry? Game { get; set; }
        public Action? Activate { get; set; }
    }
    private List<NavTarget> _navTargets = new(); // tudo que o controle pode selecionar na tela atual
    private int _navIndex = 0;
    private FrameworkElement? _highlighted;
    private const int HomeTileCount = 30;       // quantos jogos aparecem na tela Início
    private GameEntry? _homeGame;                // jogo em foco na tela Início
    private FrameworkElement? _homeMouseContainer;
    private Border? _homeMouseTile;
    private int _bgTarget;
    private GameEntry? _currentDetailGame;
    private enum SortMode { Name, LastPlayed, MostPlayed }
    private SortMode _sortMode = SortMode.Name;
    private bool _detailOpen = false; // painel de detalhe aberto (vira false assim que começa a fechar)
    private const int RecentMaxAgeDays = 30;
    private bool _showHidden = false; // true = mostra só os jogos ocultos (pra poder desocultar)
    private bool _restoringUi; // true enquanto restaura aba/ordem salvas ao abrir
    private bool _uiReady; // só depois de restaurar aba/ordem é que escolhas passam a ser salvas
    private DispatcherTimer? _clockTimer;
    private Task? _localCoversTask; // carregamento das capas locais em andamento
    private bool _libraryLoaded;   // true depois da primeira carga completa
    private bool _refreshing;      // varredura automática em andamento
    private DateTime _lastRefresh = DateTime.MinValue;
    private int _filterVersion;
    private int _homeBgVersion;
    private CancellationTokenSource? _bgFetchCts;
    private int _dnShot;               // miniatura selecionada
    private Brush? _dnBtnBg, _dnBtnFg;
 
    private bool ViewerOpen => ShotViewerRoot?.Visibility == Visibility.Visible;
    private bool CpOpen => CollectionPickerRoot?.Visibility == Visibility.Visible;
    private bool OverlayBlocksActions => !KbOpen && (ViewerOpen || CpOpen);
 
    // ── O que some no modo controle ──────────────────────────────────────────
 
    /// <summary>Chamado ao entrar/sair do modo controle e depois de recriar os chips de coleção.</summary>
    private void ApplyControllerOnlyVisibility()
    {
        bool c = _controllerMode;
 
        Resources["CardEditVis"] = c ? Visibility.Collapsed : Visibility.Visible; // lixeiras dos cards
 
        if (AddCustomGameButton != null)
            AddCustomGameButton.Visibility = !c && _selectedPlatform == "Modificados"
                ? Visibility.Visible : Visibility.Collapsed;
 
        if (DuplicatesButton != null)
            DuplicatesButton.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
 
        if (CollectionChips != null)
            foreach (var child in CollectionChips.Children)
                if (child is Button chip && (chip.Tag as string) == ColPrefix + "\u0001new")
                    chip.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
 
        if (DetailPanelRoot?.Visibility == Visibility.Visible)
        {
            ApplyDetailControllerLayout();
            DetailHighlight();
        }
        UpdatePadGlyphs();
    }
 
    /// <summary>No detalhe, com controle: sem "Links" (abrem o navegador) e sem "Personalizar" (abre seletor de arquivo).</summary>
    private void ApplyDetailControllerLayout()
    {
        var v = _controllerMode ? Visibility.Collapsed : Visibility.Visible;
        DetailLinksCard.Visibility = v;
        DetailCustomizeCard.Visibility = v;
    }
 
    // ── Painel de detalhes pelo controle ─────────────────────────────────────
 
    private void UpdateDetailCollections(GameEntry game)
    {
        var names = CollectionsService.Names().Where(n => CollectionsService.Contains(n, game)).ToList();
        DetailCollectionsText.Text = names.Count > 0 ? string.Join("  ·  ", names) : "Nenhuma";
    }
 
    // ── Visualizador de capturas pelo controle ───────────────────────────────
 
    private void ViewerMove(Dir dir)
    {
        if (dir == Dir.Left) { NavSounds.Move(); OpenShot(_shotIndex - 1); }
        else if (dir == Dir.Right) { NavSounds.Move(); OpenShot(_shotIndex + 1); }
    }
 
    // ── Seletor de coleções (abre pelo botão "Editar coleções" do detalhe) ───
 
    private GameEntry? _cpGame;
    private int _cpIndex;
    private readonly List<Border> _cpRows = new();
    private readonly List<string> _cpNames = new();
 
    private void DetailCollectionsButton_Click(object sender, RoutedEventArgs e) => OpenCollectionPicker();
 
    private void OpenCollectionPicker()
    {
        var game = _currentDetailGame;
        if (game == null) return;
 
        _cpGame = game;
        _cpIndex = 0;
        CollectionPickerTitle.Text = game.Name;
        BuildCollectionPicker();
        CollectionPickerRoot.Visibility = Visibility.Visible;
        RefreshHints();
    }

        private string? _kbNameBuf;                 // != null => digitando um nome (e não a pesquisa)
    private string _kbTitleBase = "PESQUISAR";
    private Func<string, bool>? _kbIsValid;
    private Action<string>? _kbOnDone;
    private bool KbNameMode => _kbNameBuf != null;
 
    private void OpenNameKeyboard(string title, string initial, Func<string, bool> isValid, Action<string> onDone)
    {
        BuildKeyboard();
        _kbNameBuf = initial;
        _kbIsValid = isValid;
        _kbOnDone = onDone;
        _kbTitleBase = title;
        KbTitle.Text = title;
        _kbR = 1; _kbC = 0;
        KbRefreshText();
        KbHighlight();
        KbRoot.Visibility = Visibility.Visible;
        RefreshHints();
    }
 
    private void EndNameKeyboard()
    {
        _kbNameBuf = null; _kbIsValid = null; _kbOnDone = null;
        KbRoot.Visibility = Visibility.Collapsed;
        RefreshHints();
    }
 
    /// <summary>OK / Start no teclado: na pesquisa fecha; no modo nome confirma (se o nome for válido).</summary>
    private void KbFinish()
    {
        if (!KbNameMode) { CloseKeyboard(); return; }
 
        var name = (_kbNameBuf ?? "").Trim();
        if (name.Length == 0 || _kbIsValid?.Invoke(name) == false)
        {
            NavSounds.Back();
            KbTitle.Text = name.Length == 0 ? "DIGITE UM NOME" : "ESSE NOME JÁ EXISTE";
            return;
        }
 
        var done = _kbOnDone;
        EndNameKeyboard();
        NavSounds.Confirm();
        done?.Invoke(name);
    }
 
    private void KbType(string s)
    {
        if (KbNameMode)
        {
            if (_kbNameBuf!.Length >= 40) return;
            if (_kbNameBuf.Length == 0 || _kbNameBuf.EndsWith(' ')) s = s.ToUpperInvariant(); // Maiúscula no começo de cada palavra
            _kbNameBuf += s;
            KbTitle.Text = _kbTitleBase;
        }
        else SearchBox.Text += s;
 
        NavSounds.Hover();
        KbRefreshText();
    }
 
    private void KbBackspace()
    {
        if (KbNameMode)
        {
            if (_kbNameBuf!.Length > 0) _kbNameBuf = _kbNameBuf[..^1];
            KbTitle.Text = _kbTitleBase;
        }
        else if (SearchBox.Text.Length > 0) SearchBox.Text = SearchBox.Text[..^1];
 
        NavSounds.Hover();
        KbRefreshText();
    }
 
    private void KbRefreshText()
    {
        string text = KbNameMode ? _kbNameBuf! : SearchBox.Text;
        bool empty = text.Length == 0;
        KbText.Text = empty ? (KbNameMode ? "Digite o nome…" : "Digite para pesquisar…") : text + "▏";
        KbText.Foreground = empty ? new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)) : Brushes.White;
    }
 
    private void CloseKeyboard()
    {
        if (KbNameMode) { EndNameKeyboard(); return; } // cancelou o nome: volta para onde estava
 
        KbRoot.Visibility = Visibility.Collapsed;
        if (SearchBox.Text.Length == 0) SearchBar.Visibility = Visibility.Collapsed;
        UpdateTopToggles();
 
        // O cursor volta para os jogos (já filtrados)
        EnsureTopBarTargets();
        int first = _navTargets.FindIndex(t => !t.TopBar);
        _navIndex = first >= 0 ? first : 0;
        ScheduleHighlight();
        RefreshHints();
    }
 
    // ── Status do jogo (usa coleções reservadas: filtra pelos chips de Coleções) ─
 
    private static readonly string[] StatusNames = { "Quero jogar", "Jogando", "Zerado" };
 
    private static string? CurrentStatus(GameEntry g)
        => StatusNames.FirstOrDefault(n => CollectionsService.Contains(n, g));
 
    private void ToggleStatus(GameEntry game, string name)
        => SetStatus(game, CurrentStatus(game) == name ? null : name);
 
    /// <summary>Muda o status (null = limpar). Só um status por jogo.</summary>
    private void SetStatus(GameEntry game, string? status)
    {
        foreach (var n in StatusNames)
            if (n != status && CollectionsService.Contains(n, game))
                CollectionsService.Toggle(n, game);
 
        if (status != null && !CollectionsService.Contains(status, game))
        {
            if (!CollectionsService.Exists(status)) CollectionsService.Create(status);
            CollectionsService.Toggle(status, game);
        }
 
        NavSounds.Confirm();
        RefreshCollections();
        UpdateTopNav();
 
        // Se a lista aberta é uma coleção (ex.: "Jogando"), ela muda na hora
        if (IsCollectionTab(_selectedPlatform) || (IsOnHome && IsCollectionTab(_homeFilter))) ApplyFilter();
 
        UpdateDetailCollections(game);
        DetailHighlight();
    }
 
    private void BuildDetailStatus(GameEntry game)
    {
        DetailStatusPanel.Children.Clear();
        foreach (var name in StatusNames)
        {
            var n = name;
            var b = new Button
            {
                Style = (Style)FindResource("TopChipStyle"),
                FontSize = 12,
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = Brushes.White
            };
            b.Click += (_, _) => { if (_currentDetailGame != null) ToggleStatus(_currentDetailGame, n); };
            DetailStatusPanel.Children.Add(b);
        }
        PaintStatus(game);
    }
 
    /// <summary>Cores base dos botões de status: o ativo fica verde-escuro com ✓.</summary>
    private void PaintStatus(GameEntry? game)
    {
        var cur = game != null ? CurrentStatus(game) : null;
        var buttons = DetailStatusPanel.Children.OfType<Button>().ToList();
        for (int i = 0; i < buttons.Count && i < StatusNames.Length; i++)
        {
            bool active = cur == StatusNames[i];
            buttons[i].Content = active ? "✓  " + StatusNames[i] : StatusNames[i];
            buttons[i].Background = new SolidColorBrush(active
                ? Color.FromRgb(0x0F, 0x6B, 0x4D) : Color.FromRgb(0x1F, 0x22, 0x33));
        }
    }
 
    // ── Painel de detalhes pelo controle (3 linhas: capturas, status, coleções) ─
 
    private int _dnRow;      // 0 capturas, 1 status, 2 botão "Editar coleções"
    private int _dnStatus;   // status selecionado na linha 1
 
    private int[] DetailRows() => DetailShotsPanel.Children.Count > 0 ? new[] { 0, 1, 2 } : new[] { 1, 2 };
 
    private void DetailHighlight()
    {
        var thumbs = DetailShotsPanel.Children.OfType<Border>().ToList();
        foreach (var t in thumbs) t.BorderBrush = Brushes.Transparent;
 
        PaintStatus(_currentDetailGame);
 
        _dnBtnBg ??= DetailCollectionsButton.Background;
        _dnBtnFg ??= DetailCollectionsButton.Foreground;
        DetailCollectionsButton.Background = _dnBtnBg;
        DetailCollectionsButton.Foreground = _dnBtnFg;
 
        if (!_controllerMode || !_detailOpen) return;
 
        var rows = DetailRows();
        if (Array.IndexOf(rows, _dnRow) < 0) _dnRow = rows[0];
 
        switch (_dnRow)
        {
            case 0:
                _dnShot = Math.Clamp(_dnShot, 0, thumbs.Count - 1);
                thumbs[_dnShot].BorderBrush = HomeAccent;
                thumbs[_dnShot].BringIntoView();
                break;
            case 1:
                var sb = DetailStatusPanel.Children.OfType<Button>().ToList();
                if (sb.Count == 0) break;
                _dnStatus = Math.Clamp(_dnStatus, 0, sb.Count - 1);
                sb[_dnStatus].Background = HomeAccent;
                sb[_dnStatus].BringIntoView();
                break;
            default:
                DetailCollectionsButton.Background = HomeAccent;
                DetailCollectionsButton.Foreground = Brushes.White;
                DetailCollectionsButton.BringIntoView();
                break;
        }
    }
 
    private void DetailMove(Dir dir)
    {
        int n = DetailShotsPanel.Children.Count;
        var rows = DetailRows();
        int pos = Math.Max(0, Array.IndexOf(rows, _dnRow));
        bool moved = false;
 
        switch (dir)
        {
            case Dir.Left:
                if (_dnRow == 0 && _dnShot > 0) { _dnShot--; moved = true; }
                else if (_dnRow == 1 && _dnStatus > 0) { _dnStatus--; moved = true; }
                break;
            case Dir.Right:
                if (_dnRow == 0 && _dnShot < n - 1) { _dnShot++; moved = true; }
                else if (_dnRow == 1 && _dnStatus < StatusNames.Length - 1) { _dnStatus++; moved = true; }
                break;
            case Dir.Down:
                if (pos < rows.Length - 1) { _dnRow = rows[pos + 1]; moved = true; }
                else DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset + 180); // lê o resto do painel
                break;
            default: // Up
                if (pos > 0) { _dnRow = rows[pos - 1]; moved = true; }
                else DetailScroll.ScrollToVerticalOffset(DetailScroll.VerticalOffset - 180);
                break;
        }
 
        if (moved) { NavSounds.Move(); DetailHighlight(); }
    }
 
    private void DetailConfirm()
    {
        if (!_detailOpen || _currentDetailGame == null) return;
 
        switch (_dnRow)
        {
            case 0 when DetailShotsPanel.Children.Count > 0: OpenShot(_dnShot); break;
            case 1: ToggleStatus(_currentDetailGame, StatusNames[Math.Clamp(_dnStatus, 0, StatusNames.Length - 1)]); break;
            default: OpenCollectionPicker(); break;
        }
    }
 
    // ── Seletor de coleções com "+ Nova coleção" ─────────────────────────────
 
    private void BuildCollectionPicker()
    {
        CollectionPickerList.Children.Clear();
        _cpRows.Clear();
        _cpNames.Clear();
        if (_cpGame == null) return;
 
        Border MakeRow(string check, string text, bool accentText, Action onClick)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = check, Width = 24, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = HomeAccent
            });
            row.Children.Add(new TextBlock
            {
                Text = text, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = accentText ? HomeAccent : Brushes.White
            });
            var border = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6), BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x24)),
                BorderBrush = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                Child = row
            };
            border.MouseLeftButtonDown += (_, _) => { _cpIndex = _cpRows.IndexOf(border); CpHighlight(); onClick(); };
            _cpRows.Add(border);
            CollectionPickerList.Children.Add(border);
            return border;
        }
 
        foreach (var name in CollectionsService.Names())
        {
            _cpNames.Add(name);
            MakeRow(CollectionsService.Contains(name, _cpGame) ? "✓" : "", name, false, CpToggle);
        }
 
        MakeRow("+", "Nova coleção…", true, CpToggle); // última linha: _cpIndex == _cpNames.Count
 
        CollectionPickerEmpty.Visibility = Visibility.Collapsed;
        CpHighlight();
    }
 
    private void CpMove(Dir dir)
    {
        if (_cpRows.Count == 0) return;
        int next = Math.Clamp(_cpIndex + (dir == Dir.Down ? 1 : dir == Dir.Up ? -1 : 0), 0, _cpRows.Count - 1);
        if (next == _cpIndex) return;
        _cpIndex = next;
        NavSounds.Move();
        CpHighlight();
    }
 
    private void CpToggle()
    {
        if (_cpGame == null || _cpIndex < 0 || _cpIndex >= _cpRows.Count) return;
 
        if (_cpIndex >= _cpNames.Count) { CpNew(); return; } // linha "+ Nova coleção…"
 
        var name = _cpNames[_cpIndex];
        ToggleInCollection(name, _cpGame); // marca/desmarca, toca o som e atualiza a lista aberta
        ((TextBlock)((StackPanel)_cpRows[_cpIndex].Child).Children[0]).Text =
            CollectionsService.Contains(name, _cpGame) ? "✓" : "";
        UpdateDetailCollections(_cpGame);
        PaintStatus(_cpGame);
    }
 
    private void CpNew()
    {
        var game = _cpGame;
        if (game == null) return;
 
        if (!_controllerMode)
        {
            // Mouse: a caixa de texto de sempre
            CreateCollectionFor(game);
            BuildCollectionPicker();
            UpdateDetailCollections(game);
            return;
        }
 
        // Controle: teclado virtual (por cima do seletor)
        OpenNameKeyboard("NOVA COLEÇÃO", "",
            n => !CollectionsService.Exists(n),
            name =>
            {
                if (!CollectionsService.Create(name)) return;
                CollectionsService.Toggle(name, game);
                RefreshCollections();
                UpdateTopNav();
                BuildCollectionPicker();
                _cpIndex = Math.Max(0, _cpNames.IndexOf(name));
                CpHighlight();
                UpdateDetailCollections(game);
            });
    }

 
    private void CpHighlight()
    {
        for (int i = 0; i < _cpRows.Count; i++)
            _cpRows[i].BorderBrush = i == _cpIndex ? HomeAccent : Brushes.Transparent;
        if (_cpIndex >= 0 && _cpIndex < _cpRows.Count) _cpRows[_cpIndex].BringIntoView();
    }
 
    private void CloseCollectionPicker()
    {
        CollectionPickerRoot.Visibility = Visibility.Collapsed;
        _cpGame = null;
        RefreshHints();
    }
 
    private void CollectionPicker_BgClick(object sender, MouseButtonEventArgs e) => CloseCollectionPicker();

        private Button? _emuBack, _emuAdd, _emuRefresh;
 
    /// <summary>
    /// Conteúdo de um botão: no mouse, o texto de sempre; no controle, o botão do controle + o texto.
    /// </summary>
    private object PadContent(string key, string mouseText, string padText)
    {
        if (!_controllerMode) return mouseText;
 
        var glyph = BuildHintGlyph(key);
        glyph.LayoutTransform = new ScaleTransform(0.8, 0.8); // um pouco menor para não engordar o botão
        glyph.VerticalAlignment = VerticalAlignment.Center;
 
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(glyph);
        sp.Children.Add(new TextBlock
        {
            Text = padText,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return sp;
    }
 
    /// <summary>Atualiza os botões que mostram o ícone do controle. Chamado ao trocar de modo e ao abrir telas.</summary>
    private void UpdatePadGlyphs()
    {
        // Tela de um console (Emuladores)
        if (_emuBack != null) _emuBack.Content = PadContent("B", "←  Voltar aos consoles", "Voltar aos consoles");
        if (_emuAdd != null) _emuAdd.Visibility = _controllerMode ? Visibility.Collapsed : Visibility.Visible;
        if (_emuRefresh != null) _emuRefresh.Visibility = _controllerMode ? Visibility.Collapsed : Visibility.Visible;
 
        // Painel de detalhes
        DetailPlayButton.Content = PadContent("START", "▶  JOGAR AGORA", "JOGAR AGORA");
        if (_currentDetailGame != null) UpdateDetailUserButtons(_currentDetailGame);
 
        if (_controllerMode)
        {
            var b = BuildHintGlyph("B");
            b.HorizontalAlignment = HorizontalAlignment.Center;
            CloseDetailButton.Content = b;
        }
        else CloseDetailButton.Content = "✕";
 
        // Visualizador de capturas
        ShotCloseButton.Content = PadContent("B", "✕  Fechar", "Fechar");
    }
 


    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (s, e) => LayoutHomeBg();
        StartClock();
        RefreshCollections();
        RestoreUiState();
        UpdateTopNav();
        Loaded += async (_, _) =>
        {
            RestoreUiState();
            _uiReady = true;
            await LoadGamesAsync();
        };
        Closed += (_, _) => _controller.Stop();

        StateChanged += (_, _) =>
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        WindowStateService.Restore(this);
        Closing += (_, _) => WindowStateService.Save(this);

        Deactivated += (_, _) =>
        {
        if (_launching)
        {
            _launchSawDeactivate = true;        // outra janela assumiu: a camada fica no fundo
            // em vez de cancelar o timeout, estende para 90s (evita camada presa)
            _launchTimeout?.Stop();
            _launchTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
            _launchTimeout.Tick += (_, _) => HideLaunchOverlay();
            _launchTimeout.Start();
        }
        ClearHighlight();                       // para o brilho do controle
        };

        Activated += (_, _) =>
        {
        // Voltou ao GameHub depois de um tempo (jogo fechado / alt-tab): tira a camada
        if (_launching && _launchSawDeactivate &&
            (DateTime.Now - _launchStartedAt).TotalSeconds > 15)
            HideLaunchOverlay();

        if (_controllerMode) ScheduleHighlight();
        };
        Activated += async (_, _) => await RefreshLibraryQuietlyAsync();
    
        // Troca automática de modo
        MouseMove += (s, e) =>
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _lastMousePos.X) > 3 || Math.Abs(pos.Y - _lastMousePos.Y) > 3)
            {
                _lastMousePos = pos;
                EnterMouseMode();
            }
        };
        KeyDown += (s, e) =>
        {
            if (_launching && e.Key == System.Windows.Input.Key.Escape) { HideLaunchOverlay(); return; }
            switch (e.Key)
            {
                case System.Windows.Input.Key.F11:
                case System.Windows.Input.Key.Snapshot:      // PrintScreen
                case System.Windows.Input.Key.LWin:
                case System.Windows.Input.Key.RWin:
                case System.Windows.Input.Key.LeftShift:
                case System.Windows.Input.Key.RightShift:
                    break;
                default:
                    EnterMouseMode();
                    break;
            }
        };

                // Digitar em qualquer lugar vai direto pra busca
        PreviewTextInput += (s, e) =>
        {
            if (System.Windows.Input.Keyboard.FocusedElement is TextBox) return;
            if (_launching || OverlayOpen()) return;
            if (LoadingOverlay?.Visibility == Visibility.Visible) return;
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            if ((System.Windows.Input.Keyboard.Modifiers &
                 (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt)) != 0) return;

            EnterMouseMode();
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.UpdateLayout();
            UpdateTopToggles();
            SearchBox.Focus();
            SearchBox.Text += e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        };
    
        // O controle (XInput) é lido mesmo com a janela em segundo plano.
        // Só reage quando o GameHub está ativo, senão o Start do jogo dispara outro jogo.
        void Guard(Action a) => Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive || WindowState == WindowState.Minimized) return;
            if (_launching) return; // com a camada de lançamento aberta, o controle não age
            a();
        });
    
        _controller.AnyInput      += () => Guard(EnterControllerMode);
        _controller.DPadRight     += () => Guard(() => Move(Dir.Right));
        _controller.DPadLeft      += () => Guard(() => Move(Dir.Left));
        _controller.DPadDown      += () => Guard(() => Move(Dir.Down));
        _controller.DPadUp        += () => Guard(() => Move(Dir.Up));
        _controller.ButtonA       += () => Guard(ControllerConfirm);
        _controller.ButtonY       += () => Guard(ControllerToggleFavorite);
        _controller.ButtonX       += () => Guard(ControllerToggleHidden);
         _controller.ButtonB += () => Dispatcher.BeginInvoke(() =>
            {
                if (!IsActive) return;
                if (_launching) HideLaunchOverlay(); else ControllerBack();
            });
        _controller.RightShoulder += () => Guard(() => CyclePlatformTab(+1));
        _controller.LeftShoulder  += () => Guard(() => CyclePlatformTab(-1));
        _controller.Start         += () => Guard(ControllerPlay);
        _controller.BatteryChanged += OnControllerBattery;
        _controller.Begin();
    }

    private int _detailRichVersion;
    private List<ShotInfo> _shots = new();
    private int _shotIndex;

    /// <summary>Chamado ao abrir o painel: mostra os links na hora e busca o resto em segundo plano.</summary>
    private void ResetDetailRich(GameEntry game)
    {
        DetailShotsCard.Visibility = Visibility.Collapsed;
        DetailReqCard.Visibility = Visibility.Collapsed;
        DetailShotsPanel.Children.Clear();
        _shots = new List<ShotInfo>();
        BuildDetailLinks(game, null);
        BuildDetailStatus(game);
        DetailHighlight();
        _ = LoadDetailRichAsync(game);
        UpdateDetailCollections(game);
    }

    private async Task LoadDetailRichAsync(GameEntry game)
    {
        int v = ++_detailRichVersion;
        GameDetails? d = null;
        try { d = await GameDetailsService.GetAsync(game); }
        catch (Exception ex) { Logger.LogError("Detalhes do jogo", ex); }

        // Já abriu outro jogo ou fechou o painel enquanto carregava
        if (v != _detailRichVersion || !ReferenceEquals(_currentDetailGame, game) || d == null) return;

        BuildDetailLinks(game, d);
        BuildShots(d);
        BuildRequirements(d);
    }

    private static string UrlName(GameEntry game)
        => Uri.EscapeDataString(game.Name.Replace("™", "").Replace("®", "").Trim());

    private void BuildDetailLinks(GameEntry game, GameDetails? d)
    {
        DetailLinksPanel.Children.Clear();

        void Add(string text, string url)
        {
            var b = new Button
            {
                Content = text,
                Style = (Style)FindResource("TopChipStyle"),
                Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x22, 0x33)),
                FontSize = 12,
                Padding = new Thickness(14, 8, 14, 8),
                Margin = new Thickness(0, 0, 8, 8)
            };
            b.Click += (_, _) => OpenUrl(url);
            DetailLinksPanel.Children.Add(b);
        }

        if (d?.SteamAppId != null) Add("Loja Steam", $"https://store.steampowered.com/app/{d.SteamAppId}");
        if (!string.IsNullOrWhiteSpace(d?.Website)) Add("Site oficial", d!.Website!);
        Add("PCGamingWiki", d?.SteamAppId != null
            ? $"https://www.pcgamingwiki.com/api/appid.php?appid={d.SteamAppId}"
            : $"https://www.pcgamingwiki.com/w/index.php?search={UrlName(game)}");
        Add("HowLongToBeat", $"https://howlongtobeat.com/?q={UrlName(game)}");
    }

    private void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Logger.LogError("Abrir link", ex); ShowToast("Não consegui abrir o link"); }
    }

    private void BuildShots(GameDetails d)
    {
        DetailShotsPanel.Children.Clear();
        _shots = d.Screenshots;
        if (_shots.Count == 0) { DetailShotsCard.Visibility = Visibility.Collapsed; return; }

        for (int i = 0; i < _shots.Count; i++)
        {
            int index = i;
            var img = new Image { Stretch = Stretch.UniformToFill };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(_shots[i].Thumb);
                bmp.DecodePixelWidth = 352;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                img.Source = bmp;
            }
            catch { /* link inválido: a miniatura fica vazia */ }

            var thumb = new Border
            {
                Width = 176, Height = 99, Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0F, 0x1A)),
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = img
            };
            // Recorte recuado pela borda (176 - 4 = 172; 99 - 4 = 95; raio 8 - 2 = 6)
            img.Clip = new RectangleGeometry(new Rect(0, 0, 172, 95), 6, 6);
            thumb.MouseEnter += (_, _) => thumb.BorderBrush = HomeAccent;
            thumb.MouseLeave += (_, _) => thumb.BorderBrush = Brushes.Transparent;
            thumb.MouseLeftButtonDown += (_, _) => { NavSounds.Confirm(); OpenShot(index); };

            DetailShotsPanel.Children.Add(thumb);
        }
        DetailShotsCard.Visibility = Visibility.Visible;
    }

    private void BuildRequirements(GameDetails d)
    {
        bool hasReq = !string.IsNullOrWhiteSpace(d.MinRequirements) || !string.IsNullOrWhiteSpace(d.RecRequirements);
        DetailReqMinText.Text = string.IsNullOrWhiteSpace(d.MinRequirements) ? "—" : d.MinRequirements;
        DetailReqRecText.Text = string.IsNullOrWhiteSpace(d.RecRequirements) ? "—" : d.RecRequirements;

        if (d.HasLanguageInfo)
        {
            DetailLangText.Text = d.Portuguese != null ? "✓  " + d.Portuguese : "Sem português";
            DetailLangText.Foreground = new SolidColorBrush(d.Portuguese != null
                ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0x9C, 0xA3, 0xAF));
        }
        else DetailLangText.Text = "—";

        DetailReqCard.Visibility = hasReq || d.HasLanguageInfo ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Visualizador das capturas ────────────────────────────────────────────

    private void OpenShot(int index)
    {
        if (_shots.Count == 0) return;
        _shotIndex = ((index % _shots.Count) + _shots.Count) % _shots.Count;

        ShotViewerImage.Source = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_shots[_shotIndex].Full);
            bmp.DecodePixelWidth = 1600;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            ShotViewerImage.Source = bmp;
        }
        catch { /* link inválido */ }

        ShotViewerCounter.Text = $"{_shotIndex + 1} / {_shots.Count}";
        ShotViewerRoot.Visibility = Visibility.Visible;
    }

    private void ShotViewerBg_Click(object sender, MouseButtonEventArgs e) => CloseShotViewer();
    private void ShotViewerImage_Click(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicar na imagem não fecha

    private void CloseShotViewer()
    {
        if (!ViewerOpen) return;
        ShotViewerRoot.Visibility = Visibility.Collapsed;
        _dnShot = Math.Clamp(_shotIndex, 0, Math.Max(0, _shots.Count - 1));
        DetailHighlight();
    }

    /// <summary>A roda do mouse sobre a faixa de miniaturas rola o painel (não fica presa na faixa).</summary>
    private void ShotsScroll_Wheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).Parent is UIElement parent)
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
    }

    private void ShotViewerClose_Click(object sender, RoutedEventArgs e) => CloseShotViewer();
    private void ShotPrev_Click(object sender, RoutedEventArgs e) { NavSounds.Move(); OpenShot(_shotIndex - 1); }
    private void ShotNext_Click(object sender, RoutedEventArgs e) { NavSounds.Move(); OpenShot(_shotIndex + 1); }

    /// <summary>Com o visualizador aberto: ← → trocam de captura e Esc fecha.</summary>
    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (ShotViewerRoot?.Visibility != Visibility.Visible) return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: CloseShotViewer(); e.Handled = true; break;
            case System.Windows.Input.Key.Left: OpenShot(_shotIndex - 1); e.Handled = true; break;
            case System.Windows.Input.Key.Right: OpenShot(_shotIndex + 1); e.Handled = true; break;
        }
    }
 
    private bool KbOpen => KbRoot?.Visibility == Visibility.Visible;
 
    private static readonly string[][] KbLayout =
    {
        new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        new[] { "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
        new[] { "A", "S", "D", "F", "G", "H", "J", "K", "L", "Ç" },
        new[] { "Z", "X", "C", "V", "B", "N", "M", "-", ":", "." },
        new[] { "ESPAÇO", "APAGAR", "LIMPAR", "OK" }
    };
 
    private readonly List<List<Border>> _kbRows = new();
    private int _kbR, _kbC;
 
    private void BuildKeyboard()
    {
        if (_kbRows.Count > 0) return;
        KbKeys.Children.Clear();
 
        static SolidColorBrush Hex(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
 
        for (int r = 0; r < KbLayout.Length; r++)
        {
            bool lastRow = r == KbLayout.Length - 1;
            var keys = KbLayout[r];
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            var rowKeys = new List<Border>();
 
            for (int c = 0; c < keys.Length; c++)
            {
                // Última linha: Espaço largo, os outros médios
                double width = lastRow ? (c == 0 ? 4 : 1.5) : 1;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
 
                string key = keys[c];
                string label = key switch
                {
                    "ESPAÇO" => "Espaço",
                    "APAGAR" => "⌫  Apagar",
                    "LIMPAR" => "Limpar",
                    _ => key
                };
 
                var cap = new TextBlock
                {
                    Text = label,
                    FontSize = lastRow ? 13 : 17,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Hex("#E5E7EB"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
 
                var border = new Border
                {
                    Height = 48,
                    Margin = new Thickness(3, 0, 3, 0),
                    CornerRadius = new CornerRadius(10),
                    Background = key == "OK" ? Hex("#0F6B4D") : Hex("#161824"),
                    BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(2),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Child = cap
                };
                Grid.SetColumn(border, c);
 
                int rr = r, cc = c;
                border.MouseLeftButtonDown += (_, _) =>
                {
                    _kbR = rr; _kbC = cc;
                    KbHighlight();
                    KbPress();
                };
 
                grid.Children.Add(border);
                rowKeys.Add(border);
            }
 
            _kbRows.Add(rowKeys);
            KbKeys.Children.Add(grid);
        }
    }
 
    private void OpenKeyboard()
    {
        BuildKeyboard();
        _kbNameBuf = null;_kbTitleBase = "PESQUISAR"; KbTitle.Text = "PESQUISAR";
        SearchBar.Visibility = Visibility.Collapsed;
        _kbR = 1; _kbC = 0; // começa no Q
        KbRefreshText();
        KbHighlight();
        ClearHighlight();
        KbRoot.Visibility = Visibility.Visible;
        UpdateTopToggles();
        RefreshHints();
    }
 
 
    private void KbOverlay_Click(object sender, MouseButtonEventArgs e) => CloseKeyboard();
 
 
    private void KbPress()
    {
        switch (KbLayout[_kbR][_kbC])
        {
            case "ESPAÇO": KbType(" "); break;
            case "APAGAR": KbBackspace(); break;
            case "LIMPAR":
                if (KbNameMode) _kbNameBuf = ""; else SearchBox.Clear();
                NavSounds.Confirm();
                KbRefreshText();
                break;
            case "OK":
                KbFinish();
                break;
            default:
                KbType(KbLayout[_kbR][_kbC].ToLowerInvariant());
                break;
        }
    }
 
    private void KbMove(Dir dir)
    {
        int r = _kbR, c = _kbC;
        switch (dir)
        {
            case Dir.Left: c = Math.Max(0, c - 1); break;
            case Dir.Right: c = Math.Min(_kbRows[r].Count - 1, c + 1); break;
            default:
                int nr = Math.Clamp(r + (dir == Dir.Down ? 1 : -1), 0, _kbRows.Count - 1);
                if (nr != r)
                {
                    // mantém a posição proporcional (a última linha tem só 4 teclas)
                    double f = _kbRows[r].Count > 1 ? (double)c / (_kbRows[r].Count - 1) : 0;
                    c = (int)Math.Round(f * (_kbRows[nr].Count - 1));
                    r = nr;
                }
                break;
        }
        if (r == _kbR && c == _kbC) return;
        _kbR = r; _kbC = c;
        NavSounds.Move();
        KbHighlight();
    }
 
    private void KbHighlight()
    {
        for (int r = 0; r < _kbRows.Count; r++)
        {
            for (int c = 0; c < _kbRows[r].Count; c++)
            {
                bool on = r == _kbR && c == _kbC;
                var b = _kbRows[r][c];
                b.BorderBrush = on ? HomeAccent : Brushes.Transparent;
                b.RenderTransform = on ? new ScaleTransform(1.06, 1.06) : Transform.Identity;
                Panel.SetZIndex(b, on ? 5 : 0);
            }
        }
    }

    /// <summary>Clicar fora da barra de pesquisa (e fora da lupa) fecha a barra. O filtro continua; a lupa fica acesa.</summary>
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
    
        if (SearchBar.Visibility != Visibility.Visible || KbOpen) return;
        if (IsInside(e.OriginalSource as DependencyObject, SearchBar) ||
            IsInside(e.OriginalSource as DependencyObject, NavSearchButton)) return;
    
        SearchBar.Visibility = Visibility.Collapsed;
        LeaveSearchBox();
        UpdateTopToggles();
        // não marca o evento como tratado: o clique continua valendo (abrir o card, trocar de tela...)
    }
    
    private static bool IsInside(DependencyObject? d, DependencyObject root)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, root)) return true;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }


    // ──────────────────────────────────────────────────────────────
    // Carregamento
    // ──────────────────────────────────────────────────────────────

    private async Task LoadGamesAsync()
    {
        StartupLog.Mark("LoadGamesAsync começou");

        // Primeira abertura: mostra a biblioteca da última vez na hora e atualiza em segundo plano
        var cached = _allGames.Count == 0 ? await Task.Run(LibraryCacheService.Load) : new List<GameEntry>();
        bool showingCache = cached.Count > 0;
        StartupLog.Mark($"cache lido: {cached.Count} jogos");

        try
        {
            if (showingCache)
            {
                _allGames = cached;
                UserDataService.Apply(_allGames);
                PlaySessionService.Apply(_allGames);
                UpdateStats();
                StartupLog.Mark("ApplyFilter (cache) vai começar");
                ApplyFilter(); // lista na tela já; as capas vão aparecendo conforme carregam
                StartupLog.Mark("ApplyFilter (cache) terminou");
                _ = Dispatcher.BeginInvoke(new Action(() => StartupLog.Mark("tela do cache desenhada")),
                    System.Windows.Threading.DispatcherPriority.ContextIdle);

                _localCoversTask = Task.Run(() =>
                {
                    CoverService.LoadLocalCoversSync(cached);
                    StartupLog.Mark("capas locais do cache carregadas");
                });
            }
            else
            {
                if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Visible;
                if (LoadingStatusText != null) LoadingStatusText.Text = "Carregando sua biblioteca de jogos...";
                if (LoadingSubText != null) LoadingSubText.Text = "Sincronizando plataformas...";
            }

            // Varredura de verdade (lê registro, lojas e Galaxy)
            var fresh = await Task.Run(() => _libraryService.LoadAllGames());
            StartupLog.Mark($"varredura terminou: {fresh.Count} jogos");
            UserDataService.Apply(fresh);
            PlaySessionService.Apply(fresh);

            // false = a lista é idêntica à que já está na tela
            bool changed = await Task.Run(() => LibraryCacheService.Save(fresh));
            StartupLog.Mark($"cache gravado (mudou: {changed})");

            if (!showingCache)
            {
                if (LoadingSubText != null) LoadingSubText.Text = "Carregando capas em cache...";
                await Task.Run(() => CoverService.LoadLocalCoversSync(fresh));
                StartupLog.Mark("capas locais carregadas (sem cache)");

                _allGames = fresh;
                UpdateStats();
                ApplyFilter();
                StartupLog.Mark("ApplyFilter (sem cache) terminou");
            }
            else if (changed)
            {
                SwapInLibrary(fresh);
                StartupLog.Mark("ApplyFilter (troca) terminou");
            }

            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;

            _libraryLoaded = true;
            _lastRefresh = DateTime.Now;

            FetchMissingCoversInBackground();
            FetchSteamMetadataInBackground();
            PrefetchBackgroundsInBackground();
            StartupLog.Mark("LoadGamesAsync terminou");
        }
        catch (Exception ex)
        {
            if (LoadingOverlay != null) LoadingOverlay.Visibility = Visibility.Collapsed;

            if (showingCache) Logger.LogError("Atualizar biblioteca em segundo plano", ex);
            else MessageBox.Show($"Erro ao carregar biblioteca: {ex.Message}", "GameHub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    /// <summary>Troca a lista na tela pela nova, reaproveitando as capas já carregadas (não pisca).</summary>
    private void SwapInLibrary(List<GameEntry> fresh)
    {
        var oldByKey = _allGames.GroupBy(g => g.UserKey).ToDictionary(x => x.Key, x => x.First());
        foreach (var game in fresh)
        {
            if (oldByKey.TryGetValue(game.UserKey, out var old) && old.CoverImage != null)
            {
                game.CoverImage = old.CoverImage;
                game.CoverPath = old.CoverPath;
            }
        }

        // Se o painel de detalhe está aberto, ele passa a apontar para o jogo novo equivalente
        var openKey = DetailPanelRoot?.Visibility == Visibility.Visible ? _currentDetailGame?.UserKey : null;

        _allGames = fresh;

        if (openKey != null)
        {
            var same = fresh.FirstOrDefault(g => g.UserKey == openKey);
            if (same != null) _currentDetailGame = same;
        }

        UpdateStats();
        ApplyFilter();

        _localCoversTask = Task.Run(() => CoverService.LoadLocalCoversSync(fresh)); // só as que ainda faltam
    }

    /// <summary>
    /// Ao voltar para o GameHub (depois de instalar/remover um jogo numa loja, ou de fechar um jogo),
    /// refaz a varredura em silêncio. A tela só muda se a lista realmente mudou.
    /// </summary>
    private async Task RefreshLibraryQuietlyAsync()
    {
        if (!_libraryLoaded || _refreshing || _launching) return;
        if (LoadingOverlay?.Visibility == Visibility.Visible) return; // "Recarregar" em andamento
        if (DateTime.Now - _lastRefresh < TimeSpan.FromSeconds(5)) return; // evita varrer a cada alt-tab

        _refreshing = true;
        try
        {
            var fresh = await Task.Run(() => _libraryService.LoadAllGames());
            UserDataService.Apply(fresh);
            PlaySessionService.Apply(fresh);

            bool changed = await Task.Run(() => LibraryCacheService.Save(fresh));
            if (changed)
            {
                SwapInLibrary(fresh);
                FetchMissingCoversInBackground();   // capa do jogo recém-instalado
                FetchSteamMetadataInBackground();
                PrefetchBackgroundsInBackground();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError("Atualização automática da biblioteca", ex);
        }
        finally
        {
            _refreshing = false;
            _lastRefresh = DateTime.Now;
        }
    }

    private void FetchMissingCoversInBackground()
    {
        var pending = _localCoversTask;
        var games = _allGames;
    
        Task.Run(async () =>
        {
            // Espera as capas locais terminarem, para não ler tudo em dobro
            if (pending != null)
            {
                try { await pending; } catch { }
            }
    
            var missing = games.Where(g => g.CoverImage == null).ToList();
            if (missing.Count == 0) return;
    
            await CoverService.LoadAllCoversParallelAsync(missing);
        });
    }

    private void FetchSteamMetadataInBackground()
    {
        // O painel de detalhe lê os campos do GameEntry na hora que você clica no card
        // (não é data-binding ao vivo), então não precisa de nada especial de UI aqui -
        // quando você abrir o painel, se já tiver chegado, já aparece atualizado.
        Task.Run(async () => await SteamStoreMetadataService.EnrichAllAsync(_allGames));
    }

    private bool _bgPrefetching;

    /// <summary>Baixa em silêncio o fundo dos jogos que ainda não têm (só com a chave do SteamGridDB).</summary>
    private void PrefetchBackgroundsInBackground()
    {
        if (_bgPrefetching || _bgFetchCts != null || !BackgroundService.HasSgdbKey) return;
        var games = _allGames.Where(g => !g.IsHidden).ToList();
        _bgPrefetching = true;

        Task.Run(async () =>
        {
            try { await BackgroundService.PrefetchAsync(games, null, CancellationToken.None); }
            catch (Exception ex) { Logger.LogError("Fundos em segundo plano", ex); }
            finally { _bgPrefetching = false; }
        });
    }

    // ── Possíveis duplicados (Ajustes > Duplicados) ──────────────────────────

    private bool _dupDirty;

    private async void DuplicatesButton_Click(object sender, RoutedEventArgs e)
    {
        var games = _allGames.Where(g => !g.IsHidden).ToList();
        var list = await Task.Run(() => TitleAliasService.FindCandidates(games));
        BuildDuplicatesList(list);
        DuplicatesRoot.Visibility = Visibility.Visible;
        RefreshHints();
    }

    private void BuildDuplicatesList(List<DuplicateCandidate> list)
    {
        DuplicatesList.Children.Clear();

        void UpdateHint() => DuplicatesHint.Text = DuplicatesList.Children.Count == 0
            ? "Nenhum duplicado provável encontrado."
            : "Mesmo jogo em lojas diferentes com nomes parecidos. \"Juntar\" faz os dois virarem um card só.";

        static SolidColorBrush Hex(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

        FrameworkElement Line(GameEntry g)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            line.Children.Add(new TextBlock
            {
                Text = g.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hex("#E5E7EB"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340
            });
            foreach (var badge in g.PlatformBadges)
                line.Children.Add(BuildPlatformChip(badge, small: true));
            return line;
        }

        foreach (var cand in list)
        {
            var c = cand;
            var row = new Border
            {
                Background = Hex("#161824"), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 10, 12, 10), Margin = new Thickness(0, 0, 0, 10),
                BorderBrush = Hex("#232636"), BorderThickness = new Thickness(1)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var names = new StackPanel();
            names.Children.Add(Line(c.A));
            names.Children.Add(Line(c.B));
            grid.Children.Add(names);

            Button Pill(string text, string bg, string fg)
                => new()
                {
                    Content = text, Style = (Style)FindResource("TopChipStyle"),
                    Background = Hex(bg), Foreground = Hex(fg), FontSize = 12,
                    Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 0, 0, 0)
                };

            var join = Pill("Juntar", "#10B981", "#FFFFFF");
            join.Click += (_, _) =>
            {
                // Chaves recalculadas agora: uma junção anterior pode ter mudado a chave de um dos dois
                TitleAliasService.AddAlias(TitleAliasService.GroupKey(c.B.Name), TitleAliasService.GroupKey(c.A.Name));
                _dupDirty = true;
                DuplicatesList.Children.Remove(row);
                UpdateHint();
            };
            var diff = Pill("São diferentes", "#1F2233", "#D1D5DB");
            diff.Click += (_, _) =>
            {
                TitleAliasService.Ignore(c.KeyA, c.KeyB);
                DuplicatesList.Children.Remove(row);
                UpdateHint();
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            buttons.Children.Add(join);
            buttons.Children.Add(diff);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            row.Child = grid;
            DuplicatesList.Children.Add(row);
        }
        UpdateHint();
    }

    private void DuplicatesClose_Click(object sender, RoutedEventArgs e) => CloseDuplicates();
    private void DuplicatesOverlay_Click(object sender, MouseButtonEventArgs e) => CloseDuplicates();

    private void CloseDuplicates()
    {
        DuplicatesRoot.Visibility = Visibility.Collapsed;
        RefreshHints();
        // Se juntou algum par, refaz a biblioteca para os cards se fundirem
        if (_dupDirty) { _dupDirty = false; _ = LoadGamesAsync(); }
    }

        // ── Estatísticas (card na Início) ───────────────────────────────────────

    /// <summary>Minutos por plataforma de um jogo (usa o total solto se não houver o detalhe por loja).</summary>
    private static Dictionary<Platform, int> PlaytimeOf(GameEntry g)
    {
        var d = g.PlaytimeByPlatform.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (d.Count == 0 && g.PlaytimeMinutes > 0) d[g.Platform] = g.PlaytimeMinutes;
        return d;
    }

    private static string FormatHours(long minutes)
        => (minutes / 60).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("pt-BR")) + " h";

    private long TotalPlayMinutes() => _allGames.Sum(g => (long)PlaytimeOf(g).Values.Sum());

    private static string StoreName(Platform p) => p switch
    {
        Platform.Steam => "Steam",
        Platform.Epic => "Epic Games",
        Platform.Gog => "GOG",
        Platform.EaApp => "EA App",
        Platform.Ubisoft => "Ubisoft",
        Platform.Emulador => "Emuladores",
        Platform.Modificado => "Modificados",
        _ => p.ToString()
    };

    private void OpenStats()
    {
        BuildStats();
        StatsRoot.Visibility = Visibility.Visible;
        RefreshHints();
    }

    private void StatsClose_Click(object sender, RoutedEventArgs e) => CloseStats();
    private void StatsOverlay_Click(object sender, MouseButtonEventArgs e) => CloseStats();

    private void CloseStats()
    {
        StatsRoot.Visibility = Visibility.Collapsed;
        RefreshHints();
    }

    private void BuildStats()
    {
        static SolidColorBrush Hex(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

        FrameworkElement Bar(double ratio, string color)
        {
            ratio = Math.Clamp(ratio, 0.02, 1.0);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - ratio, GridUnitType.Star) });
            grid.Children.Add(new Border { Background = Hex(color), CornerRadius = new CornerRadius(4) });
            return new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = Hex("#232636"), Child = grid };
        }

        var withTime = _allGames.Select(g => (Game: g, Minutes: PlaytimeOf(g))).Where(x => x.Minutes.Count > 0).ToList();
        long total = withTime.Sum(x => (long)x.Minutes.Values.Sum());
        StatsSummary.Text = $"{FormatHours(total)} jogadas  ·  {withTime.Count} jogos com tempo registrado";

        // ── Mais jogados ──
        StatsTopList.Children.Clear();
        var top = _allGames.Where(g => g.SortPlaytime > 0).OrderByDescending(g => g.SortPlaytime).Take(10).ToList();
        int topMax = top.Count > 0 ? top[0].SortPlaytime : 1;

        for (int i = 0; i < top.Count; i++)
        {
            var g = top[i];
            var row = new Border
            {
                Background = Hex("#161824"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 16, 8),
                Margin = new Thickness(0, 0, 0, 8), Cursor = System.Windows.Input.Cursors.Hand,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1.5)
            };
            row.MouseEnter += (_, _) => row.BorderBrush = HomeAccent;
            row.MouseLeave += (_, _) => row.BorderBrush = Brushes.Transparent;
            var game = g;
            row.MouseLeftButtonDown += (_, _) =>
            {
                NavSounds.Confirm();
                CloseStats();
                OpenDetailPanel(game);
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(), FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Hex("#6B7280"), VerticalAlignment = VerticalAlignment.Center
            });

            var cover = new Border
            {
                Width = 32, Height = 44, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Background = Hex("#0D0F1A"), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new Image { Source = g.CoverImage, Stretch = Stretch.UniformToFill }
            };
            cover.Clip = new RectangleGeometry(new Rect(0, 0, 32, 44), 6, 6);
            Grid.SetColumn(cover, 1);
            grid.Children.Add(cover);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            mid.Children.Add(new TextBlock
            {
                Text = g.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hex("#E5E7EB"),
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6)
            });
            mid.Children.Add(Bar((double)g.SortPlaytime / topMax, "#10B981"));
            Grid.SetColumn(mid, 2);
            grid.Children.Add(mid);

            var hours = new TextBlock
            {
                Text = FormatHours(g.SortPlaytime), FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hours, 3);
            grid.Children.Add(hours);

            row.Child = grid;
            StatsTopList.Children.Add(row);
        }
        if (top.Count == 0)
            StatsTopList.Children.Add(new TextBlock { Text = "Nenhum tempo de jogo registrado ainda.", FontSize = 13, Foreground = Hex("#9CA3AF") });

        // ── Horas por loja (cada loja soma só o que foi jogado nela) ──
        StatsStoresList.Children.Clear();
        var perStore = withTime
            .SelectMany(x => x.Minutes.Select(kv => (Platform: kv.Key, Minutes: kv.Value)))
            .GroupBy(x => x.Platform)
            .Select(gr => (Platform: gr.Key, Minutes: gr.Sum(x => (long)x.Minutes), Games: gr.Count()))
            .OrderByDescending(x => x.Minutes)
            .ToList();
        long storeMax = perStore.Count > 0 ? perStore[0].Minutes : 1;

        foreach (var s in perStore)
        {
            var row = new Border
            {
                Background = Hex("#161824"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 16, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });

            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var iconPath = PlatformStyle.IconPath(s.Platform);
            if (!string.IsNullOrEmpty(iconPath))
                label.Children.Add(BuildPlatformChip(new PlatformBadge
                {
                    Letter = PlatformStyle.Letter(s.Platform), ColorHex = PlatformStyle.ColorHex(s.Platform), IconPath = iconPath
                }, small: true));
            label.Children.Add(new TextBlock
            {
                Text = StoreName(s.Platform), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hex("#E5E7EB"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6)
            });
            grid.Children.Add(label);

            var bar = Bar((double)s.Minutes / storeMax, "#3B82F6");
            bar.VerticalAlignment = VerticalAlignment.Center;
            bar.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);

            var info = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = FormatHours(s.Minutes), FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right
            });
            info.Children.Add(new TextBlock
            {
                Text = s.Games == 1 ? "1 jogo" : $"{s.Games} jogos", FontSize = 11, Foreground = Hex("#9CA3AF"),
                HorizontalAlignment = HorizontalAlignment.Right
            });
            Grid.SetColumn(info, 2);
            grid.Children.Add(info);

            row.Child = grid;
            StatsStoresList.Children.Add(row);
        }
        if (perStore.Count == 0)
            StatsStoresList.Children.Add(new TextBlock { Text = "Sem horas registradas por loja ainda.", FontSize = 13, Foreground = Hex("#9CA3AF") });
    }

    // ──────────────────────────────────────────────────────────────
    // Stats e Filtros
    // ──────────────────────────────────────────────────────────────

    private void UpdateStats()
    {
        var installedCount = _allGames.Count(g => g.IsInstalled && !g.IsHidden);
        var acquiredCount = _allGames.Count(g => !g.IsInstalled && !g.IsHidden);
        TotalInstalledText.Text = installedCount.ToString();
        TotalAcquiredText.Text = acquiredCount.ToString();
        ScAcqCount.Text = acquiredCount.ToString();
        ScStatsHours.Text = FormatHours(TotalPlayMinutes());
    }

    private void ApplyFilter()
    {
        if (_allGames == null) return;
        UpdateTopNav();
        bool isHome = _selectedPlatform == "Início";
        if (HomeView != null) HomeView.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        if (MainScrollViewer != null) MainScrollViewer.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        if (GridBanner != null) GridBanner.Visibility = isHome ? Visibility.Collapsed : Visibility.Visible;
        if (isHome)
        {
            bool entering = _fadeScreen != "Início";   // veio de outra tela (não é só um refresh)
            _fadeScreen = "Início";
            BuildHome();
            if (entering) ScrollHomeToFocus();
            if (entering && HomeView != null)
            {
                var homeFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop };
                Timeline.SetDesiredFrameRate(homeFade, 30);
                HomeView.BeginAnimation(UIElement.OpacityProperty, homeFade);
            }
            return;
        }
        ResetBgLayout();
        if (GridBannerTitle != null && GridBannerSub != null)
        {
            GridBannerTitle.Text = _selectedPlatform == "Emuladores" && _selectedEmulatorSystem != null
                ? _selectedEmulatorSystem
                    : IsCollectionTab(_selectedPlatform) ? CollectionName(_selectedPlatform) : _selectedPlatform;
            GridBannerSub.Text = "";
        }
        if (DynamicBgImage.Opacity > 0.2 || BgTint.Visibility == Visibility.Visible)
        {
            DynamicBgImage.Opacity = 0.18;
            DynamicBgImage.Source = null;
            BgTint.Visibility = Visibility.Collapsed;
            BgShade.Opacity = 1.0;
            _bgSource = null; // força o fundo pequeno de volta no próximo foco
        }

        var searchText = SearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
        IEnumerable<GameEntry> filtered = _allGames;

        switch (_selectedPlatform)
        {
            case string s when IsCollectionTab(s):
                filtered = filtered.Where(g => CollectionsService.Contains(CollectionName(s), g));
                break;
            case "Favoritos":
                filtered = filtered.Where(g => g.IsFavorite);
                break;
            case "Steam":
                filtered = filtered.Where(g => g.Platforms.Contains(Platform.Steam) || g.Platform == Platform.Steam);
                break;
            case "Epic Games":
                filtered = filtered.Where(g => g.Platforms.Contains(Platform.Epic) || g.Platform == Platform.Epic);
                break;
            case "GOG":
                filtered = filtered.Where(g => g.Platforms.Contains(Platform.Gog) || g.Platform == Platform.Gog);
                break;
            case "EA App":
                filtered = filtered.Where(g => g.Platforms.Contains(Platform.EaApp) || g.Platform == Platform.EaApp);
                break;
            case "Ubisoft":
                filtered = filtered.Where(g => g.Platforms.Contains(Platform.Ubisoft) || g.Platform == Platform.Ubisoft);
                break;
            case "Emuladores":
                filtered = filtered.Where(g => g.Platform == Platform.Emulador);
                break;
            case "Modificados":
                filtered = filtered.Where(g => g.Platform == Platform.Modificado);
                break;
            default: // "Todos", "Instalados" e "Adquiridos": jogos de loja (ROMs e Modificados ficam nas abas deles)
                filtered = filtered.Where(g => g.Platform != Platform.Emulador && g.Platform != Platform.Modificado);
                break;
        }

        // Ocultos só aparecem quando o botão "Ocultos" está ligado (e aí só eles aparecem)
        filtered = filtered.Where(g => g.IsHidden == _showHidden);

        if (!string.IsNullOrWhiteSpace(searchText))
            filtered = SearchAliasService.Filter(filtered, searchText);

        // "Instalado" precisa ser checado NA PLATAFORMA DA ABA ATUAL, não globalmente -
        // senão um jogo instalado só na Steam aparecia como instalado também na aba da Epic
        // (bastava constar em Platforms, sem checar InstalledPlatforms daquela loja específica).
        bool IsInstalledForCurrentTab(GameEntry g) => _selectedPlatform switch
        {
            "Steam" => g.IsInstalledOn(Platform.Steam),
            "Epic Games" => g.IsInstalledOn(Platform.Epic),
            "GOG" => g.IsInstalledOn(Platform.Gog),
            "EA App" => g.IsInstalledOn(Platform.EaApp),
            "Ubisoft" => g.IsInstalledOn(Platform.Ubisoft),
            "Emuladores" => true, // ROM só existe na lista se o arquivo foi encontrado - sempre "instalado"
            _ => g.IsInstalled // "Todos": instalado em qualquer lugar conta
        };

        var installedList = SortGames(filtered.Where(IsInstalledForCurrentTab));
        var acquiredList = SortGames(filtered.Where(g => !IsInstalledForCurrentTab(g)));
        // Telas separadas: cada uma monta só a sua lista
        bool onlyInstalled = _selectedPlatform == "Instalados";
        bool onlyAcquired = _selectedPlatform == "Adquiridos";
        if (onlyInstalled) acquiredList = new List<GameEntry>();
        if (onlyAcquired) installedList = new List<GameEntry>();

        bool isEmulatorTab = _selectedPlatform == "Emuladores";
        bool isCustomTab = _selectedPlatform == "Modificados";


        // ROM e jogo personalizado não têm conceito de "possuído mas não instalado" -
        // some a seção pra não mostrar vazia à toa
        if (AcquiredSection != null)
            AcquiredSection.Visibility = (isEmulatorTab || isCustomTab || onlyInstalled) ? Visibility.Collapsed : Visibility.Visible;

        // Na aba Emuladores usamos a lista agrupada por console em vez da lista plana normal,
        // para não misturar consoles diferentes (ex.: N64 e PS2) no mesmo grid.
        if (InstalledSection != null)
            InstalledSection.Visibility = (isEmulatorTab || onlyAcquired) ? Visibility.Collapsed : Visibility.Visible;
        if (EmulatorSection != null)
            EmulatorSection.Visibility = isEmulatorTab ? Visibility.Visible : Visibility.Collapsed;

        if (AddCustomGameButton != null)
            AddCustomGameButton.Visibility = isCustomTab && !_controllerMode ? Visibility.Visible : Visibility.Collapsed;
        if (InstalledHeaderTitle != null)
            InstalledHeaderTitle.Text = isCustomTab ? "⚙️ JOGOS MODIFICADOS" : "🎮 JOGOS INSTALADOS";
                var version = ++_filterVersion;

        if (isEmulatorTab)
        {
            BuildEmulatorSections(installedList);
        }
        else
        {
            if (InstalledHeaderCount != null) InstalledHeaderCount.Text = $"({installedList.Count})";
            FillInChunks(InstalledGamesList, installedList, version);
        }

        if (AcquiredHeaderCount != null) AcquiredHeaderCount.Text = $"({acquiredList.Count})";
        FillInChunks(AcquiredGamesList, acquiredList, version);

        // Ao trocar de tela (ou de console dentro de Emuladores): volta ao topo e faz um fade curto.
        // Não repete a cada letra digitada na busca.
        string screenKey = _selectedPlatform + "|" + _selectedEmulatorSystem;
        if (MainScrollViewer != null && _fadeScreen != screenKey)
        {
            _fadeScreen = screenKey;
            MainScrollViewer.ScrollToVerticalOffset(0);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            Timeline.SetDesiredFrameRate(fade, 30);
            MainScrollViewer.BeginAnimation(UIElement.OpacityProperty, fade);
                        if (!_controllerMode)
            {
                IEnumerable<GameEntry> artPool = installedList.Concat(acquiredList);
                if (isEmulatorTab && _selectedEmulatorSystem != null)
                    artPool = artPool.Where(g => EmuSystemOf(g) == _selectedEmulatorSystem);
                _ = ApplyDefaultListBackgroundAsync(screenKey, artPool.ToList());
            }
        }

        if (!isEmulatorTab)
            RebuildNavTargets(installedList, isCustomTab ? new List<GameEntry>() : acquiredList);
    }

        private readonly Dictionary<string, string> _listArtSession = new();

    /// <summary>Ao abrir uma tela de lista no modo mouse, já mostra a arte de um jogo (aleatório, fixo na sessão).</summary>
    private async Task ApplyDefaultListBackgroundAsync(string screenKey, List<GameEntry> games)
    {
        if (games.Count == 0) return;
        int v0 = _homeBgVersion;

        GameEntry? pick = null;
        if (_listArtSession.TryGetValue(screenKey, out var uk))
            pick = games.FirstOrDefault(g => g.UserKey == uk);

        if (pick == null)
        {
            // Sorteia alguns jogos e fica com o primeiro que tem arte de fundo
            foreach (var g in games.OrderBy(_ => _artRandom.Next()).Take(8))
            {
                BitmapSource? bmp = null;
                try { bmp = await BackgroundService.GetAsync(g); } catch { }
                if (screenKey != _fadeScreen || v0 != _homeBgVersion) return; // mudou de tela ou o mouse já escolheu outro
                if (bmp != null) { pick = g; break; }
            }
            if (pick == null) return;
            _listArtSession[screenKey] = pick.UserKey;
        }

        if (screenKey != _fadeScreen || v0 != _homeBgVersion) return;
        _ = ApplyHomeBackgroundAsync(pick);
    }

    private string? _fadeScreen;

    /// <summary>Mostra os primeiros cards na hora e entrega o resto em lotes, sem travar a tela.</summary>
    private void FillInChunks(ItemsControl? list, List<GameEntry> items, int version)
    {
        if (list == null) return;
        const int first = 40, chunk = 30;
        if (items.Count <= first) { list.ItemsSource = items; return; }

        var shown = new System.Collections.ObjectModel.ObservableCollection<GameEntry>(items.Take(first));
        list.ItemsSource = shown;
        int next = first;

        void Step()
        {
            // Outro filtro ou tela entrou no meio: este preenchimento para
            if (version != _filterVersion || !ReferenceEquals(list.ItemsSource, shown)) return;
            int end = Math.Min(next + chunk, items.Count);
            for (; next < end; next++) shown.Add(items[next]);
            if (next < items.Count)
                Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.Background);
        }
        Dispatcher.BeginInvoke(new Action(Step), DispatcherPriority.Background);
    }

    /// <summary>
    /// Aba Emuladores tem 2 telas: primeiro escolhe o console (cards grandes), depois
    /// vê só os jogos daquele console - evita uma lista enorme com tudo empilhado.
    /// </summary>
    private void BuildEmulatorSections(List<GameEntry> emulatorGames)
    {
        if (EmulatorSection == null) return;
        EmulatorSection.Children.Clear();
        _navTargets = new();

        var gameGroups = emulatorGames
            .GroupBy(g => string.IsNullOrWhiteSpace(g.EmulatorSystem) ? "Outros" : g.EmulatorSystem)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).ToList());

        // Todo sistema do emulators.json entra no seletor, mesmo com 0 jogo ainda -
        // assim dá pra abrir a pasta e usar "Adicionar ROM" mesmo estando vazio.
        var allSystems = _emulatorScannerInfo.GetConfiguredSystems()
            .OrderBy(s => s.DisplayName)
            .ToList();

        if (_selectedEmulatorSystem == null || allSystems.All(s => s.DisplayName != _selectedEmulatorSystem))
        {
            _selectedEmulatorSystem = null;
            BuildEmulatorConsolePicker(allSystems, gameGroups);
        }
        else
        {
            var system = allSystems.First(s => s.DisplayName == _selectedEmulatorSystem);
            var games = gameGroups.TryGetValue(system.DisplayName, out var list) ? list : new List<GameEntry>();
            BuildEmulatorConsoleGames(system, games);
        }
        ScheduleHighlight();
    }

    private void BuildEmulatorConsolePicker(List<EmulatorScanner.SystemInfo> systems, Dictionary<string, List<GameEntry>> gameGroups)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "ESCOLHA O CONSOLE",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = HomeAccent,
            Margin = new Thickness(0, 0, 0, 16)
        });

        static SolidColorBrush Solid(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
        var green      = Solid(0x10, 0xB9, 0x81);
        var borderIdle = Solid(0x2A, 0x2D, 0x3A);
        var gray       = Solid(0xB6, 0xBC, 0xC8);
        var emptyBg    = Solid(0x16, 0x18, 0x24);
        var darkEnd    = Color.FromRgb(0x12, 0x14, 0x1F);

        // Escurecido por cima da arte, para o texto continuar legível
        var shadeBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        shadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x99, 0, 0, 0), 0.0));
        shadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0, 0, 0), 0.45));
        shadeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0xD0, 0, 0, 0), 1.0));
        shadeBrush.Freeze();

        int CountOf(EmulatorScanner.SystemInfo s)
            => gameGroups.TryGetValue(s.DisplayName, out var l) ? l.Count : 0;

        // Consoles com jogo primeiro, os vazios no fim (cada grupo em ordem alfabética)
        var ordered = systems.OrderBy(s => CountOf(s) == 0).ThenBy(s => s.DisplayName).ToList();

        var wrap = new WrapPanel();
        foreach (var system in ordered)
        {
            var count = CountOf(system);
            var hasGames = count > 0;
            var accent = AccentFor(system.DisplayName);

            Brush bg = emptyBg;
            Brush idleBorder = borderIdle;
            if (hasGames)
            {
                var lg = new LinearGradientBrush(accent, darkEnd, 45);
                lg.Freeze();
                bg = lg;
                var edge = new SolidColorBrush(Color.FromRgb(
                    (byte)Math.Min(255, accent.R + 60), (byte)Math.Min(255, accent.G + 60), (byte)Math.Min(255, accent.B + 60)));
                edge.Freeze();
                idleBorder = edge;
            }

            var tile = new Border
            {
                Width = 320,
                Height = 118,
                Background = bg,
                BorderBrush = idleBorder,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 14, 14),
                Cursor = System.Windows.Input.Cursors.Hand,
                Opacity = hasGames ? 1.0 : 0.6
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Margin = new Thickness(16, 14, 16, 12);

            // Ícone de monitor apagado ao fundo
            var deco = new TextBlock
            {
                Text = "\uE7F4",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 58,
                Foreground = Brushes.White,
                Opacity = 0.10,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -6, 0),
                IsHitTestVisible = false
            };
            Grid.SetRowSpan(deco, 2);
            grid.Children.Add(deco);

            var name = new TextBlock
            {
                Text = system.DisplayName,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetRow(name, 0);
            grid.Children.Add(name);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            if (hasGames)
            {
                footer.Children.Add(new TextBlock
                {
                    Text = count.ToString(), FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Brushes.White
                });
                footer.Children.Add(new TextBlock
                {
                    Text = count == 1 ? "jogo" : "jogos", FontSize = 12, Foreground = gray,
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6, 0, 0, 7)
                });
            }
            else
            {
                footer.Children.Add(new TextBlock { Text = "sem jogos ainda", FontSize = 12, Foreground = gray });
            }
            Grid.SetRow(footer, 1);
            grid.Children.Add(footer);
                        var outer = new Grid();
            if (hasGames && gameGroups.TryGetValue(system.DisplayName, out var consoleGames))
            {
                var art = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, IsHitTestVisible = false };
                var shade = new Border { Background = shadeBrush, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                outer.Children.Add(art);
                outer.Children.Add(shade);
                AttachCardMenu(tile, "console:" + system.DisplayName, art, shade, () => consoleGames);
                _ = LoadCardArtAsync("console:" + system.DisplayName, tile, art, shade, consoleGames);
            }
            outer.Children.Add(grid);
            tile.Child = outer;
            outer.Clip = new RectangleGeometry(new Rect(0, 0, tile.Width - 3, tile.Height - 3), 10.5, 10.5);

            tile.MouseEnter += (_, _) =>
            {
                tile.BorderBrush = green;
                tile.Opacity = 1.0;
                var st = new ScaleTransform(1, 1);
                tile.RenderTransformOrigin = new Point(0.5, 0.5);
                tile.RenderTransform = st;
                var grow = new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(140));
                st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                Panel.SetZIndex(tile, 5);
            };
            tile.MouseLeave += (_, _) =>
            {
                tile.BorderBrush = idleBorder;
                tile.Opacity = hasGames ? 1.0 : 0.6;
                if (tile.RenderTransform is ScaleTransform st)
                {
                    var back = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140));
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, back);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, back);
                }
                Panel.SetZIndex(tile, 0);
            };

            var systemKey = system.DisplayName;
            tile.MouseLeftButtonDown += (_, _) =>
            {
                _selectedEmulatorSystem = systemKey;
                ApplyFilter();
            };
            _navTargets.Add(new NavTarget
            {
                Element = () => tile,
                Activate = () => { _selectedEmulatorSystem = systemKey; _navIndex = 0; ApplyFilter(); }
            });

            wrap.Children.Add(tile);
        }

        stack.Children.Add(wrap);
        EmulatorSection.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x11, 0x13, 0x1C)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Child = stack
        });
    }

    private static readonly Random _artRandom = new();
    private readonly Dictionary<string, string> _cardArtSession = new(); // sorteio de cada card nesta abertura

    /// <summary>Qual card a arte do jogo representa: console (ROM), Modificados, Instalados ou Adquiridos.</summary>
    private static string CardKeyFor(GameEntry g)
    {
        if (g.Platform == Platform.Emulador) return "console:" + EmuSystemOf(g);
        if (g.Platform == Platform.Modificado) return "shortcut:Modificados";
        return g.IsInstalled ? "shortcut:Instalados" : "shortcut:Adquiridos";
    }

        private static string CardLabel(string cardKey) => cardKey[(cardKey.IndexOf(':') + 1)..];

    /// <summary>Arte do card: o jogo fixado pelo usuário; senão um sorteado (o mesmo até fechar o app).</summary>
    private async Task LoadCardArtAsync(string cardKey, FrameworkElement tile, Image art, Border shade, List<GameEntry> games)
    {
        try
        {
            if (games.Count == 0) return;
            var tries = new List<GameEntry>();

            var pinnedKey = CardArtService.Get(cardKey);
            var pinned = pinnedKey == null ? null : games.FirstOrDefault(g => g.UserKey == pinnedKey);
            if (pinned != null) tries.Add(pinned);
            else if (_cardArtSession.TryGetValue(cardKey, out var sk))
            {
                var same = games.FirstOrDefault(g => g.UserKey == sk);
                if (same != null) tries.Add(same);
            }

            // Reserva: outros jogos em ordem aleatória, caso o primeiro não tenha arte
            var others = games.Where(g => !tries.Contains(g)).OrderBy(_ => _artRandom.Next()).Take(6).ToList();
            tries.AddRange(others);

            foreach (var g in tries)
            {
                var bmp = await BackgroundService.GetAsync(g);
                if (bmp == null || bmp.PixelWidth <= 0) continue;

                // Reduz para o tamanho do card, para não segurar a imagem grande na memória
                double s = 640.0 / bmp.PixelWidth;
                var cached = new CachedBitmap(new TransformedBitmap(bmp, new ScaleTransform(s, s)),
                                              BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                cached.Freeze();

                bool isPinned = ReferenceEquals(g, pinned);
                if (!isPinned) _cardArtSession[cardKey] = g.UserKey;
                tile.ToolTip = (isPinned ? "Arte fixada: " : "Arte: ") + g.Name + "  (botão direito para mudar)";

                art.Source = cached;
                shade.Visibility = Visibility.Visible;
                art.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.75, TimeSpan.FromMilliseconds(300)));
                return;
            }
        }
        catch (Exception ex) { Logger.LogError("Arte do card", ex); }
    }

    /// <summary>Menu do botão direito no card: ver o que está fixado, desafixar ou sortear outro.</summary>
        /// <summary>Menu do botão direito no card: ver o que está fixado, desafixar ou sortear outro.</summary>
    private void AttachCardMenu(FrameworkElement tile, string cardKey, Image art, Border shade, Func<List<GameEntry>> games)
    {
        MenuItem Item(string header, bool enabled = true) => new()
        {
            Header = header,
            IsEnabled = enabled,
            Style = (Style)FindResource("DarkMenuItemStyle")
        };

        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle") };
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            var pinnedKey = CardArtService.Get(cardKey);
            var pinnedGame = pinnedKey == null ? null : _allGames.FirstOrDefault(g => g.UserKey == pinnedKey);

            if (pinnedGame != null)
            {
                menu.Items.Add(Item($"Arte fixada: {pinnedGame.Name}", false));
                menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparatorStyle") });
                var unpin = Item("Desafixar (voltar ao aleatório)");
                unpin.Click += (_, _) =>
                {
                    CardArtService.Clear(cardKey);
                    _cardArtSession.Remove(cardKey);
                    _ = LoadCardArtAsync(cardKey, tile, art, shade, games());
                };
                menu.Items.Add(unpin);
            }
            else
            {
                menu.Items.Add(Item("Arte aleatória", false));
                menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparatorStyle") });
                var reroll = Item("Sortear outro jogo");
                reroll.Click += (_, _) =>
                {
                    _cardArtSession.Remove(cardKey);
                    _ = LoadCardArtAsync(cardKey, tile, art, shade, games());
                };
                menu.Items.Add(reroll);
            }
        };
        tile.ContextMenu = menu;
    }

        // ── Menu do botão direito nos cards de jogo ──────────────────────────────

        private void GameCard_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true; // o menu é aberto aqui, senão o 1º clique direito só "prepara" o menu
        if (sender is not FrameworkElement { Tag: GameEntry game } fe) return;
        var menu = BuildGameMenu(game);
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

        private Platform? ContextPlatform()
    {
        string tab = IsOnHome ? _homeFilter : _selectedPlatform;
        return tab switch
        {
            "Steam" => Platform.Steam,
            "Epic Games" => Platform.Epic,
            "GOG" => Platform.Gog,
            "EA App" => Platform.EaApp,
            "Ubisoft" => Platform.Ubisoft,
            _ => null
        };
    }

    private ContextMenu BuildGameMenu(GameEntry game)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle") };

        void Add(string header, Action action)
        {
            var mi = new MenuItem { Header = header, Style = (Style)FindResource("DarkMenuItemStyle") };
            mi.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) { Logger.LogError("Menu do jogo", ex); ShowToast("Não consegui abrir isso"); }
            };
            menu.Items.Add(mi);
        }

        static string? DirOfFile(string? file)
            => !string.IsNullOrWhiteSpace(file) && !file.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) && File.Exists(file)
                ? Path.GetDirectoryName(file) : null;

        // "common" sozinha é a pasta da biblioteca da Steam (cache antigo), não a do jogo
        static bool IsLibraryRoot(string p)
            => string.Equals(Path.GetFileName(p.TrimEnd('\\', '/')), "common", StringComparison.OrdinalIgnoreCase);

        string? mainPath = null;

        if (!string.IsNullOrWhiteSpace(game.RomFilePath) && File.Exists(game.RomFilePath))
        {
            mainPath = game.RomFilePath;
            Add("Mostrar ROM no Explorer", () => Process.Start("explorer.exe", $"/select,\"{game.RomFilePath}\""));
            var emuDir = DirOfFile(game.LaunchExecutable);
            if (emuDir != null)
                Add("Abrir pasta do emulador", () => Process.Start("explorer.exe", $"\"{emuDir}\""));
        }
        else if (game.IsInstalled)
        {
            // Numa aba de loja, só oferece a pasta se o jogo está instalado NAQUELA loja
            var tabPlatform = ContextPlatform();
            bool wrongStore = tabPlatform != null && game.Platform != tabPlatform;

            if (!wrongStore)
            {
                string? gameDir = !string.IsNullOrWhiteSpace(game.InstallPath)
                                  && Directory.Exists(game.InstallPath) && !IsLibraryRoot(game.InstallPath)
                    ? game.InstallPath : DirOfFile(game.LaunchExecutable);
                if (gameDir != null)
                {
                    mainPath = gameDir;
                    Add("Abrir pasta do jogo", () => Process.Start("explorer.exe", $"\"{gameDir}\""));
                }
            }
        }

        if (mainPath != null)
            Add("Copiar caminho", () => { Clipboard.SetText(mainPath); ShowToast("Caminho copiado"); });

        AddCollectionItems(menu, game);

        return menu;
    }

    private const string ColPrefix = "col:";
    private static bool IsCollectionTab(string? s) => s != null && s.StartsWith(ColPrefix, StringComparison.Ordinal);
    private static string CollectionName(string tab) => tab.Substring(ColPrefix.Length);
 
    /// <summary>Recria os chips da barra Coleções e as abas escondidas de cada coleção.</summary>
    private void RefreshCollections()
    {
        CollectionChips.Children.Clear();
 
        foreach (var rb in PlatformTabs.Children.OfType<RadioButton>()
                     .Where(r => IsCollectionTab(r.Content?.ToString())).ToList())
            PlatformTabs.Children.Remove(rb);
 
        var chipStyle = (Style)FindResource("TopChipStyle");
 
        foreach (var name in CollectionsService.Names())
        {
            var tab = ColPrefix + name;
 
            var chip = new Button
            {
                Content = name,
                Tag = tab,
                Style = chipStyle,
                Margin = new Thickness(0, 0, 6, 6),
                ContextMenu = BuildCollectionChipMenu(name)
            };
            chip.Click += CollectionChip_Click;
            CollectionChips.Children.Add(chip);
 
            var rb = new RadioButton
            {
                Content = tab,
                Style = (Style)FindResource("PlatformTabButtonStyle")
            };
            rb.Checked += PlatformTab_Checked;
            PlatformTabs.Children.Add(rb);
        }
 
        // Criar uma coleção vazia direto da barra
        var add = new Button
        {
            Content = "+  Nova coleção",
            Tag = ColPrefix + "\u0001new",
            Style = chipStyle,
            Margin = new Thickness(0, 0, 6, 6)
        };
        add.Click += CollectionChip_Click;
        CollectionChips.Children.Add(add);
 
        UpdateCollectionChipsHighlight();
        ApplyControllerOnlyVisibility();
    }
        private void UpdateCollectionChipsHighlight()
    {
        if (CollectionChips == null) return;
        string key = IsOnHome ? _homeFilter : _selectedPlatform;
        foreach (var b in CollectionChips.Children.OfType<Button>())
        {
            bool active = (b.Tag as string) == key;
            b.Background = active ? HomeAccent : Brushes.Transparent;
            b.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
        }
    }
 
    private void NavCollections_Click(object sender, RoutedEventArgs e)
    {
        NavSounds.Tab();
        bool open = CollectionChipsBar.Visibility != Visibility.Visible;
        CollectionChipsBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            TopChipsBar.Visibility = Visibility.Collapsed;
            EmuChipsBar.Visibility = Visibility.Collapsed;
            SettingsBar.Visibility = Visibility.Collapsed;
        }
        UpdateTopToggles();
    }
 
    private void CollectionChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && tag == ColPrefix + "\u0001new")
        {
            NewEmptyCollection();
            return;
        }
        TopChip_Click(sender, e); // abre a coleção (na Início filtra a fileira; nas outras telas abre a lista)
    }
 
    private void NewEmptyCollection()
    {
        var name = PromptName("Nova coleção", "", n => !CollectionsService.Exists(n));
        if (name == null || !CollectionsService.Create(name)) return;
        NavSounds.Confirm();
        RefreshCollections(); // a barra continua aberta, já com a coleção nova
    }


    private ContextMenu BuildCollectionChipMenu(string name)
    {
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");
        var menu = new ContextMenu { Style = (Style)FindResource("DarkContextMenuStyle") };

        var rename = new MenuItem { Header = "Renomear coleção…", Style = itemStyle };
        rename.Click += (_, _) => RenameCollection(name);
        var delete = new MenuItem { Header = "Excluir coleção", Style = itemStyle };
        delete.Click += (_, _) => DeleteCollection(name);

        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>Itens "Coleções" do menu do botão direito do card (chame no fim do BuildGameMenu).</summary>
    private void AddCollectionItems(ContextMenu menu, GameEntry game)
    {
        var itemStyle = (Style)FindResource("DarkMenuItemStyle");

        if (menu.Items.Count > 0)
        menu.Items.Add(new Separator { Style = (Style)FindResource("DarkMenuSeparatorStyle") });
        menu.Items.Add(new MenuItem { Header = "COLEÇÕES", IsEnabled = false, Style = itemStyle, FontSize = 11 });

        foreach (var name in CollectionsService.Names())
        {
            bool inside = CollectionsService.Contains(name, game);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = inside ? "✓" : "", Width = 20, Foreground = HomeAccent });
            row.Children.Add(new TextBlock { Text = name });

            var item = new MenuItem { Header = row, Style = itemStyle };
            var n = name;
            item.Click += (_, _) => ToggleInCollection(n, game);
            menu.Items.Add(item);
        }

        var create = new MenuItem { Header = "+  Nova coleção…", Style = itemStyle };
        create.Click += (_, _) => CreateCollectionFor(game);
        menu.Items.Add(create);
    }

    private void ToggleInCollection(string name, GameEntry game)
    {
        CollectionsService.Toggle(name, game);
        NavSounds.Confirm();
        string tab = ColPrefix + name;
        if (_selectedPlatform == tab || (IsOnHome && _homeFilter == tab)) ApplyFilter(); // a lista aberta muda na hora
    }

    private void CreateCollectionFor(GameEntry game)
    {
        var name = PromptName("Nova coleção", "", n => !CollectionsService.Exists(n));
        if (name == null) return;
        if (!CollectionsService.Create(name)) return;
        CollectionsService.Toggle(name, game);
        NavSounds.Confirm();
        RefreshCollections();
        UpdateTopNav();
    }

    private void RenameCollection(string oldName)
    {
        var newName = PromptName("Renomear coleção", oldName,
            n => string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase) || !CollectionsService.Exists(n));
        if (newName == null || !CollectionsService.Rename(oldName, newName)) return;

        string oldTab = ColPrefix + oldName, newTab = ColPrefix + newName;
        bool was = _selectedPlatform == oldTab, wasHome = _homeFilter == oldTab;

        RefreshCollections();
        if (was) { GoToTab(newTab); return; } // PlatformTab_Checked atualiza o resto
        if (wasHome) _homeFilter = newTab;
        UpdateTopNav();
        if (wasHome) ApplyFilter();
    }

    private void DeleteCollection(string name)
    {
        string tab = ColPrefix + name;
        bool was = _selectedPlatform == tab, wasHome = _homeFilter == tab;

        CollectionsService.Delete(name); // só a coleção: os jogos continuam na biblioteca
        if (wasHome) _homeFilter = "Todas";

        RefreshCollections();
        if (was) { _homeFilter = "Todas"; GoToTab("Início"); return; }
        UpdateTopNav();
        if (wasHome) ApplyFilter();
    }

    /// <summary>Janelinha escura para digitar o nome. Devolve null se cancelar.</summary>
    private string? PromptName(string title, string initial, Func<string, bool> isValid)
    {
        string? result = null;

        var box = new TextBox
        {
            Text = initial,
            MaxLength = 30,
            FontSize = 15,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 14, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x11, 0x1A)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3F)),
            BorderThickness = new Thickness(1)
        };

        var okBtn = new Button
        {
            Content = "Salvar",
            Style = (Style)FindResource("TopChipStyle"),
            Background = HomeAccent,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 0)
        };
        var cancelBtn = new Button
        {
            Content = "Cancelar",
            Style = (Style)FindResource("TopChipStyle"),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(okBtn);

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = new Border
            {
                Width = 380,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x24)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3F)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = panel
            }
        };

        void Confirm()
        {
            var text = box.Text.Trim();
            if (text.Length == 0 || !isValid(text))
            {
                // nome vazio ou já existente: borda vermelha e continua aberto
                box.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                return;
            }
            result = text;
            win.Close();
        }

        okBtn.Click += (_, _) => Confirm();
        cancelBtn.Click += (_, _) => win.Close();
        win.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { Confirm(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Escape) { win.Close(); e.Handled = true; }
        };
        box.TextChanged += (_, _) =>
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3F));
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        win.ShowDialog();
        return result;
    }


    /// <summary>Texto do botão do painel de detalhes conforme o estado do card do jogo.</summary>
    private void UpdateCardArtButton()
    {
        if (SetCardArtButton == null || ClearCardArtButton == null || _currentDetailGame == null) return;
        var g = _currentDetailGame;
        var key = CardKeyFor(g);
        var label = CardLabel(key);
        var pinnedKey = CardArtService.Get(key);

        if (pinnedKey == null)
        {
            SetCardArtButton.Content = $"Usar como arte de {label}";
            SetCardArtButton.ToolTip = "Fixa este jogo como a arte do card";
            ClearCardArtButton.Visibility = Visibility.Collapsed;
        }
        else if (pinnedKey == g.UserKey)
        {
            SetCardArtButton.Content = $"✓ Arte fixada em {label} · desafixar";
            SetCardArtButton.ToolTip = "Clique para voltar ao aleatório";
            ClearCardArtButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var other = _allGames.FirstOrDefault(x => x.UserKey == pinnedKey)?.Name ?? "outro jogo";
            SetCardArtButton.Content = $"Trocar a arte de {label} para este jogo";
            SetCardArtButton.ToolTip = $"Hoje fixada: {other}";
            ClearCardArtButton.ToolTip = $"Desafixar {other} e voltar ao aleatório";
            ClearCardArtButton.Visibility = Visibility.Visible;
        }
    }

    private void SetCardArtButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;
        var key = CardKeyFor(_currentDetailGame);
        if (CardArtService.Get(key) == _currentDetailGame.UserKey) CardArtService.Clear(key);   // já era ele: desafixa
        else CardArtService.Set(key, _currentDetailGame.UserKey);
        _cardArtSession.Remove(key);
        RefreshCardArts();
        UpdateCardArtButton();
    }

    private void ClearCardArtButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;
        var key = CardKeyFor(_currentDetailGame);
        CardArtService.Clear(key);
        _cardArtSession.Remove(key);
        RefreshCardArts();
        UpdateCardArtButton();
    }

    private bool _scMenusAttached;

    private List<GameEntry> ShortcutGames(string card) => _allGames.Where(g => !g.IsHidden && card switch
    {
        "Instalados" => g.IsInstalled && g.Platform != Platform.Emulador && g.Platform != Platform.Modificado,
        "Modificados" => g.IsInstalled && g.Platform == Platform.Modificado,
        _ => !g.IsInstalled
    }).ToList();

    /// <summary>Arte de fundo dos atalhos de baixo da Início. Só recarrega quando a biblioteca muda.</summary>
    private void LoadShortcutArts()
    {
        if (!_scMenusAttached)
        {
            _scMenusAttached = true;
            AttachCardMenu(ScAll, "shortcut:Instalados", ScAllArt, ScAllShade, () => ShortcutGames("Instalados"));
            AttachCardMenu(ScMod, "shortcut:Modificados", ScModArt, ScModShade, () => ShortcutGames("Modificados"));
            AttachCardMenu(ScAcq, "shortcut:Adquiridos", ScAcqArt, ScAcqShade, () => ShortcutGames("Adquiridos"));
        }

        if (ReferenceEquals(_scArtGames, _allGames)) return;
        _scArtGames = _allGames;

        _ = LoadCardArtAsync("shortcut:Instalados", ScAll, ScAllArt, ScAllShade, ShortcutGames("Instalados"));
        _ = LoadCardArtAsync("shortcut:Modificados", ScMod, ScModArt, ScModShade, ShortcutGames("Modificados"));
        _ = LoadCardArtAsync("shortcut:Adquiridos", ScAcq, ScAcqArt, ScAcqShade, ShortcutGames("Adquiridos"));
    }

    private void RefreshCardArts()
    {
        _scArtGames = null;
        if (IsOnHome) LoadShortcutArts();
    }

    /// <summary>Cor fixa de cada console (a mesma toda vez que o app abre).</summary>
    private static Color AccentFor(string name)
    {
        int h = 0;
        foreach (var ch in name) h = (h * 31 + ch) & 0x7FFFFFFF;
        return FromHsv(h % 360, 0.55, 0.55);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    /// <summary>Botão em pílula, no mesmo estilo dos botões do topo.</summary>
    private Button MakePill(string text)
    {
        return new Button
        {
            Content = text,
            Style = (Style)FindResource("TopChipStyle"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x22, 0x33)),
            Foreground = Brushes.White
        };
    }

    private void BuildEmulatorConsoleGames(EmulatorScanner.SystemInfo system, List<GameEntry> games)
    {
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        var backButton = MakePill("←  Voltar aos consoles");
        backButton.Click += (_, _) =>
        {
            _selectedEmulatorSystem = null;
            ApplyFilter();
        };
        toolbar.Children.Add(backButton);
        _emuBack = backButton;

        var romFolder = system.RomFolder;

        var addButton = MakePill("+  Adicionar ROM");
        addButton.Click += (_, _) =>
        {
            // Abre a pasta de ROM desse console no Explorer - você copia o arquivo lá dentro
            // e depois é só clicar em "Atualizar" que ele aparece na lista.
            try
            {
                if (!Directory.Exists(romFolder))
                    Directory.CreateDirectory(romFolder); // sistema ainda sem nenhuma ROM - cria a pasta
                Process.Start("explorer.exe", $"\"{romFolder}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Não consegui abrir a pasta: {ex.Message}");
            }
        };
        toolbar.Children.Add(addButton);
        _emuAdd = addButton;

        var refreshButton = MakePill("↻  Atualizar");
        refreshButton.Click += async (_, _) => await LoadGamesAsync();
        toolbar.Children.Add(refreshButton);
        _emuRefresh = refreshButton;

        EmulatorSection.Children.Add(toolbar);
        UpdatePadGlyphs();

        var sectionBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x11, 0x13, 0x1C)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18)
        };
        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock
        {
            Text = $"🎮 {system.DisplayName.ToUpperInvariant()}",
            FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
        });
        header.Children.Add(new TextBlock
        {
            Text = $"({games.Count})", FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
            Margin = new Thickness(8, 0, 0, 0)
        });
        stack.Children.Add(header);

        if (games.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Nenhuma ROM aqui ainda. Clica em \"Adicionar ROM\" pra abrir a pasta e colocar uma.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        var itemsControl = new ItemsControl
        {
            ItemTemplate = (DataTemplate)Resources["InstalledGameCardTemplate"],
            ItemsSource = games
        };
        var panelTemplate = new ItemsPanelTemplate();
        var wrapPanelFactory = new FrameworkElementFactory(typeof(WrapPanel));
        wrapPanelFactory.SetValue(Panel.IsItemsHostProperty, true);
        panelTemplate.VisualTree = wrapPanelFactory;
        itemsControl.ItemsPanel = panelTemplate;
        stack.Children.Add(itemsControl);
        foreach (var g in games)
        {
            var game = g;
            _navTargets.Add(new NavTarget
            {
                Game = game,
                Element = () => itemsControl.ItemContainerGenerator.ContainerFromItem(game) as FrameworkElement
            });
        }

        sectionBorder.Child = stack;
        EmulatorSection.Children.Add(sectionBorder);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            SearchBox.Clear();
            LeaveSearchBox();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Down)
        {
            LeaveSearchBox();
            e.Handled = true;
        }
    }

        // ── Tela Início (estilo console) ─────────────────────────────────────────

    private static readonly SolidColorBrush HomeAccent = new(Color.FromRgb(0x10, 0xB9, 0x81));

    private void BuildHome()
    {
        if (HomeTilesList == null) return;

        var search = SearchBox?.Text?.Trim().ToLowerInvariant() ?? "";
               // Filtros (menos "Todas") também mostram os não instalados, depois dos instalados
        bool withAcquired = _homeFilter != "Todas" || search.Length > 0;
        var pool = ApplyHomeFilter(_allGames.Where(g => (withAcquired || g.IsInstalled) && g.IsHidden == _showHidden)).ToList();
        var limite = DateTime.Now.AddDays(-RecentMaxAgeDays);

        List<GameEntry> tiles;
        if (search.Length > 0)
        {
            tiles = SortGames(SearchAliasService.Filter(pool, search))
                        .Take(HomeTileCount).ToList();
        }
        else
        {
            var recent = SortGames(pool.Where(g => g.SortLastPlayed != null && g.SortLastPlayed.Value >= limite));
            if (_homeFilter == "Todas")
            {
                // Recentes primeiro; depois completa com os jogos de loja instalados (sem ROMs e modificados)
                var recentSet = new HashSet<GameEntry>(recent);
                var rest = SortGames(pool.Where(g => !recentSet.Contains(g)
                                                     && g.Platform != Platform.Emulador
                                                     && g.Platform != Platform.Modificado));
                tiles = recent.Concat(rest).Take(HomeTileCount).ToList();
            }
            else
            {
                // Plataforma ou favoritos: recentes primeiro, depois o resto
                var recentSet = new HashSet<GameEntry>(recent);
                var rest = SortGames(pool.Where(g => !recentSet.Contains(g) && g.IsInstalled))
                    .Concat(SortGames(pool.Where(g => !recentSet.Contains(g) && !g.IsInstalled)));
                tiles = recent.Concat(rest).Take(HomeTileCount).ToList();
            }
        }

        _homeMouseTile = null;
        _homeMouseContainer = null;
        HomeTilesList.ItemsSource = tiles;

        _navTargets = new();
        foreach (var g in tiles) AddGameTarget(HomeTilesList, g);

        // "Ver todos": abre a tela completa do filtro atual
        HomeSeeAllTile.Tag = HomeFilterTab();
        _navTargets.Add(new NavTarget
        {
            Element = () => HomeSeeAllTile,
            Activate = OpenHomeSeeAll
        });
        AddShortcutTargets();
        LoadShortcutArts();

        int idx = _homeGame != null ? tiles.IndexOf(_homeGame) : -1;
        if (idx < 0) idx = Math.Clamp(_navIndex, 0, Math.Max(0, tiles.Count - 1));
        _navIndex = idx;

        SetHomeFocus(tiles.Count > 0 ? tiles[idx] : null);
        ScheduleHighlight();
        UpdateHomeArrows();
    }

        /// <summary>Ao voltar para a Início, rola a fileira até o jogo em foco (o primeiro volta ao início da fileira).</summary>
    private void ScrollHomeToFocus()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (HomeScroll == null || HomeTilesList.Items.Count == 0) return;
            int i = Math.Clamp(_navIndex, 0, HomeTilesList.Items.Count - 1);
            if (i == 0) { HomeScroll.ScrollToHorizontalOffset(0); return; }
            (HomeTilesList.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement)?.BringIntoView();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ── Início: filtro do topo e setas ───────────────────────────────────────

    private string _homeFilter = "Todas"; // Todas, Favoritos, Steam, Epic Games, GOG, EA App, Ubisoft, Emuladores, Modificados
    private string? _homeEmuSystem; // console escolhido no filtro Emuladores (null = todos)

    private static string EmuSystemOf(GameEntry g) =>
        string.IsNullOrWhiteSpace(g.EmulatorSystem) ? "Outros" : g.EmulatorSystem;
    private bool IsOnHome => _selectedPlatform == "Início";

    /// <summary>Aba que "Ver todos" abre para o filtro atual.</summary>
    private string HomeFilterTab() => _homeFilter == "Todas"
        ? (string.IsNullOrWhiteSpace(SearchBox?.Text) ? "Instalados" : "Todos")
        : _homeFilter;

        private IEnumerable<GameEntry> ApplyHomeFilter(IEnumerable<GameEntry> p) => _homeFilter switch
    {
        "Favoritos" => p.Where(g => g.IsFavorite),
        "Steam" => p.Where(g => g.Platforms.Contains(Platform.Steam) || g.Platform == Platform.Steam),
        "Epic Games" => p.Where(g => g.Platforms.Contains(Platform.Epic) || g.Platform == Platform.Epic),
        "GOG" => p.Where(g => g.Platforms.Contains(Platform.Gog) || g.Platform == Platform.Gog),
        "EA App" => p.Where(g => g.Platforms.Contains(Platform.EaApp) || g.Platform == Platform.EaApp),
        "Ubisoft" => p.Where(g => g.Platforms.Contains(Platform.Ubisoft) || g.Platform == Platform.Ubisoft),
        "Emuladores" => p.Where(g => g.Platform == Platform.Emulador
                        && (_homeEmuSystem == null || EmuSystemOf(g) == _homeEmuSystem)),
        "Modificados" => p.Where(g => g.Platform == Platform.Modificado),
        _ when IsCollectionTab(_homeFilter) => p.Where(g => CollectionsService.Contains(CollectionName(_homeFilter), g)),
        _ => p
    };

        private void SetHomeFilter(string filter, string? emuSystem = null)
    {
        _homeFilter = filter;
        _homeEmuSystem = filter == "Emuladores" ? emuSystem : null;
        _homeGame = null;
        _navIndex = 0;
        BuildHome();
        UpdateTopNav();
    }

    /// <summary>"Ver todos": abre a tela completa do filtro atual (e, nos emuladores, direto no console escolhido).</summary>
    private void OpenHomeSeeAll()
    {
        if (_homeFilter == "Emuladores") _selectedEmulatorSystem = _homeEmuSystem;
        GoToTab(HomeFilterTab());
    }

    private void HomeArrow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string t } || !int.TryParse(t, out var dir)) return;
        HomeScroll.ScrollToHorizontalOffset(HomeScroll.HorizontalOffset + dir * HomeScroll.ViewportWidth * 0.8);
    }

    private void HomeScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => UpdateHomeArrows();

    /// <summary>Setas só no mouse e só quando há mais jogos para aquele lado.</summary>
    private void UpdateHomeArrows()
    {
        if (HomeArrowLeft == null || HomeArrowRight == null || HomeScroll == null) return;
        bool show = !_controllerMode && IsOnHome;
        HomeArrowLeft.Visibility = show && HomeScroll.HorizontalOffset > 1
            ? Visibility.Visible : Visibility.Collapsed;
        HomeArrowRight.Visibility = show && HomeScroll.HorizontalOffset + HomeScroll.ViewportWidth < HomeScroll.ExtentWidth - 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

        // ── Início: atalhos ──────────────────────────────────────────────────────

    private void AddShortcutTargets()
    {
        foreach (var sc in new[] { ScAll, ScMod, ScAcq })
        {
            var el = sc;
            var key = (string)sc.Tag;
            _navTargets.Add(new NavTarget { Element = () => el, Activate = () => GoToTab(key) });
        }
            _navTargets.Add(new NavTarget { Element = () => ScStats, Activate = OpenStats });
    }

        private List<GameEntry>? _scArtGames;

    private void GoToTab(string name)
    {
        foreach (var child in PlatformTabs.Children)
            if (child is RadioButton rb && rb.Content?.ToString() == name) { rb.IsChecked = true; return; }
    }

        // ── Barra do topo ────────────────────────────────────────────────────────

    private static readonly string[] TopStoreTabs = { "Steam", "Epic Games", "GOG", "EA App", "Ubisoft" };
    private static readonly SolidColorBrush TopNavActiveFill = MakeTopNavFill();

    private static SolidColorBrush MakeTopNavFill()
    {
        var b = new SolidColorBrush(Color.FromArgb(41, 255, 255, 255));
        b.Freeze();
        return b;
    }

        private void UpdateTopNav()
    {
        if (NavHomeButton == null) return;
        bool home = IsOnHome;
        SetTopNavActive(NavHomeButton, home && _homeFilter == "Todas");
        SetTopNavActive(NavFavButton, home ? _homeFilter == "Favoritos" : _selectedPlatform == "Favoritos");

        string chipKey = home ? (_homeFilter == "Todas" ? "Todos" : _homeFilter) : _selectedPlatform;
        foreach (var child in TopChips.Children)
        {
            if (child is not Button b) continue;
            bool active = (b.Tag as string) == chipKey;
            b.Background = active ? HomeAccent : Brushes.Transparent;
            b.Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
        }
        TopChipsBar.Visibility = Visibility.Collapsed; // escolheu algo: fecha as barras de chips
        EmuChipsBar.Visibility = Visibility.Collapsed;
        CollectionChipsBar.Visibility = Visibility.Collapsed;
        UpdateCollectionChipsHighlight();
        UpdateTopToggles();
    }

    private void UpdateTopToggles()
    {
        if (NavPlatformsButton == null) return;
        bool colActive = IsCollectionTab(IsOnHome ? _homeFilter : _selectedPlatform);
        bool platformActive = IsOnHome
            ? _homeFilter != "Todas" && _homeFilter != "Favoritos" && _homeFilter != "Emuladores" && !IsCollectionTab(_homeFilter)
            : Array.IndexOf(TopStoreTabs, _selectedPlatform) >= 0;
        bool emuActive = IsOnHome ? _homeFilter == "Emuladores" : _selectedPlatform == "Emuladores";

        SetTopNavActive(NavPlatformsButton, TopChipsBar.Visibility == Visibility.Visible || platformActive);
        SetTopNavActive(NavEmuButton, EmuChipsBar.Visibility == Visibility.Visible || emuActive);
        SetTopNavActive(NavCollectionsButton, CollectionChipsBar.Visibility == Visibility.Visible || colActive);
        SetTopNavActive(NavSearchButton, SearchBar.Visibility == Visibility.Visible || !string.IsNullOrEmpty(SearchBox.Text));
        SetTopNavActive(SettingsButton, SettingsBar.Visibility == Visibility.Visible);
    }

    private void NavPlatforms_Click(object sender, RoutedEventArgs e)
    {
        NavSounds.Tab();
        bool open = TopChipsBar.Visibility != Visibility.Visible;
        TopChipsBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            CollectionChipsBar.Visibility = Visibility.Collapsed;
            SettingsBar.Visibility = Visibility.Collapsed;
            EmuChipsBar.Visibility = Visibility.Collapsed;
        }
        UpdateTopToggles();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        NavSounds.Tab();
        bool open = SettingsBar.Visibility != Visibility.Visible;
        SettingsBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            CollectionChipsBar.Visibility = Visibility.Collapsed;
            TopChipsBar.Visibility = Visibility.Collapsed;
            EmuChipsBar.Visibility = Visibility.Collapsed;
        }
        UpdateTopToggles();
    }

    private void NavSearch_Click(object sender, RoutedEventArgs e)
    {
        if (SearchBar.Visibility == Visibility.Visible)
        {
            // Segundo clique: fecha a busca e tira o filtro
            SearchBox.Clear();
            SearchBar.Visibility = Visibility.Collapsed;
            LeaveSearchBox();
        }
        else
        {
            SearchBar.Visibility = Visibility.Visible;
            SearchBox.UpdateLayout();
            SearchBox.Focus();
        }
        UpdateTopToggles();
    }

        private void NavEmu_Click(object sender, RoutedEventArgs e)
    {
        NavSounds.Tab();
        bool open = EmuChipsBar.Visibility != Visibility.Visible;
        if (open) BuildEmuChips();
        EmuChipsBar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            CollectionChipsBar.Visibility = Visibility.Collapsed;
            TopChipsBar.Visibility = Visibility.Collapsed;
            SettingsBar.Visibility = Visibility.Collapsed;
        }
        UpdateTopToggles();
    }

    /// <summary>Chips: "Todos" + um por console que tem jogo.</summary>
    private void BuildEmuChips()
    {
        EmuChips.Children.Clear();
        var systems = _allGames
            .Where(g => g.Platform == Platform.Emulador && !g.IsHidden)
            .Select(EmuSystemOf).Distinct().OrderBy(s => s).ToList();

        void AddChip(string label, string tag)
        {
            bool active = IsOnHome && _homeFilter == "Emuladores" && (_homeEmuSystem ?? "") == tag;
            var chip = new Button
            {
                Content = label,
                Tag = tag,
                Style = (Style)FindResource("TopChipStyle"),
                Margin = new Thickness(0, 0, 6, 6),
                Background = active ? HomeAccent : Brushes.Transparent,
                Foreground = active ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB))
            };
            chip.Click += EmuChip_Click;
            EmuChips.Children.Add(chip);
        }

        AddChip("Todos", "");
        foreach (var s in systems) AddChip(s, s);
    }

    private void EmuChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        NavSounds.Tab();
        string? sys = tag.Length == 0 ? null : tag;

        if (IsOnHome) { SetHomeFilter("Emuladores", sys); return; }

        // Em outra tela: vai para a Início já com o console escolhido
        _homeFilter = "Emuladores";
        _homeEmuSystem = sys;
        _homeGame = null;
        _navIndex = 0;
        GoToTab("Início");
        UpdateTopNav();
    }

    private void SearchBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(SearchBox.Text)) SearchBar.Visibility = Visibility.Collapsed;
        UpdateTopToggles();
    }

    private static void SetTopNavActive(Button b, bool active)
    {
        b.BorderBrush = active ? HomeAccent : Brushes.Transparent;
        b.Background = active ? TopNavActiveFill : Brushes.Transparent;
    }

    private void NavTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tab }) return;
        NavSounds.Tab();

        if (IsOnHome && tab == "Início") { SetHomeFilter("Todas"); return; }       // volta ao padrão
        if (IsOnHome && tab == "Favoritos")                                         // liga/desliga favoritos
        {
            SetHomeFilter(_homeFilter == "Favoritos" ? "Todas" : "Favoritos");
            return;
        }
        if (tab == "Início") _homeFilter = "Todas";
        GoToTab(tab);
    }

    private void TopChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tab }) return;
        NavSounds.Tab();

        if (IsOnHome) { SetHomeFilter(tab == "Todos" ? "Todas" : tab); return; }   // filtra a fileira da Início
        GoToTab(tab);
        UpdateTopNav();
    }

    private void Shortcut_Click(object sender, MouseButtonEventArgs e)
    {
                if (ReferenceEquals(sender, HomeSeeAllTile))
        {
            NavSounds.Confirm();
            OpenHomeSeeAll();
            return;
        }
        if (ReferenceEquals(sender, ScStats))
        {
            NavSounds.Confirm();
            OpenStats();
            return;
        }
        if (sender is FrameworkElement { Tag: string key })
        {
            NavSounds.Confirm();
            GoToTab(key);
        }
    }

    private void Shortcut_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_controllerMode || sender is not Border b) return;
        b.BorderBrush = HomeAccent;
        NavSounds.Hover();
    }

    private void Shortcut_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border b) b.BorderBrush = Brushes.Transparent;
    }

    /// <summary>Troca o jogo em foco: título, linha de informação, botão Jogar e arte de fundo.</summary>
    private void SetHomeFocus(GameEntry? game)
    {
        _homeGame = game;
        if (HomeTitleText == null) return;

        if (game == null)
        {
            HomeTitleText.Text = "Nenhum jogo encontrado";
            HomeSubText.Text = "";
            HomePlayButton.Visibility = Visibility.Collapsed;
            return;
        }

        bool changed = HomeTitleText.Text != game.Name;
        HomeTitleText.Text = game.Name;
        HomeSubText.Text = HomeSubtitle(game);
        UpdateHomePlayVisibility();
        if (changed)
        {
            var textFade = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(textFade, 30);
            HomeTitleText.BeginAnimation(UIElement.OpacityProperty, textFade);
            HomeSubText.BeginAnimation(UIElement.OpacityProperty, textFade);
        }
        _ = ApplyHomeBackgroundAsync(game);
    }
        /// <summary>O botão Jogar grande só aparece com o mouse (no controle o START joga).</summary>
    private void UpdateHomePlayVisibility()
    {
        if (HomePlayButton == null) return;
        HomePlayButton.Visibility = !_controllerMode && _homeGame is { IsInstalled: true }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string HomeSubtitle(GameEntry game)
    {
        var parts = new List<string>();

        // Tempo: só mostra quando há uma única plataforma com tempo (nunca soma entre lojas)
        var withTime = game.PlaytimeByPlatform.Where(kv => kv.Value > 0).ToList();
        if (withTime.Count == 1)
            parts.Add($"{withTime[0].Value / 60}h {withTime[0].Value % 60}m jogadas");
        else if (withTime.Count == 0 && game.PlaytimeMinutes > 0)
            parts.Add($"{game.PlaytimeMinutes / 60}h {game.PlaytimeMinutes % 60}m jogadas");

           var last = game.LastPlayedFormatted;
           if (!string.IsNullOrWhiteSpace(last) && last != "\u2014")
               parts.Add(last.StartsWith("Nunca", StringComparison.OrdinalIgnoreCase) ? last : $"Última sessão: {last}");

        if (!game.IsInstalled) parts.Insert(0, "Não instalado");
        return parts.Count == 0 ? "Nunca jogado" : string.Join("  \u00B7  ", parts);
    }

    private void ClearHomeMouseFocus()
    {
        if (_homeMouseContainer != null)
        {
            _homeMouseContainer.RenderTransform = Transform.Identity;
            Panel.SetZIndex(_homeMouseContainer, 0);
        }
        if (_homeMouseTile != null) _homeMouseTile.BorderBrush = Brushes.Transparent;
        _homeMouseContainer = null;
        _homeMouseTile = null;
    }

    private void HomeTile_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_controllerMode) return;
        NavSounds.Hover();
        if (sender is not Border { Tag: GameEntry game } tile) return;

        ClearHomeMouseFocus();
        var container = HomeTilesList.ItemContainerGenerator.ContainerFromItem(game) as FrameworkElement;
        _homeMouseTile = tile;
        _homeMouseContainer = container;
        tile.BorderBrush = HomeAccent;
        if (container != null)
        {
            container.RenderTransformOrigin = new Point(0.5, 0.5);
            container.RenderTransform = new ScaleTransform(1.15, 1.15);
            Panel.SetZIndex(container, 10);
        }

        int i = _navTargets.FindIndex(t => ReferenceEquals(t.Game, game));
        if (i >= 0) _navIndex = i;
        SetHomeFocus(game);
    }

    private void HomePlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_homeGame != null) LaunchGame(_homeGame);
    }

    /// <summary>Fundo do jogo em foco (Início e telas de lista). Sem imagem: degradê (Início) ou capa desfocada (listas).</summary>
        /// <summary>Fundo do jogo em foco (Início e telas de lista). Sem imagem: degradê (Início) ou capa desfocada (listas).</summary>
    private async Task ApplyHomeBackgroundAsync(GameEntry game)
    {
        int version = ++_homeBgVersion;
        string screen = _selectedPlatform;
        bool home = screen == "Início";
        if (!home) SetGridBanner(game);

        BitmapSource? bg = null;
        try { bg = await BackgroundService.GetAsync(game); }
        catch (Exception ex) { Logger.LogError("Fundo do jogo", ex); }

        // Logo na abertura o disco e a rede ainda estão ocupados: se veio vazio, tenta mais uma vez
        if (bg == null && home)
        {
            await Task.Delay(1500);
            if (version != _homeBgVersion || screen != _selectedPlatform) return;
            try { bg = await BackgroundService.GetAsync(game); }
            catch (Exception ex) { Logger.LogError("Fundo do jogo (2ª tentativa)", ex); }
        }
        if (home) StartupLog.Mark($"fundo da Início: {game.Name} -> {(bg != null ? "ok" : "vazio")}");

        // Já mudou de jogo ou de tela enquanto carregava
        if (version != _homeBgVersion || screen != _selectedPlatform) return;

        if (bg != null)
        {
            BgTint.Visibility = Visibility.Collapsed;
            _homeBgBmp = bg;
            _bgSource = null;
            DynamicBgImage.Source = bg;
            double bgTarget = home ? 0.75 : 0.6;
            DynamicBgImage.Opacity = bgTarget;
            var bgFade = new DoubleAnimation(bgTarget * 0.2, bgTarget, TimeSpan.FromMilliseconds(260))
            {
                FillBehavior = FillBehavior.Stop   // depois da animação, vale o valor normal da propriedade
            };
            Timeline.SetDesiredFrameRate(bgFade, 30);
            DynamicBgImage.BeginAnimation(UIElement.OpacityProperty, bgFade);
            BgShade.Opacity = 0.6;
            LayoutHomeBg();
            return;
        }

        ResetBgLayout();
        BgTint.Visibility = Visibility.Collapsed;

        if (!home)
        {
            // Telas de lista sem fundo do jogo: capa desfocada, como antes
            DynamicBgImage.Source = null;
            _bgSource = null;
            SetDynamicBackground(game.CoverImage);
            return;
        }

        // Início sem imagem: degradê escuro com a cor média da capa
        DynamicBgImage.Source = null;
        _bgSource = null;
        BgShade.Opacity = 0.0;
        var c = BackgroundService.AverageColor(game.CoverImage);
        if (c == null) return;
        var top = Color.FromRgb((byte)(c.Value.R * 0.45), (byte)(c.Value.G * 0.45), (byte)(c.Value.B * 0.45));
        var brush = new LinearGradientBrush(top, Color.FromRgb(0x0A, 0x0B, 0x10), 90);
        brush.Freeze();
        BgTint.Fill = brush;
        BgTint.Visibility = Visibility.Visible;
    }

        /// <summary>Título e info do jogo em foco, na faixa de banner das telas de lista.</summary>
    private void SetGridBanner(GameEntry game)
    {
        if (GridBannerTitle == null) return;
        GridBannerTitle.Text = game.Name;
        GridBannerSub.Text = HomeSubtitle(game);
    }

    /// <summary>Início: imagem na largura da janela, colada no topo, esmaecendo para baixo.</summary>
    private void LayoutHomeBg()
    {
        var bmp = _homeBgBmp;
        if (bmp == null) return;

        var parent = DynamicBgImage.Parent as FrameworkElement;
        double w = parent?.ActualWidth > 0 ? parent.ActualWidth : ActualWidth;
        double h = parent?.ActualHeight > 0 ? parent.ActualHeight : ActualHeight;
        if (w <= 0 || h <= 0) return;

        double scale = Math.Max(w / bmp.PixelWidth, h * 0.5 / bmp.PixelHeight);
        DynamicBgImage.Stretch = Stretch.Fill;
        DynamicBgImage.HorizontalAlignment = HorizontalAlignment.Center;
        DynamicBgImage.VerticalAlignment = VerticalAlignment.Top;
        DynamicBgImage.Width = bmp.PixelWidth * scale;
        DynamicBgImage.Height = bmp.PixelHeight * scale;
        DynamicBgImage.OpacityMask = HomeBgFade;
    }

    private void ResetBgLayout()
    {
        _homeBgBmp = null;
        DynamicBgImage.Stretch = Stretch.UniformToFill;
        DynamicBgImage.HorizontalAlignment = HorizontalAlignment.Stretch;
        DynamicBgImage.VerticalAlignment = VerticalAlignment.Stretch;
        DynamicBgImage.Width = double.NaN;
        DynamicBgImage.Height = double.NaN;
        DynamicBgImage.OpacityMask = null;
    }

    private void ChangeBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Escolher plano de fundo",
            Filter = "Imagens (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        if (!BackgroundService.SetManual(_currentDetailGame, dialog.FileName))
        {
            MessageBox.Show("Não consegui usar essa imagem como fundo.");
            return;
        }
        if (_selectedPlatform == "Início" && ReferenceEquals(_homeGame, _currentDetailGame))
            _ = ApplyHomeBackgroundAsync(_currentDetailGame);
    }

    private void RemoveBackgroundButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;
        BackgroundService.RemoveManual(_currentDetailGame);
        if (_selectedPlatform == "Início" && ReferenceEquals(_homeGame, _currentDetailGame))
            _ = ApplyHomeBackgroundAsync(_currentDetailGame);
    }

    private BitmapSource? _homeBgBmp;
    private static readonly Brush HomeBgFade = MakeHomeBgFade();

    private static Brush MakeHomeBgFade()
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        b.GradientStops.Add(new GradientStop(Colors.Black, 0.0));
        b.GradientStops.Add(new GradientStop(Colors.Black, 0.55));
        b.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
        b.Freeze();
        return b;
    }

    private void HomeScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        HomeScroll.ScrollToHorizontalOffset(HomeScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void LeaveSearchBox()
    {
        System.Windows.Input.Keyboard.ClearFocus();
        Focus();
    }

    // ── Ordenar, favoritos e ocultos ─────────────────────────────────────────

    private List<GameEntry> SortGames(IEnumerable<GameEntry> games) => _sortMode switch
    {
        SortMode.LastPlayed => games.OrderByDescending(g => g.SortLastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name).ToList(),
        SortMode.MostPlayed => games.OrderByDescending(g => g.SortPlaytime).ThenBy(g => g.Name).ToList(),
        _ => games.OrderBy(g => g.Name).ToList()
    };

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        _sortMode = _sortMode switch
        {
            SortMode.Name => SortMode.LastPlayed,
            SortMode.LastPlayed => SortMode.MostPlayed,
            _ => SortMode.Name
        };
        SortButton.Content = SortLabel();
        SaveUiState();
        _navIndex = 0;
        ApplyFilter();
    }
        private async void FetchBackgrounds_Click(object sender, RoutedEventArgs e)
    {
        // Segundo clique durante a busca = cancelar
        if (_bgFetchCts != null) { _bgFetchCts.Cancel(); return; }

        if (!BackgroundService.HasSgdbKey)
        {
            MessageBox.Show(this,
                "Chave do SteamGridDB não encontrada.\nCrie o arquivo %AppData%\\GameHub\\secrets.json com SteamGridDbKey.",
                "Buscar fundos", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var games = _allGames.Where(g => !g.IsHidden).ToList();
        if (games.Count == 0) return;

        BackgroundService.ResetMisses();
        _bgFetchCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p =>
            FetchBgButton.Content = $"Buscando… {p.Done}/{p.Total}  (clique para cancelar)");
        FetchBgButton.Content = $"Buscando… 0/{games.Count}  (clique para cancelar)";

        (int Done, int Have) result = (0, 0);
        bool cancelled;
        try
        {
            result = await BackgroundService.PrefetchAsync(games, progress, _bgFetchCts.Token);
        }
        finally
        {
            cancelled = _bgFetchCts.IsCancellationRequested;
            _bgFetchCts.Dispose();
            _bgFetchCts = null;
            FetchBgButton.Content = "Buscar fundos";
        }

        if (_homeGame != null) _ = ApplyHomeBackgroundAsync(_homeGame);

            ShowToast(cancelled
            ? $"Busca cancelada · {result.Have} de {result.Done} jogos com fundo"
            : $"Fundos prontos · {result.Have} de {result.Done} jogos");
    }
        private DispatcherTimer? _toastTimer;
    private int _toastVersion;

    /// <summary>Aviso curto no rodapé que some sozinho.</summary>
    private void ShowToast(string text, int ms = 3500)
    {
        int v = ++_toastVersion;
        ToastText.Text = text;
        ToastBar.Visibility = Visibility.Visible;
        ToastBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) => { if (v == _toastVersion) ToastBar.Visibility = Visibility.Collapsed; };
            ToastBar.BeginAnimation(OpacityProperty, fade);
        };
        _toastTimer.Start();
    }
    private string SortLabel() => _sortMode switch
    {
        SortMode.LastPlayed => "\u2195 Ordem: \u00DAltimo jogado",
        SortMode.MostPlayed => "\u2195 Ordem: Mais jogado",
        _ => "\u2195 Ordem: Nome"
    };

    private void SaveUiState()
    {
        if (!_uiReady) return; // ainda abrindo: não pode sobrescrever o que foi salvo
        UiStateService.Save(_selectedPlatform, _sortMode.ToString());
    }

    /// <summary>Volta para a aba e a ordenação da última vez, sem recarregar a lista ainda.</summary>
    private void RestoreUiState()
    {
        try
        {
            _restoringUi = true;

            if (Enum.TryParse<SortMode>(UiStateService.Sort, out var sort) && Enum.IsDefined(sort))
                _sortMode = sort;
            SortButton.Content = SortLabel();

            foreach (var child in PlatformTabs.Children)
            {
                if (child is RadioButton rb && rb.Content?.ToString() == UiStateService.Platform)
                {
                    _selectedPlatform = UiStateService.Platform;
                    rb.IsChecked = true;
                    break;
                }
            }
        }
        catch (Exception ex) { Logger.LogError("Restaurar aba e ordem", ex); }
        finally { _restoringUi = false; }
    }

    private void HiddenButton_Click(object sender, RoutedEventArgs e)
    {
        _showHidden = !_showHidden;
        HiddenButton.Content = _showHidden ? "Ocultos \u2713" : "Ocultos";
        HiddenButton.Background = new SolidColorBrush(_showHidden
            ? Color.FromRgb(0x05, 0x96, 0x69)
            : Color.FromRgb(0x1E, 0x21, 0x30));
        _navIndex = 0;
        ApplyFilter();
    }

    private void UpdateDetailUserButtons(GameEntry game)
    {
        DetailFavoriteButton.Content = PadContent("Y",
            game.IsFavorite ? "★  Favorito" : "☆  Favoritar",
            game.IsFavorite ? "Favorito" : "Favoritar");
        DetailHideButton.Content = PadContent("X",
            game.IsHidden ? "Mostrar na lista" : "Ocultar",
            game.IsHidden ? "Mostrar na lista" : "Ocultar");
        RefreshHints();
    }

    private void DetailFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;
        var game = _currentDetailGame;
        game.IsFavorite = !game.IsFavorite;
        UserDataService.SetFavorite(game, game.IsFavorite);
        if (game.IsFavorite) NavSounds.Favorite(); // SOM
        UpdateDetailUserButtons(game);
        ApplyFilter();
    }

    private void DetailHideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;
        var game = _currentDetailGame;
        game.IsHidden = !game.IsHidden;
        UserDataService.SetHidden(game, game.IsHidden);
        UpdateStats();
        ApplyFilter();
        CloseDetailPanel(); // o jogo saiu desta lista, então fecha o painel
    }

    // ── Barra de dicas dos botões do controle ────────────────────────────────

    /// <summary>Dicas (botão, ação) da tela atual. Muda conforme onde o usuário está.</summary>
    private List<(string Key, string Label)> CurrentHints()
    {
        var hints = new List<(string, string)>();
        if (KbOpen)
        {
            hints.Add(("A", "Digitar"));
            hints.Add(("X", "Apagar"));
            hints.Add(("Y", "Espaço"));
            hints.Add(("START", "Concluir"));
            hints.Add(("B", "Fechar"));
            return hints;
        }

        // Lançando o jogo ou adicionando jogo: a tela já tem as próprias instruções
        if (_launching || AddCustomGamePanelRoot?.Visibility == Visibility.Visible) return hints;

        var game = ControllerTargetGame();
        if (ViewerOpen) { hints.Add(("D-PAD", "Trocar")); hints.Add(("B", "Fechar")); return hints; }
        if (CpOpen) { hints.Add(("D-PAD", "Navegar")); hints.Add(("A", "Marcar / desmarcar")); hints.Add(("B", "Fechar")); return hints; }

        if (_detailOpen)
        {
            hints.Add(("D-PAD", "Navegar")); hints.Add(("A", "Selecionar"));
            hints.Add(("B", "Fechar"));
            if (game is { IsInstalled: true }) hints.Add(("START", "Jogar"));
            if (game != null)
            {
                hints.Add(("Y", game.IsFavorite ? "Tirar dos favoritos" : "Favoritar"));
                hints.Add(("X", game.IsHidden ? "Mostrar na lista" : "Ocultar"));
            }
            return hints;
        }

        hints.Add(("D-PAD", "Navegar"));

        var tgt = _navIndex >= 0 && _navIndex < _navTargets.Count ? _navTargets[_navIndex] : null;
        if (tgt != null && tgt.Game == null && tgt.Activate != null
            && !(_selectedPlatform == "Emuladores" && _selectedEmulatorSystem == null))
        {
            hints.Add(("A", "Abrir"));
            hints.Add(("LB RB", "Menu do topo"));
            return hints;
        }

        bool consolePicker = _selectedPlatform == "Emuladores" && _selectedEmulatorSystem == null;
        if (consolePicker)
        {
            hints.Add(("A", "Abrir console"));
        }
        else
        {
            hints.Add(("A", "Detalhes"));
            if (game is { IsInstalled: true }) hints.Add(("START", "Jogar"));
            if (game != null) hints.Add(("Y", game.IsFavorite ? "Tirar dos favoritos" : "Favoritar"));
            if (!string.IsNullOrEmpty(SearchBox.Text)) hints.Add(("B", "Limpar pesquisa"));
            else if (!IsOnHome || _homeFilter != "Todas") hints.Add(("B", "Voltar"));
        }

        hints.Add(("LB RB", "Menu do topo"));
        return hints;
    }

    private void RefreshHints()
    {
        if (ControllerHintBar == null || ControllerHintPanel == null) return;

        if (!_controllerMode)
        {
            ControllerHintBar.Visibility = Visibility.Collapsed;
            return;
        }

        var hints = CurrentHints();
        ControllerHintPanel.Children.Clear();
        foreach (var (key, label) in hints)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 28, 0) };
            item.Children.Add(BuildHintGlyph(key));
            item.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            ControllerHintPanel.Children.Add(item);
        }

        ControllerHintBar.Visibility = hints.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Desenha o botão como no controle: A/B/X/Y redondos e coloridos, os outros em cápsula cinza.</summary>
    private static FrameworkElement BuildHintGlyph(string key)
    {
        Color? face = key switch
        {
            "A" => Color.FromRgb(0x4C, 0xAF, 0x50),
            "B" => Color.FromRgb(0xE5, 0x39, 0x35),
            "X" => Color.FromRgb(0x3B, 0x82, 0xF6),
            "Y" => Color.FromRgb(0xF5, 0xB0, 0x1E),
            _ => null
        };

        if (face != null)
        {
            return new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(face.Value),
                Child = new TextBlock
                {
                    Text = key,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = key == "Y" ? Brushes.Black : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        return new Border
        {
            Height = 26,
            MinWidth = 26,
            Padding = new Thickness(9, 0, 9, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x3F)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x44, 0x5C)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = key,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    /// <summary>Jogo em foco pro controle: o do painel de detalhe, se aberto, senão o card selecionado.</summary>
    private GameEntry? ControllerTargetGame()
    {
        if (DetailPanelRoot?.Visibility == Visibility.Visible) return _currentDetailGame;
        if (_navTargets.Count == 0) return null;
        return _navTargets[Math.Clamp(_navIndex, 0, _navTargets.Count - 1)].Game;
    }

    /// <summary>Botão Y: favorita / desfavorita o jogo selecionado (na grade ou no painel de detalhe).</summary>
    private void ControllerToggleFavorite()
    {
        if (OverlayBlocksActions) return;
        if (KbOpen) { KbType(" "); return; }
        if (AddCustomGamePanelRoot?.Visibility == Visibility.Visible) return;

        var game = ControllerTargetGame();
        if (game == null) return;

        game.IsFavorite = !game.IsFavorite;
        UserDataService.SetFavorite(game, game.IsFavorite);
        if (game.IsFavorite) NavSounds.Favorite(); // SOM
        if (_currentDetailGame == game) UpdateDetailUserButtons(game);
        ApplyFilter();
    }

    /// <summary>Botão X: oculta o jogo, mas só com o painel de detalhe aberto (evita ocultar sem querer pela grade).</summary>
    private void ControllerToggleHidden()
    {
        if (OverlayBlocksActions) return;
        if (KbOpen) { KbBackspace(); return; }
        if (DetailPanelRoot?.Visibility != Visibility.Visible || _currentDetailGame == null) return;
        DetailHideButton_Click(this, new RoutedEventArgs());
    }

    private void PlatformTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_restoringUi) return; // restauração ao abrir: a lista ainda nem carregou
        if (sender is RadioButton radio && radio.IsChecked == true)
        {
            _selectedPlatform = radio.Content?.ToString() ?? "Todos";
            if (_selectedPlatform != "Emuladores") _selectedEmulatorSystem = null;
            SaveUiState();
            _navIndex = 0;
            ApplyFilter();
            _ = RefreshLibraryQuietlyAsync();
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadGamesAsync();

    // ──────────────────────────────────────────────────────────────
    // Lançamento de jogo
    // ──────────────────────────────────────────────────────────────

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameEntry game })
            LaunchGame(game);
    }

    private void AddCustomGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (AddGameExePath != null) AddGameExePath.Text = "Nenhum arquivo selecionado";
        if (AddGameNameBox != null) AddGameNameBox.Text = "";
        if (AddCustomGamePanelRoot != null) AddCustomGamePanelRoot.Visibility = Visibility.Visible;
        RefreshHints();
        AddGameNameBox?.Focus();
    }

    private void AddGameBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolher executável do jogo",
            Filter = "Executáveis (*.exe)|*.exe|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        if (AddGameExePath != null) AddGameExePath.Text = dialog.FileName;
        if (AddGameNameBox != null && string.IsNullOrWhiteSpace(AddGameNameBox.Text))
            AddGameNameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        AddGameNameBox?.Focus();
    }

    private void CloseAddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        if (AddCustomGamePanelRoot != null) AddCustomGamePanelRoot.Visibility = Visibility.Collapsed;
        RefreshHints();
    }

    private async void ConfirmAddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        var path = AddGameExePath?.Text ?? "";
        var name = AddGameNameBox?.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(path) || path == "Nenhum arquivo selecionado")
        {
            MessageBox.Show("Escolhe o executável do jogo primeiro.");
            return;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Dá um nome pro jogo.");
            return;
        }

        if (AddCustomGamePanelRoot != null) AddCustomGamePanelRoot.Visibility = Visibility.Collapsed;
        RefreshHints();
        CustomGamesService.Add(name, path);
        await LoadGamesAsync();
    }

    private void RemoveCustomGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameEntry game }) return;
        if (string.IsNullOrWhiteSpace(game.LaunchExecutable)) return;

        var result = MessageBox.Show(
            $"Remover \"{game.Name}\" do GameHub?\n\nIsso só tira o card daqui - não apaga o jogo nem nenhum arquivo do seu PC.",
            "Remover jogo", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        CustomGamesService.Remove(game.Name, game.LaunchExecutable);
        _allGames.Remove(game);
        ApplyFilter();
    }

    // ── Modo controle / Modo mouse ───────────────────────────────────────────

    private void EnterControllerMode()
    {
        if (_controllerMode) return;
        _controllerMode = true;
        ClearHomeMouseFocus();
        Resources["CardPlayVis"] = Visibility.Collapsed; 
        try { _lastMousePos = Mouse.GetPosition(this); } catch { }
        // MouseBlocker cobre toda a tela com Cursor=None e IsHitTestVisible=True,
        // engolindo todos os eventos de mouse antes que cheguem nos cards.
        // Isso elimina hover E piscar — muito mais limpo que OverrideCursor.
        if (MouseBlocker != null) MouseBlocker.Visibility = Visibility.Visible;
        RefreshHints();
        ScheduleHighlight();
        UpdateHomeArrows();
        UpdateHomePlayVisibility();
        ApplyControllerOnlyVisibility();
    }

    private void EnterMouseMode()
    {
        if (!_controllerMode) return;
        _controllerMode = false;
        Resources["CardPlayVis"] = Visibility.Visible;
        if (MouseBlocker != null) MouseBlocker.Visibility = Visibility.Collapsed;
        ClearHighlight();
        RefreshHints();
        UpdateHomeArrows();
        UpdateHomePlayVisibility();
        ApplyControllerOnlyVisibility();
    }

    // ── Navegação por controle ───────────────────────────────────────────────

    private enum Dir { Up, Down, Left, Right }

    private void RebuildNavTargets(List<GameEntry> installed, List<GameEntry> acquired)
    {
        _navTargets = new();
        foreach (var g in installed) AddGameTarget(InstalledGamesList, g);
        foreach (var g in acquired) AddGameTarget(AcquiredGamesList, g);
        ScheduleHighlight();
    }

    private void AddGameTarget(ItemsControl? list, GameEntry game)
    {
        _navTargets.Add(new NavTarget
        {
            Game = game,
            Element = () => list?.ItemContainerGenerator.ContainerFromItem(game) as FrameworkElement
        });
    }

    private void ScheduleHighlight()
        => Dispatcher.BeginInvoke(new Action(HighlightCurrent), System.Windows.Threading.DispatcherPriority.Loaded);

    private void ClearHighlight()
    {
        if (_highlighted == null) return;
        if (_highlighted.Effect is DropShadowEffect fx)
            fx.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        _highlighted.Effect = null;
        _highlighted.RenderTransform = Transform.Identity;
        Panel.SetZIndex(_highlighted, 0);
        _highlighted = null;
    }

    /// <summary>Destaca o alvo atual: zoom suave + brilho pulsante, pra você saber onde está.</summary>
    private void HighlightCurrent()
    {
        EnsureTopBarTargets();
        ClearHighlight();
        RefreshHints();
        if (_detailOpen) return; // painel de detalhe aberto: sem brilho pulsando por trás

        // Só aplica o destaque especial quando o modo controle está ativo.
        if (!_controllerMode) return;
        
        if (_navTargets.Count == 0) return;
        _navIndex = Math.Clamp(_navIndex, 0, _navTargets.Count - 1);

        var el = _navTargets[_navIndex].Element();
        if (el == null) return;

        // Botões da barra de Ajustes: destaque menor, para não passar da borda da barra
        bool inSettingsBar = el is FrameworkElement { Parent: StackPanel sp } && ReferenceEquals(sp.Parent, SettingsBar);
        // Atalhos da Início (Instalados/Modificados/Adquiridos): zoom pequeno, sem cobrir os vizinhos
        bool isShortcut = ReferenceEquals(el, ScAll) || ReferenceEquals(el, ScMod) || ReferenceEquals(el, ScAcq) || ReferenceEquals(el, ScStats);

        var scale = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = scale;
        var grow = new DoubleAnimation(inSettingsBar ? 1.03 : isShortcut ? 1.06 : (_selectedPlatform == "Início" ? 1.18 : 1.07), TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var glow = new DropShadowEffect
        {
            Color = Color.FromRgb(0xA7, 0xF3, 0xD0),
            BlurRadius = (inSettingsBar || isShortcut) ? 10 : 18,
            ShadowDepth = 0,
            Opacity = 1.0
        };
        el.Effect = glow;
        // Pulsa por ~2 segundos (termina no brilho máximo) e depois o brilho fica fixo.
        // Animação infinita mantém a GPU redesenhando a janela inteira mesmo com você parado.
        // Pra voltar ao pulso contínuo, troque o RepeatBehavior por RepeatBehavior.Forever.
        var pulse = new DoubleAnimation(0.4, 1.0, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(TimeSpan.FromSeconds(2.1))
        };
        Timeline.SetDesiredFrameRate(pulse, 30);
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, pulse);

        Panel.SetZIndex(el, 10);
        // Na Início, pede um pouco de folga dos dois lados, para o zoom do destaque não passar da borda
        if (_selectedPlatform == "Início" && el is FrameworkElement fe && fe.ActualWidth > 0)
            el.BringIntoView(new Rect(-40, -10, fe.ActualWidth + 80, fe.ActualHeight + 20));
        else
            el.BringIntoView();
        _highlighted = el;

        if (_selectedPlatform == "Início")
        {
            if (_navTargets[_navIndex].Game is { } homeGame)
                SetHomeFocus(homeGame);
        }
        else
        {
            // Telas de lista: o fundo e o banner acompanham o jogo selecionado pelo controle
            if (_navTargets[_navIndex].Game is { } listGame)
                _ = ApplyHomeBackgroundAsync(listGame);

            // Na primeira linha (ou no menu do topo), volta a mostrar a faixa do banner
            if (MainScrollViewer.Visibility == Visibility.Visible)
            {
                bool showBanner = _navTargets[_navIndex].TopBar;
                if (!showBanner && MainScrollViewer.Content is Visual content)
                {
                    try { showBanner = el.TransformToAncestor(content).Transform(new Point(0, 0)).Y < 400; }
                    catch { }
                }
                if (showBanner) MainScrollViewer.ScrollToTop();
            }
        }
    }

        /// <summary>Põe a barra do topo na navegação do controle (sempre no fim da lista, sem mudar os índices dos jogos).</summary>
    private void EnsureTopBarTargets()
    {
        _navTargets.RemoveAll(t => t.TopBar);
        var rea = new RoutedEventArgs();

        void Add(FrameworkElement el, Action act) =>
            _navTargets.Add(new NavTarget { Element = () => el, Activate = act, TopBar = true });

        Add(NavHomeButton, () => NavTab_Click(NavHomeButton, rea));
        Add(NavPlatformsButton, () =>
        {
            NavPlatforms_Click(NavPlatformsButton, rea);
            AfterTopToggle(TopChipsBar.Visibility == Visibility.Visible
                ? TopChips.Children[0] as FrameworkElement : NavPlatformsButton);
        });
        Add(NavEmuButton, () =>
        {
            NavEmu_Click(NavEmuButton, rea);
            AfterTopToggle(EmuChipsBar.Visibility == Visibility.Visible && EmuChips.Children.Count > 0
                ? EmuChips.Children[0] as FrameworkElement : NavEmuButton);
        });
        Add(NavCollectionsButton, () =>
        {
            NavCollections_Click(NavCollectionsButton, rea);
            AfterTopToggle(CollectionChipsBar.Visibility == Visibility.Visible && CollectionChips.Children.Count > 0
                ? CollectionChips.Children[0] as FrameworkElement : NavCollectionsButton);
        });
        Add(NavFavButton, () => NavTab_Click(NavFavButton, rea));
        Add(SettingsButton, () =>
        {
            SettingsButton_Click(SettingsButton, rea);
            AfterTopToggle(SettingsBar.Visibility == Visibility.Visible ? SortButton : SettingsButton);
        });

        Add(NavSearchButton, OpenKeyboard);

        if (TopChipsBar.Visibility == Visibility.Visible)
        {
            foreach (var child in TopChips.Children)
            {
                if (child is not Button chip) continue;
                var c = chip;
                Add(c, () => TopChip_Click(c, rea));
            }
        }
        if (EmuChipsBar.Visibility == Visibility.Visible)
        {
            foreach (var child in EmuChips.Children)
            {
                if (child is not Button chip) continue;
                var c = chip;
                Add(c, () => EmuChip_Click(c, rea));
            }
        }
        if (CollectionChipsBar.Visibility == Visibility.Visible)
        {
            foreach (var child in CollectionChips.Children)
            {
                if (child is not Button chip) continue;
                if ((chip.Tag as string) == ColPrefix + "\u0001new") continue; // criar coleção exige teclado
                var c = chip;
                Add(c, () => CollectionChip_Click(c, rea));
            }
        }
        if (SettingsBar.Visibility == Visibility.Visible)
        {
            Add(SortButton, () => SortButton_Click(SortButton, rea));
            Add(HiddenButton, () => HiddenButton_Click(HiddenButton, rea));
            Add(RefreshBtn, () => RefreshButton_Click(RefreshBtn, rea));
            if (DuplicatesButton.Visibility == Visibility.Visible) Add(DuplicatesButton, () => DuplicatesButton_Click(DuplicatesButton, rea));
            Add(FetchBgButton, () => FetchBackgrounds_Click(FetchBgButton, rea));
        }
    }

    /// <summary>
    /// De qual ícone do topo o alvo atual faz parte: 0 Início, 1 Plataformas, 2 Emuladores, 3 Favoritos, 4 Ajustes.
    /// Chips contam como o ícone da barra deles. -1 se o cursor não está no topo.
    /// </summary>
    private int TopAnchorOf(int navIndex, out bool isIcon)
    {
        isIcon = false;
        if (navIndex < 0 || navIndex >= _navTargets.Count || !_navTargets[navIndex].TopBar) return -1;

        var el = _navTargets[navIndex].Element();
        if (ReferenceEquals(el, NavHomeButton)) { isIcon = true; return 0; }
        if (ReferenceEquals(el, NavPlatformsButton)) { isIcon = true; return 1; }
        if (ReferenceEquals(el, NavEmuButton)) { isIcon = true; return 2; }
        if (ReferenceEquals(el, NavCollectionsButton)) { isIcon = true; return 3; }
        if (ReferenceEquals(el, NavFavButton)) { isIcon = true; return 4; }
        if (ReferenceEquals(el, SettingsButton)) { isIcon = true; return 5; }
        if (ReferenceEquals(el, NavSearchButton)) { isIcon = true; return 6; }
        if (el != null && TopChips.Children.Contains(el)) return 1;
        if (el != null && EmuChips.Children.Contains(el)) return 2;
        if (el != null && CollectionChips.Children.Contains(el)) return 3;
        return 5; // itens da barra de Ajustes
    }

    /// <summary>Depois de abrir ou fechar uma barra do topo: recria os alvos e põe o cursor no item certo.</summary>
    private void AfterTopToggle(FrameworkElement? focusEl)
    {
        EnsureTopBarTargets();
        int i = focusEl != null ? _navTargets.FindIndex(t => ReferenceEquals(t.Element(), focusEl)) : -1;
        if (i >= 0) _navIndex = i;
        ScheduleHighlight();
    }

    private Point? CenterOf(FrameworkElement el)
    {
        try
        {
            if (!el.IsVisible || el.ActualWidth <= 0) return null;
            return el.TransformToAncestor(this).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        }
        catch { return null; }
    }

    private bool OverlayOpen()
        => DetailPanelRoot?.Visibility == Visibility.Visible
        || AddCustomGamePanelRoot?.Visibility == Visibility.Visible
        || DuplicatesRoot?.Visibility == Visibility.Visible
        || StatsRoot?.Visibility == Visibility.Visible
        || KbRoot?.Visibility == Visibility.Visible
        || ShotViewerRoot.Visibility == Visibility.Visible
        || CollectionPickerRoot?.Visibility == Visibility.Visible;

    /// <summary>Navegação espacial: vai pro alvo mais próximo na direção apertada (atravessa seções).</summary>
        /// <summary>Navegação espacial: vai pro alvo mais próximo na direção apertada (atravessa seções).</summary>
    private void Move(Dir dir)
    {
        if (KbOpen) { KbMove(dir); return; }
        if (ViewerOpen) { ViewerMove(dir); return; }
        if (CpOpen) { CpMove(dir); return; }
        if (_detailOpen && DetailPanelRoot.Visibility == Visibility.Visible) { DetailMove(dir); return; }
        EnsureTopBarTargets();
        if (OverlayOpen() || _navTargets.Count == 0) return;
        _navIndex = Math.Clamp(_navIndex, 0, _navTargets.Count - 1);

        var curEl = _navTargets[_navIndex].Element();
        var cur = curEl != null ? CenterOf(curEl) : null;
        if (cur == null) { _navIndex = 0; HighlightCurrent(); return; }

        bool curIsTop = _navTargets[_navIndex].TopBar;

        int best = -1, bestContent = -1;
        double bestScore = double.MaxValue, bestContentScore = double.MaxValue;
        for (int i = 0; i < _navTargets.Count; i++)
        {
            if (i == _navIndex) continue;
            var el = _navTargets[i].Element();
            if (el == null) continue;
            var c = CenterOf(el);
            if (c == null) continue;

            double dx = c.Value.X - cur.Value.X;
            double dy = c.Value.Y - cur.Value.Y;
            double score;
            switch (dir)
            {
                case Dir.Right: if (Math.Abs(dy) > 40 || dx <= 10) continue; score = dx; break;
                case Dir.Left:  if (Math.Abs(dy) > 40 || dx >= -10) continue; score = -dx; break;
                case Dir.Down:  if (dy <= 40) continue; score = dy * 3 + Math.Abs(dx); break;
                default:        if (dy >= -40) continue; score = -dy * 3 + Math.Abs(dx); break;
            }
            if (score < bestScore) { bestScore = score; best = i; }
            if (!_navTargets[i].TopBar && score < bestContentScore) { bestContentScore = score; bestContent = i; }
        }

        // Subindo a partir de um jogo: se ainda há jogos acima (mesmo rolados para fora da tela),
        // vai neles; o menu do topo só entra quando não sobra nada acima.
        if (dir == Dir.Up && !curIsTop && bestContent >= 0) best = bestContent;

        if (best >= 0)
        {
            _navIndex = best;
            NavSounds.Move(); // SOM
            HighlightCurrent();
        }
    }

    private void ControllerConfirm()
    {
        if (KbOpen) { KbPress(); return; }
        if (ViewerOpen) { CloseShotViewer(); return; }
        if (CpOpen) { CpToggle(); return; }
        if (AddCustomGamePanelRoot?.Visibility == Visibility.Visible) return;

        NavSounds.Confirm(); // SOM

        if (DetailPanelRoot?.Visibility == Visibility.Visible)
        {
            DetailConfirm();
            return;
        }
        if (_navTargets.Count == 0) return;

        var target = _navTargets[Math.Clamp(_navIndex, 0, _navTargets.Count - 1)];
        if (target.Activate != null) target.Activate();
        else if (target.Game != null) OpenDetailPanel(target.Game);
    }

    private void ControllerBack()
    {
        if (ShotViewerRoot.Visibility == Visibility.Visible) { CloseShotViewer(); return; }
        if (CpOpen) { NavSounds.Back(); CloseCollectionPicker(); return; }
        if (KbOpen) { NavSounds.Back(); CloseKeyboard(); return; }
        if (DuplicatesRoot?.Visibility == Visibility.Visible)
        {
            NavSounds.Back();
            CloseDuplicates();
            return;
        }
        if (StatsRoot?.Visibility == Visibility.Visible)
        {
            NavSounds.Back();
            CloseStats();
            return;
        }
        if (DetailPanelRoot?.Visibility == Visibility.Visible)
        {
            NavSounds.Back();
            CloseDetailPanel();
            return;
        }

        // Cursor no topo
        int anchor = TopAnchorOf(_navIndex, out bool isIcon);
        if (anchor >= 0)
        {
            NavSounds.Back();
            if (!isIcon)
            {
                // Item de uma barra aberta: fecha a barra e leva o cursor de volta para o ícone dela
                FrameworkElement icon;
                if (anchor == 1) { TopChipsBar.Visibility = Visibility.Collapsed; icon = NavPlatformsButton; }
                else if (anchor == 2) { EmuChipsBar.Visibility = Visibility.Collapsed; icon = NavEmuButton; }
                else if (anchor == 3) { CollectionChipsBar.Visibility = Visibility.Collapsed; icon = NavCollectionsButton; }
                else { SettingsBar.Visibility = Visibility.Collapsed; icon = SettingsButton; }
                UpdateTopToggles();
                EnsureTopBarTargets();
                int i = _navTargets.FindIndex(t => ReferenceEquals(t.Element(), icon));
                if (i >= 0) _navIndex = i;
            }
            else
            {
                // Ícone: devolve o cursor para os jogos
                int first = _navTargets.FindIndex(t => !t.TopBar);
                _navIndex = first >= 0 ? first : 0;
            }
            ScheduleHighlight();
            return;
        }

        // Pesquisa ativa (feita pelo teclado virtual): B limpa e volta à lista completa
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            NavSounds.Back();
            SearchBox.Clear();
            SearchBar.Visibility = Visibility.Collapsed;
            UpdateTopToggles();
            RefreshHints();
            return;
        }

        // Dentro de um console (aba Emuladores): B volta pra escolha de console
        if (_selectedPlatform == "Emuladores" && _selectedEmulatorSystem != null)
        {
            NavSounds.Back();
            _selectedEmulatorSystem = null;
            _navIndex = 0;
            ApplyFilter();
            return;
        }

        // Início com filtro (plataforma, emulador ou favoritos): B volta ao padrão
        if (IsOnHome && _homeFilter != "Todas")
        {
            NavSounds.Back();
            SetHomeFilter("Todas");
            return;
        }

        // Qualquer outra tela: B volta pra Início
        if (!IsOnHome)
        {
            NavSounds.Back();
            GoToTab("Início");
        }
    }

    private void ControllerPlay()
    {
        if (OverlayBlocksActions) return;
        if (KbOpen) { KbFinish(); return; }
        if (AddCustomGamePanelRoot?.Visibility == Visibility.Visible) return;

        GameEntry? game = null;
        if (DetailPanelRoot?.Visibility == Visibility.Visible)
            game = _currentDetailGame;
        else if (_navTargets.Count > 0)
            game = _navTargets[Math.Clamp(_navIndex, 0, _navTargets.Count - 1)].Game;

        if (game != null && game.IsInstalled)
            LaunchGame(game);
    }

    /// <summary>LB/RB: anda pelos ícones do topo, dando a volta.</summary>
    private void CyclePlatformTab(int direction)
    {
        if (OverlayBlocksActions) return;
        if (KbOpen) return;
        const int IconCount = 6;
        EnsureTopBarTargets();

        int pos = TopAnchorOf(_navIndex, out _);          // -1 = cursor nos jogos
        int next = pos < 0 ? 0 : ((pos + direction) % IconCount + IconCount) % IconCount;

        // Saindo do ícone de uma barra aberta: fecha a barra
        if (pos == 1 && next != 1) TopChipsBar.Visibility = Visibility.Collapsed;
        if (pos == 2 && next != 2) EmuChipsBar.Visibility = Visibility.Collapsed;
        if (pos == 3 && next != 3) CollectionChipsBar.Visibility = Visibility.Collapsed;
        if (pos == 5 && next != 5) SettingsBar.Visibility = Visibility.Collapsed;
        UpdateTopToggles();
        EnsureTopBarTargets();

        // Os 5 primeiros alvos do topo são os ícones
        var icons = new List<int>();
        for (int i = 0; i < _navTargets.Count && icons.Count < IconCount; i++)
            if (_navTargets[i].TopBar) icons.Add(i);
        if (icons.Count < IconCount) return;

        _navIndex = icons[next];
        NavSounds.Move();
        HighlightCurrent();
    }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // SOM: Ctrl+M liga/desliga os sons
        if (e.Key == System.Windows.Input.Key.M &&
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            NavSounds.Toggle();
            return;
        }

        if (e.Key != System.Windows.Input.Key.F11) return;
        e.Handled = true;
        ToggleFullscreen();
    }

    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    private void ToggleFullscreen()
    {
        var titleRow = ((Grid)TitleBar.Parent).RowDefinitions[0];
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);

        if (!_fullscreen)
        {
            _fullscreen = true;
            _stateBeforeFullscreen = WindowState;
            TitleBar.Visibility = Visibility.Collapsed;
            titleRow.Height = new GridLength(0);
            if (chrome != null) chrome.CaptionHeight = 0;   // sem área invisível de arrastar no topo
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;                // força remaximizar cobrindo a tela toda
            WindowState = WindowState.Maximized;
        }
        else
        {
            _fullscreen = false;
            TitleBar.Visibility = Visibility.Visible;
            titleRow.Height = new GridLength(35);
            if (chrome != null) chrome.CaptionHeight = 35;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullscreen;            // volta como estava (normal ou maximizada)
        }
    }

    private void DeleteRomButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameEntry game }) return;
        if (string.IsNullOrWhiteSpace(game.RomFilePath)) return;

        var result = MessageBox.Show(
            $"Apagar o arquivo de \"{game.Name}\" definitivamente do disco?\n\n{game.RomFilePath}",
            "Apagar ROM", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            File.Delete(game.RomFilePath);
            _allGames.Remove(game);
            ApplyFilter(); // atualiza a tela do console sem precisar re-escanear tudo
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não consegui apagar o arquivo: {ex.Message}");
        }
    }

        private bool _launching;
        private bool _launchSawDeactivate;
        private DateTime _launchStartedAt;
        private DispatcherTimer? _launchTimeout;
        private DispatcherTimer? _foregroundTimer;

        // Janelas desses processos NÃO contam como "o jogo abriu": a camada continua atrás delas
        private static readonly HashSet<string> LauncherUiProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "steamservice",
            "epicgameslauncher", "epicwebhelper", "eacefoverlayrenderer",
            "galaxyclient", "galaxyclient helper", "galaxyclientservice",
            "eadesktop", "eabackgroundservice", "ealauncher",
            "upc", "ubisoftconnect", "uplaywebcore", "ubisoftgamelauncher",
            "rgl", "rockstargameslauncher",
            // interface do próprio Windows (bandeja, menu iniciar, busca...)
            "explorer", "shellexperiencehost", "searchhost", "searchapp",
            "startmenuexperiencehost", "textinputhost", "lockapp"
        };

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>
        /// Enquanto a camada de lançamento está aberta e o GameHub perdeu o foco: se quem tomou o foco
        /// não é launcher de loja nem o Windows (ou seja, é o jogo, um launcher do jogo ou um programa), sai de cena.
        /// </summary>
        private void CheckForegroundDuringLaunch()
        {
            if (!_launching || IsActive) return;
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return;
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == (uint)Environment.ProcessId) return;

                using var proc = Process.GetProcessById((int)pid);
                if (LauncherUiProcesses.Contains(proc.ProcessName)) return;

                HideLaunchOverlay();
            }
            catch { /* processo fechou entre a leitura e o acesso: tenta de novo no próximo tick */ }
        }

        private void LaunchGame(GameEntry game)
        {
            if (_launching) return; // já existe um lançamento em andamento

            try
            {
                _launching = true;
                _launchSawDeactivate = false;
                _launchStartedAt = DateTime.Now;
                ShowLaunchOverlay(game);
                NavSounds.Launch(); // SOM
                GameLauncher.Launch(game);
                UserDataService.RecordLaunch(game);
                PlaySessionService.Start(game, () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        PlaySessionService.Apply(_allGames);
                        if (!_detailOpen) ApplyFilter();
                    }
                    catch (Exception ex) { Logger.LogError("Fim da sessão de jogo", ex); }
                })));

                // Reordena as listas com o jogo que acabou de abrir.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyFilter();
                }), DispatcherPriority.Background);

                // Segurança: se nada tomar o foco em 15s (falha ao abrir), a camada sai sozinha
                _launchTimeout?.Stop();
                _launchTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                _launchTimeout.Tick += (_, _) => HideLaunchOverlay();
                _launchTimeout.Start();

                _foregroundTimer?.Stop();
                _foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _foregroundTimer.Tick += (_, _) => CheckForegroundDuringLaunch();
                _foregroundTimer.Start();
            }
            catch (Exception ex)
            {
                HideLaunchOverlay(instant: true);
                MessageBox.Show(ex.Message, "GameHub - Inicialização", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowLaunchOverlay(GameEntry game)
        {
            RefreshHints();
            LaunchTitleText.Text = game.Name;
            LaunchStatusText.Text = game.Platform switch
            {
                GameHub.Models.Platform.Steam    => "Steam iniciando...",
                GameHub.Models.Platform.Epic     => "Epic Games Launcher iniciando...",
                GameHub.Models.Platform.EaApp    => "EA App iniciando...",
                GameHub.Models.Platform.Ubisoft  => "Ubisoft Connect iniciando...",
                GameHub.Models.Platform.Emulador => "Emulador iniciando...",
                _                                => "Iniciando o jogo..."
            };
            LaunchCoverImage.Source = game.CoverImage;
            LaunchBgImage.Source = game.CoverImage;
            LaunchBar.IsIndeterminate = true;

            LaunchOverlay.BeginAnimation(OpacityProperty, null);
            LaunchOverlay.Opacity = 0;
            LaunchOverlay.Visibility = Visibility.Visible;
            LaunchOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        }

        private void HideLaunchOverlay(bool instant = false)
        {
            _launchTimeout?.Stop();
            _foregroundTimer?.Stop();
            _launching = false;
            RefreshHints();

            if (LaunchOverlay.Visibility != Visibility.Visible) return;

            if (instant)
            {
                LaunchOverlay.BeginAnimation(OpacityProperty, null);
                LaunchOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) =>
            {
                if (!_launching) LaunchOverlay.Visibility = Visibility.Collapsed;
            };
            LaunchOverlay.BeginAnimation(OpacityProperty, fade);
        }

    // ──────────────────────────────────────────────────────────────
    // Visuais e Window Chrome
    // ──────────────────────────────────────────────────────────────

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        else
            WindowState = WindowState.Maximized;
    }
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private BitmapSource? _bgSource;

    /// <summary>
    /// Fundo desfocado sem BlurEffect: reduz a capa pra ~24 px de largura e deixa o WPF esticar.
    /// O resultado é uma mancha suave igual a um desfoque forte, mas sem recalcular um efeito
    /// da janela inteira a cada quadro de animação (era isso que enchia a GPU no modo controle).
    /// </summary>
        private void SetDynamicBackground(BitmapSource? cover)
    {
        if (cover == null || cover.PixelWidth <= 0) return;

        bool home = _selectedPlatform == "Início";
        int target = home ? 160 : 24;
        DynamicBgImage.Opacity = home ? 0.6 : 0.18;
        BgShade.Opacity = home ? 0.55 : 1.0;
        if (ReferenceEquals(_bgSource, cover) && _bgTarget == target) return;

        _bgSource = cover;
        _bgTarget = target;
        double scale = (double)target / cover.PixelWidth;
        var small = new TransformedBitmap(cover, new ScaleTransform(scale, scale));
        small.Freeze();
        DynamicBgImage.Source = small;
    }

    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_controllerMode) return;
        if (sender is FrameworkElement { Tag: GameEntry game })
            _ = ApplyHomeBackgroundAsync(game);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GameEntry game })
        {
            NavSounds.Confirm();
            OpenDetailPanel(game);
        }
    }

    private void OpenDetailPanel(GameEntry game)
    {
        _currentDetailGame = game;
        UpdateCardArtButton();
        _detailOpen = true;
        ClearHighlight(); // o card de trás não precisa pulsar com o painel por cima
        PopulateDetailPanel(game);
        UpdatePadGlyphs();
        _dnRow = 0; _dnStatus = 0; _dnShot = 0;
        DetailScroll.ScrollToVerticalOffset(0);
        ApplyDetailControllerLayout();
        DetailHighlight();
        RefreshHints();
        DetailPanelRoot.Visibility = Visibility.Visible;
        // Painel de detalhe (ZIndex=50) precisa ficar acima do bloqueador no modo controle
        if (MouseBlocker != null) Panel.SetZIndex(MouseBlocker, 40);
        var anim = new DoubleAnimation(700, 0, TimeSpan.FromSeconds(0.28))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        DetailPanelTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

        private void CloseDetail_Click(object sender, RoutedEventArgs e) { NavSounds.Back(); CloseDetailPanel(); }

    private void DetailOverlay_Click(object sender, MouseButtonEventArgs e) { NavSounds.Back(); CloseDetailPanel(); }

    private void CloseDetailPanel()
    {
        _detailOpen = false;
        _detailRichVersion++; 
        CloseShotViewer();
        RefreshHints();
        ScheduleHighlight(); // volta o destaque do controle no card
        // Restaura ZIndex do bloqueador
        if (MouseBlocker != null) Panel.SetZIndex(MouseBlocker, 200);
        var anim = new DoubleAnimation(0, 700, TimeSpan.FromSeconds(0.22))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            DetailPanelRoot.Visibility = Visibility.Collapsed;
            _currentDetailGame = null;
        };
        DetailPanelTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
    }

    private static Image BuildPlatformChip(PlatformBadge badge, bool small = false)
    {
        var size = small ? 18 : 26;
        var img = new Image
        {
            Source = new BitmapImage(new Uri(badge.IconPath, UriKind.Absolute)),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 0, 6, 6)
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    private void DetailPlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame != null)
            LaunchGame(_currentDetailGame);
    }

    private void ChangeCoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDetailGame == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "Escolher capa",
            Filter = "Imagens (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|Todos os arquivos|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        if (!CoverService.SetManualCover(_currentDetailGame, dialog.FileName))
        {
            MessageBox.Show("Não consegui usar essa imagem como capa.");
            return;
        }

        // Atualiza a capa grande no painel de detalhe e o card na grade, sem re-escanear tudo
        if (_currentDetailGame.CoverImage != null)
            DetailCoverImage.Source = _currentDetailGame.CoverImage;
        ApplyFilter();
    }

    private void PopulateDetailPanel(GameEntry game)
    {
        // Título
        DetailTitleText.Text = game.Name;

        // Plataformas - um selo colorido por loja, em vez de texto que cortava
        DetailPlatformPanel.Children.Clear();
        foreach (var badge in game.PlatformBadges)
            DetailPlatformPanel.Children.Add(BuildPlatformChip(badge));

        // Jogo de emulador não tem PlatformBadges (não é loja) - mostra o console em texto
        if (!string.IsNullOrWhiteSpace(game.EmulatorSystem))
        {
            DetailPlatformPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock
                {
                    Text = game.EmulatorSystem,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC))
                }
            });
        }

        // Capa
        DetailCoverImage.Source = game.CoverImage;

        // Faixa do topo: fundo do jogo; sem fundo, a capa desfocada como antes
        HeroBgImage.Source = game.CoverImage;
        HeroBgImage.Effect = new BlurEffect { Radius = 25 };
        HeroBgImage.Opacity = 0.3;
        _ = LoadDetailHeroAsync(game);

        // Tempo jogado - uma linha por plataforma (nunca soma nem escolhe "a maior")
        DetailPlaytimePanel.Children.Clear();
        var withTime = game.PlaytimeByPlatform.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList();

        if (withTime.Count == 0)
        {
            DetailPlaytimePanel.Children.Add(new TextBlock
            {
                Text = game.PlaytimeMinutes > 0 ? $"{game.PlaytimeMinutes / 60}h {game.PlaytimeMinutes % 60}m" : "Nunca jogado",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
            });
        }
        else
        {
            foreach (var kv in withTime)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
                var iconPath = PlatformStyle.IconPath(kv.Key);
                if (!string.IsNullOrEmpty(iconPath))
                {
                    row.Children.Add(BuildPlatformChip(
                        new PlatformBadge { Letter = PlatformStyle.Letter(kv.Key), ColorHex = PlatformStyle.ColorHex(kv.Key), IconPath = iconPath },
                        small: true));
                }
                else
                {
                    // Emulador/Modificado não têm logo: mostra o nome
                    row.Children.Add(new TextBlock
                    {
                        Text = kv.Key == Platform.Emulador ? "Emulador" : kv.Key == Platform.Modificado ? "Modificado" : kv.Key.ToString(),
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 4, 0)
                    });
                }
                row.Children.Add(new TextBlock
                {
                    Text = $"{kv.Value / 60}h {kv.Value % 60}m",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(5, 0, 0, 0)
                });
                DetailPlaytimePanel.Children.Add(row);
            }
        }

        // Última sessão
        DetailLastPlayedText.Text = game.LastPlayedFormatted;

        // Nota crítica
        if (game.CriticsScore.HasValue && game.CriticsScore > 0)
        {
            DetailScoreText.Text = $"{game.CriticsScore:F0}/100";
            DetailScoreText.Foreground = game.CriticsScore >= 75
                ? new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81))
                : game.CriticsScore >= 50
                    ? new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B))
                    : new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            DetailScoreBorder.Visibility = Visibility.Visible;
        }
        else
        {
            DetailScoreBorder.Visibility = Visibility.Collapsed;
        }

        // Chips de gênero
        DetailGenrePanel.Children.Clear();
        foreach (var genre in game.Genres)
        {
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6)
            };
            chip.Child = new TextBlock
            {
                Text = genre,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
            };
            DetailGenrePanel.Children.Add(chip);
        }

        // Dev & Publisher
        DetailDevText.Text = string.IsNullOrWhiteSpace(game.Developer) ? "Não informado" : game.Developer;
        DetailPubText.Text = string.IsNullOrWhiteSpace(game.Publisher) ? "Não informado" : game.Publisher;

        // Resumo
        DetailSummaryText.Text = string.IsNullOrWhiteSpace(game.Summary)
            ? "Nenhum resumo detalhado foi encontrado para este jogo no momento."
            : game.Summary;

        // Botão jogar
        if (game.IsInstalled)
        {
            DetailPlayButton.Visibility = Visibility.Visible;
            DetailNotInstalledBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailPlayButton.Visibility = Visibility.Collapsed;
            DetailNotInstalledBadge.Visibility = Visibility.Visible;
        }

        UpdateDetailUserButtons(game);
        ResetDetailRich(game);
    }

        private int _detailHeroVersion;

    private async Task LoadDetailHeroAsync(GameEntry game)
    {
        int v = ++_detailHeroVersion;
        BitmapSource? bg = null;
        try { bg = await BackgroundService.GetAsync(game); }
        catch (Exception ex) { Logger.LogError("Fundo do painel", ex); }

        // Já abriu outro jogo ou fechou o painel enquanto carregava
        if (v != _detailHeroVersion || !ReferenceEquals(_currentDetailGame, game) || bg == null) return;

        HeroBgImage.Effect = null;
        HeroBgImage.Source = bg;
        HeroBgImage.Opacity = 0.9;
    }
    private void LaunchOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        // 3s de folga para o clique em "Jogar" não fechar a camada na hora
        if (_launching && (DateTime.Now - _launchStartedAt).TotalSeconds > 3)
            HideLaunchOverlay();
    }

        private void StartClock()
    {
        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();
    }

    private void UpdateClock()
    {
        var text = DateTime.Now.ToString("HH:mm");
        if (ClockText.Text != text) ClockText.Text = text; // só mexe na tela quando o minuto muda
    }

    private void OnControllerBattery(ControllerBatteryInfo info) => Dispatcher.BeginInvoke(new Action(() =>
    {
        // Fica no startup_log.txt: mostra o que o controle informa de verdade
        StartupLog.Mark($"controle: conectado={info.Connected} tipo={info.Type} nivel={info.Level}");

                if (!info.Connected || info.Type == 0)
        {
            ControllerBatteryText.Text = "sem controle";
            ControllerBatteryText.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            ControllerBatteryPill.Visibility = Visibility.Visible;
            return;
        }

        string text;
        Color color;

        switch (info.Type)
        {
            case 1: // o receptor diz "com fio": não é leitura de bateria
                text = "conectado";
                color = Color.FromRgb(0x10, 0xB9, 0x81);
                break;
            case 2:
            case 3: // bateria: o XInput só tem 4 níveis
                (text, color) = info.Level switch
                {
                    0 => ("vazia", Color.FromRgb(0xEF, 0x44, 0x44)),
                    1 => ("baixa", Color.FromRgb(0xF9, 0x73, 0x16)),
                    2 => ("média", Color.FromRgb(0xFB, 0xBF, 0x24)),
                    _ => ("cheia", Color.FromRgb(0x10, 0xB9, 0x81))
                };
                break;
            default: // o controle não informa
                text = "conectado";
                color = Color.FromRgb(0x9C, 0xA3, 0xAF);
                break;
        }

        ControllerBatteryText.Text = info.Type is 2 or 3 ? "bateria " + text : text;
        ControllerBatteryText.Foreground = new SolidColorBrush(color);
        ControllerBatteryPill.Visibility = Visibility.Visible;
    }));
}
