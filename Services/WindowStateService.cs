using System.IO;
using System.Text.Json;
using System.Windows;

namespace GameHub.Services;

/// <summary>Guarda e restaura tamanho, posição e maximizada da janela principal.</summary>
public static class WindowStateService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub", "window_state.json");

    private class Data
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }
    }

    public static void Restore(Window w)
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
            if (d == null || d.Width < w.MinWidth || d.Height < w.MinHeight) return;

            // A janela precisa cair (em boa parte) dentro da área de tela atual, senão ela abriria fora
            // da vista (monitor desligado, resolução trocada)
            double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            bool visible = d.Left + d.Width > vl + 100 && d.Left < vl + vw - 100
                        && d.Top + 50 > vt && d.Top < vt + vh - 100;
            if (!visible) return;

            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = d.Left;
            w.Top = d.Top;
            w.Width = d.Width;
            w.Height = d.Height;
            if (d.Maximized) w.WindowState = WindowState.Maximized;
        }
        catch (Exception ex) { Logger.LogError("Restaurar janela", ex); }
    }

    public static void Save(Window w)
    {
        try
        {
            var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;

            var d = new Data
            {
                Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
                Maximized = w.WindowState == WindowState.Maximized
            };
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(d));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Logger.LogError("Salvar janela", ex); }
    }
}