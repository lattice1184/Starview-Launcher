using System.Diagnostics;

namespace Launcher.Core.Multiplayer;

/// <summary>
/// 联机一键修复（AL44）：清理残留陶瓦进程/锁文件 + 模块重装。
/// 残留实例抢占 7000 端口是「版本不匹配/正被其他启动器使用」的真机根因（08-09 日志 meta校验=失败 每行都有）。
/// 仅用户显式点「一键修复」时执行——杀全机 terracotta 进程属预期行为。
/// </summary>
public static class TerracottaRepairService
{
    /// <summary>锁文件（进程间互斥端口）；残留时新实例 meta 校验失败。与 Lobby 读取一致（%TEMP%/tmp），
    /// 8-29 修复：原指向 %LocalAppData%\terracotta 是错误路径，永远删不到 daemon 真锁。</summary>
    public static string LockPath => TerracottaProvisioningService.LockPath;

    /// <summary>杀掉本机全部 terracotta 进程（安装 exe 统一命名）+ 删锁文件；返回击杀数</summary>
    public static int KillStaleInstances()
    {
        var killed = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName("terracotta"))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                    killed++;
                }
                catch { /* 进程已退出/无权限 */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 进程枚举失败不阻断 */ }
        try { File.Delete(LockPath); } catch { }
        if (killed > 0 || File.Exists(LockPath) == false)
            MultiplayerLog.Log($"一键修复: 清理残留实例 {killed} 个，锁文件已删");
        return killed;
    }
}
