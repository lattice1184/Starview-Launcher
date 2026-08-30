using System.Diagnostics;

namespace Launcher.App.Services;

/// <summary>
/// 资源管理器打开文件夹统一入口（设置页「打开文件夹」等）。
/// 目录不存在时自动创建——explorer 打不存在的路径会弹报错窗。
/// </summary>
public static class FolderOpener
{
    public static void Open(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* 打开失败静默：非核心功能，不打扰用户 */ }
    }
}
