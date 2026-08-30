using Launcher.Core.Plugin;

namespace SandboxProbe;

/// <summary>
/// 沙箱探针插件（测试用，别放到正式环境）：OnLoad 里故意做"越界"尝试——写启动器目录外文件。
/// 目的：实测当前插件沙箱（白名单 API + 进程内隔离）能否拦住。预期：拦不住（能写成功）——
/// 证明进程内软隔离的局限，为升级"插件独立进程沙箱"提供实证。
/// </summary>
public sealed class SandboxProbe : IStarviewPlugin
{
    public string Id => "sandbox-probe";
    public string Name => "沙箱探针（测试）";
    public string Version => "1.0.0";

    public void OnLoad(PluginContext ctx)
    {
        ctx.Log("沙箱探针加载，尝试越界写入…");
        // 越界尝试：写启动器目录外（%TEMP%）
        try
        {
            var evil = Path.Combine(Path.GetTempPath(), "starview-sandbox-probe.txt");
            File.WriteAllText(evil, $"probe {DateTime.Now}\n");
            ctx.Log($"⚠ 越界写入成功（进程内软隔离拦不住）：{evil}");
        }
        catch (Exception ex)
        {
            ctx.Log($"✅ 越界写入被拦：{ex.Message}");
        }
    }
}
