using System.IO;
using Microsoft.Win32;
using GameHub.Models;

namespace GameHub.Scanners;

public class GogScanner : IGameScanner
{
    public Platform Platform => Platform.Gog;

    public List<GameEntry> Scan()
    {
        var games = new List<GameEntry>();

        using var gamesKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                              ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (gamesKey == null) return games;

        foreach (var subKeyName in gamesKey.GetSubKeyNames())
        {
            using var gameKey = gamesKey.OpenSubKey(subKeyName);
            if (gameKey == null) continue;

            // O GOG grava o caminho absoluto de cada jogo individualmente,
            // então não importa em qual HD cada um foi instalado.
            var name = gameKey.GetValue("gameName") as string;
            var path = gameKey.GetValue("path") as string;
            var exe = gameKey.GetValue("exe") as string ?? gameKey.GetValue("EXE") as string;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                continue;

            var fullExePath = exe != null
                ? (Path.IsPathRooted(exe) ? exe : Path.Combine(path, exe))
                : null;

            games.Add(new GameEntry
            {
                Name = name,
                Platform = Platform.Gog,
                Platforms = new List<Platform> { Platform.Gog },
                InstalledPlatforms = new List<Platform> { Platform.Gog },
                AppId = subKeyName,
                InstallPath = path,
                LaunchExecutable = fullExePath
            });
        }

        return games;
    }
}
