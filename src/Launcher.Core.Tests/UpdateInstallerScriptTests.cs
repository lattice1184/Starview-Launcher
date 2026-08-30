using Launcher.Core.Update;

namespace Launcher.Core.Tests;

/// <summary>
/// 8-31 Linux 更新脚本加固：原子替换 + set -e + 日志 + 回滚 + 验证。
/// 防「先删后拷」在 cp 失败时目录空/打不开（实测：更新后 Launcher.App 消失）。
/// </summary>
public class UpdateInstallerScriptTests
{
    private const string Root = "/home/u/starview";
    private const string Staging = "/tmp/starview-install-abc";
    private const string Ready = "/tmp/starview-update/starview-linux-x64-20260831.tar.gz";
    private const string Log = "/home/u/.local/share/starview/logs/update-install.log";

    private static string Script()
        => UpdateInstaller.BuildApplyScript(Root, Staging, Ready, Log);

    [Fact]
    public void HasSetE_AndLogRedirect()
    {
        var s = Script();
        Assert.Contains("set -e", s);
        Assert.Contains($"exec >> '{Log}' 2>&1", s);
    }

    [Fact]
    public void AtomicReplace_MvBackupBeforeCopy()
    {
        var s = Script();
        // 不再「先 rm 后 cp」——mv 整体备份再拷全新目录
        Assert.Contains($"mv '{Root}' \"$BACKUP\"", s);
        Assert.Contains($"cp -R '{Staging}' '{Root}'", s);
        Assert.DoesNotContain("rm -f Launcher.App", s); // 关键：不再先删旧可执行
    }

    [Fact]
    public void HasRollback_OnFailure()
    {
        var s = Script();
        // cp 失败 / 新目录无效 → 回滚旧版
        Assert.Contains("rolled back", s);
        Assert.Contains($"mv \"$BACKUP\" '{Root}'", s);
        Assert.Contains("rm -rf '" + Root + "'", s); // 回滚前删掉失败的新目录
    }

    [Fact]
    public void HasStagingValidation_AndChmod_AndRestart()
    {
        var s = Script();
        Assert.Contains("staging missing executable", s);
        Assert.Contains("staging missing runtimeconfig", s);
        Assert.Contains("chmod +x", s);
        Assert.Contains("[ ! -x \"$NEWAPP\" ]", s);
        Assert.Contains("nohup \"$NEWAPP\" >/dev/null 2>&1 &", s);
        Assert.Contains("sleep 3", s); // 等旧进程退出
    }

    [Fact]
    public void PreservesReadyOnFailure_CleansOnSuccess()
    {
        var s = Script();
        // 成功才清备份/staging/下载包
        Assert.Contains($"rm -f '{Ready}'", s);
        Assert.Contains("rm -rf \"$BACKUP\"", s);
        Assert.Contains($"rm -rf '{Staging}'", s);
    }
}
