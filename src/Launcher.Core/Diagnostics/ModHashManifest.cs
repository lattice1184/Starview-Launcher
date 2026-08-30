using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Core.Diagnostics;

/// <summary>
/// 模组哈希清单（8-30 投毒检测）：记录启动器自己安装的 mod 的官方 SHA1，
/// 启动预检时重算比对——文件被替换/投毒（同名不同内容）即检出。
/// 清单存 {modsDir}/.starview-mods.json；手动放进 mods 目录的 mod 不在清单 → 不拦（标未校验）。
/// 边界：能抓"同名不同内容被替换"；换文件名的投毒靠指纹/命名匹配，本期不覆盖。
/// </summary>
public static class ModHashManifest
{
    public sealed record Entry(string FileName, string Sha1, string Sha512, string Source);

    private sealed record ManifestFile(List<Entry> Mods);

    private static string PathFor(string modsDir) => System.IO.Path.Combine(modsDir, ".starview-mods.json");

    /// <summary>记录一个已安装 mod 的官方哈希（sha1 空 / 文件名为空不记录——无哈希可比对）。</summary>
    public static void Record(string modsDir, string fileName, string? sha1, string? sha512, string source)
    {
        if (string.IsNullOrWhiteSpace(sha1) || string.IsNullOrWhiteSpace(fileName)) return;
        try
        {
            var entries = Load(modsDir);
            entries.RemoveAll(e => string.Equals(e.FileName, fileName, System.StringComparison.OrdinalIgnoreCase));
            entries.Add(new Entry(fileName, sha1, sha512 ?? "", source));
            Save(modsDir, entries);
        }
        catch { /* 记录失败不影响安装 */ }
    }

    /// <summary>校验结果：Tampered = 清单内被替换/删除的 mod；Untracked = 目录里清单外的 jar（手动放入/未校验）。</summary>
    public sealed record VerifyResult(List<string> Tampered, List<string> Untracked);

    /// <summary>
    /// 重算清单内每个 jar 的 SHA1 比对 + 扫描目录找清单外 jar（手动塞的/未校验）。
    /// 8-31 主动隔离的前置：Tampered（投毒）由 App 层弹 Warn 引导严格隔离；Untracked（未校验）
    /// 弹 Confirm。校验失败不阻断启动（读目录异常返回空）。
    /// </summary>
    public static async Task<VerifyResult> VerifyAsync(string modsDir, CancellationToken ct = default)
    {
        var tampered = new List<string>();
        var untracked = new List<string>();
        try
        {
            var entries = Load(modsDir);
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = System.IO.Path.Combine(modsDir, e.FileName);
                if (!File.Exists(path)) { tampered.Add($"{e.FileName}（已删除）"); continue; }
                if (!await Download.DownloadService.Sha1MatchesAsync(path, e.Sha1, ct))
                    tampered.Add($"{e.FileName}（哈希不一致）");
            }
            // 8-31 C：扫描目录找清单外 jar（手动放入/未校验）——投毒常见入口是"下载站替换 + 手动塞进 mods"
            foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar"))
            {
                ct.ThrowIfCancellationRequested();
                var name = System.IO.Path.GetFileName(jar);
                if (!entries.Any(e => string.Equals(e.FileName, name, System.StringComparison.OrdinalIgnoreCase)))
                    untracked.Add(name);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 校验失败不阻断启动 */ }
        return new VerifyResult(tampered, untracked);
    }

    private static List<Entry> Load(string modsDir)
    {
        try
        {
            var path = PathFor(modsDir);
            if (!File.Exists(path)) return [];
            var doc = JsonSerializer.Deserialize<ManifestFile>(File.ReadAllText(path));
            return doc?.Mods ?? [];
        }
        catch { return []; }
    }

    private static void Save(string modsDir, List<Entry> entries)
    {
        Directory.CreateDirectory(modsDir);
        File.WriteAllText(PathFor(modsDir), JsonSerializer.Serialize(new ManifestFile(entries)));
    }
}
