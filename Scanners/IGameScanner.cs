using GameHub.Models;

namespace GameHub.Scanners;

public interface IGameScanner
{
    Platform Platform { get; }
    List<GameEntry> Scan();
}
