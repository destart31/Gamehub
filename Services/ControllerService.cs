using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GameHub.Services;

/// <summary>Estado da bateria do controle. Type: 0 sem controle, 1 com fio, 2/3 bateria (alcalina/recarregável), 255 sem leitura. Level: 0 vazia, 1 baixa, 2 média, 3 cheia.</summary>
public record ControllerBatteryInfo(bool Connected, byte Type, byte Level);

/// <summary>
/// Lê o estado do controle via XInput (P/Invoke direto - sem NuGet).
/// Funciona com qualquer controle XInput: Xbox, 8BitDo Ultimate 2C, etc.
/// </summary>
public class ControllerService : IDisposable
{
    // ── XInput structs ──────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte  bLeftTrigger;
        public byte  bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint          dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_BATTERY_INFORMATION
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint dwUserIndex, ref XINPUT_STATE pState);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint XInputGetBatteryInformation(uint dwUserIndex, byte devType, ref XINPUT_BATTERY_INFORMATION pBatteryInformation);

    // Botões (wButtons bitmask)
    private const ushort XINPUT_GAMEPAD_DPAD_UP    = 0x0001;
    private const ushort XINPUT_GAMEPAD_DPAD_DOWN  = 0x0002;
    private const ushort XINPUT_GAMEPAD_DPAD_LEFT  = 0x0004;
    private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    private const ushort XINPUT_GAMEPAD_START       = 0x0010;
    private const ushort XINPUT_GAMEPAD_BACK        = 0x0020;
    private const ushort XINPUT_GAMEPAD_LEFT_THUMB  = 0x0040;
    private const ushort XINPUT_GAMEPAD_RIGHT_THUMB = 0x0080;
    private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER  = 0x0100;
    private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    private const ushort XINPUT_GAMEPAD_A = 0x1000;
    private const ushort XINPUT_GAMEPAD_B = 0x2000;
    private const ushort XINPUT_GAMEPAD_X = 0x4000;
    private const ushort XINPUT_GAMEPAD_Y = 0x8000;

    // Dead zone do analógico (~30%)
    private const short DEAD_ZONE = 10000;

    // ── Eventos que a MainWindow assina ────────────────────────────────────
    /// <summary>Disparado quando qualquer botão/analógico é ativado - usado pra ativar modo controle.</summary>
    public event Action? AnyInput;
    public event Action? DPadUp;
    public event Action? DPadDown;
    public event Action? DPadLeft;
    public event Action? DPadRight;
    public event Action? ButtonA;    // Confirmar / Abrir detalhe
    public event Action? ButtonB;    // Voltar / Fechar
    public event Action? ButtonX;    // Trocar capa
    public event Action? ButtonY;
    public event Action? LeftShoulder;   // LB - aba anterior
    public event Action? RightShoulder;  // RB - próxima aba
    public event Action? Start;          // Jogar

    /// <summary>Disparado (em thread de fundo) quando o controle liga/desliga ou a bateria muda.</summary>
    public event Action<ControllerBatteryInfo>? BatteryChanged;

    private CancellationTokenSource? _cts;
    private bool _wasConnected;
    private DateTime _nextBatteryCheck = DateTime.MinValue;
    private ControllerBatteryInfo? _lastBattery;
    private ushort _prevButtons;
    private bool _prevStickUp, _prevStickDown, _prevStickLeft, _prevStickRight;

    // Repeat: segura a direcao -> 1a acao imediata, repete apos 400ms inicial, depois a cada 150ms
    private DateTime _repeatStart = DateTime.MinValue;
    private DateTime _repeatNext  = DateTime.MinValue;
    private StickDir? _repeatDir;

    public void Begin()
    {
        _cts = new CancellationTokenSource();
        Task.Run(() => PollLoop(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(50, ct); // ~20 fps de polling

            try
            {
                var state = new XINPUT_STATE();
                bool connected = XInputGetState(0, ref state) == 0;

                // Bateria: lê quando o controle liga/desliga e depois a cada 30 s (muda devagar)
                if (connected != _wasConnected || (connected && DateTime.UtcNow >= _nextBatteryCheck))
                {
                    _wasConnected = connected;
                    _nextBatteryCheck = DateTime.UtcNow.AddSeconds(30);
                    ReportBattery(connected);
                }

                if (!connected) continue; // controle desconectado

                var btn = state.Gamepad.wButtons;
                var lx  = state.Gamepad.sThumbLX;
                var ly  = state.Gamepad.sThumbLY;

                // Analógico esquerdo → direções (com dead zone)
                bool stickUp    = ly >  DEAD_ZONE;
                bool stickDown  = ly < -DEAD_ZONE;
                bool stickLeft  = lx < -DEAD_ZONE;
                bool stickRight = lx >  DEAD_ZONE;

                // Analogico: disparo imediato no primeiro frame, repeat depois de 400ms e a cada 150ms
                StickDir? stickDir = stickUp ? StickDir.Up : stickDown ? StickDir.Down : stickLeft ? StickDir.Left : stickRight ? StickDir.Right : null;

                // Notifica que o controle foi usado SÓ quando há input real novo
                // (botão pressionado ou analógico saiu do neutro) - ignora ruído
                bool anyBtn = (btn & ~_prevButtons) != 0; // borda de subida de botão
                bool stickActivated = stickDir != null && _repeatDir == null; // analógico saiu do neutro
                if (anyBtn || stickActivated) AnyInput?.Invoke();

                // Só dispara na borda (quando pressiona, não enquanto segura)
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_DPAD_UP,         DPadUp);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_DPAD_DOWN,        DPadDown);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_DPAD_LEFT,        DPadLeft);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_DPAD_RIGHT,       DPadRight);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_A,                ButtonA);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_B,                ButtonB);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_X,                ButtonX);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_Y,                ButtonY);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_LEFT_SHOULDER,    LeftShoulder);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_RIGHT_SHOULDER,   RightShoulder);
                FireIfNew(btn, _prevButtons, XINPUT_GAMEPAD_START,            Start);
                bool newStick = stickDir != null && stickDir != _repeatDir;
                if (newStick)
                {
                    _repeatDir   = stickDir;
                    _repeatStart = DateTime.Now;
                    _repeatNext  = DateTime.Now.AddMilliseconds(400);
                    FireStick(stickDir!.Value);
                }
                else if (stickDir != null && DateTime.Now >= _repeatNext)
                {
                    _repeatNext = DateTime.Now.AddMilliseconds(150);
                    FireStick(stickDir.Value);
                }
                else if (stickDir == null)
                {
                    _repeatDir = null;
                }

                _prevButtons    = btn;
                _prevStickUp    = stickUp;
                _prevStickDown  = stickDown;
                _prevStickLeft  = stickLeft;
                _prevStickRight = stickRight;
            }
            catch
            {
                // Ignora falhas pontuais (ex: erro no XInput) para não derrubar a thread de polling inteira
            }
        }
    }

    private void ReportBattery(bool connected)
    {
        byte type = 0, level = 0;

        if (connected)
        {
            try
            {
                var info = new XINPUT_BATTERY_INFORMATION();
                if (XInputGetBatteryInformation(0, 0, ref info) == 0) // 0 = gamepad
                {
                    type = info.BatteryType;
                    level = info.BatteryLevel;
                }
                else
                {
                    type = 255;
                }
            }
            catch
            {
                type = 255;
            }
        }

        var current = new ControllerBatteryInfo(connected, type, level);
        if (current == _lastBattery) return;

        _lastBattery = current;
        BatteryChanged?.Invoke(current);
    }

    private enum StickDir { Up, Down, Left, Right }

    private void FireStick(StickDir dir)
    {
        switch (dir)
        {
            case StickDir.Up:    DPadUp?.Invoke();    break;
            case StickDir.Down:  DPadDown?.Invoke();  break;
            case StickDir.Left:  DPadLeft?.Invoke();  break;
            case StickDir.Right: DPadRight?.Invoke(); break;
        }
    }

    private static void FireIfNew(ushort cur, ushort prev, ushort mask, Action? handler)
    {
        if ((cur & mask) != 0 && (prev & mask) == 0)
            handler?.Invoke();
    }

    public void Dispose() => Stop();
}
