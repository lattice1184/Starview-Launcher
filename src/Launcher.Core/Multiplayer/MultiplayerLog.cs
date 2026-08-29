using System.IO;

namespace Launcher.Core.Multiplayer;

/// <summary>
/// 联机会话日志：追加到 %AppData%\Launcher\logs\multiplayer.log。
/// 会话状态机/异常路径全打点，联机问题排查靠它（不再靠陶瓦侧 application.log 猜）。
/// </summary>
public static class MultiplayerLog
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(
        Launcher.Core.Utils.AppPaths.DataRoot, "logs", "multiplayer.log");

    /// <summary>追加一行（毫秒级时间戳）。线程安全；写失败静默（日志不能成为新故障源）。</summary>
    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 忽略：日志不可用时不影响联机功能
        }
    }
}
