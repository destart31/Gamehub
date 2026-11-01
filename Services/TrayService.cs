using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace GameHub.Services;

/// <summary>
/// Ícone do GameHub na bandeja do Windows (área de notificação).
/// Clique esquerdo abre a janela; clique direito mostra "Abrir o GameHub" e "Sair".
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir o GameHub", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitRequested?.Invoke());

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "GameHub",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    /// <summary>Usa o mesmo ícone da janela (Assets\app.ico, embutido no .exe).</summary>
    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (info != null)
            {
                using var stream = info.Stream;
                return new Drawing.Icon(stream);
            }
        }
        catch (Exception ex) { Logger.LogError("Ícone da bandeja", ex); }

        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false; // senão o ícone fica "fantasma" na bandeja até passar o mouse
        _icon.Dispose();
    }
}
