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
        // 不再「先 rm 后 cp」——mv 整体备份再拷全新目录（散文件分支）
        Assert.Contains($"mv '{Root}' \"$BACKUP\"", s);
        Assert.Contains($"cp -R '{Staging}/.' '{Root}/'", s);
        Assert.DoesNotContain("rm -f Launcher.App", s); // 关键：不再先删旧可执行
    }

    /// <summary>8-31 macOS .app bundle 适配：staging 是 bundle → bundle 分支拷 Contents、runtimeconfig 双布局预检。
    /// 旧版只认散文件路径（runtimeconfig 预检必失败）→ 朋友 Mac 更新「覆盖没用」根因。</summary>
    [Fact]
    public void BundleLayout_CopiesIntoAppContents_NotNested()
    {
        var s = Script();
        // bundle staging → bundle root：拷 Contents（不产生 .app/Starview.app 嵌套）
        Assert.Contains($"cp -R '{Staging}/Starview.app/Contents/.' '{Root}/Contents/'", s);
        // bundle 的 runtimeconfig 预检在 bundle 路径（旧版只查散文件路径 → 必失败）
        Assert.Contains($"[ -f '{Staging}/Starview.app/Contents/MacOS/Launcher.App.runtimeconfig.json' ]", s);
        // 备份移到用户可写目录（避免 /Applications 父目录只读）
        Assert.Contains("mkdir -p", s);
        Assert.Contains("BACKUP=", s);
        Assert.Contains($"xattr -dr com.apple.quarantine '{Root}'", s); // Apple Silicon 防 Gatekeeper 拦
        Assert.Contains("codesign --force --sign -", s);
        // 失败标记：下次启动可弹提示（不再静默失败）
        Assert.Contains("update-failed.txt", s);
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
