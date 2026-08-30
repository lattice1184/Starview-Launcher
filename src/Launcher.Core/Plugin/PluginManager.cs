using System.Diagnostics;
using System.Security.Cryptography;
using Launcher.Core.Utils;
using Microsoft.Extensions.Logging;

namespace Launcher.Core.Plugin;

/// <summary>
/// 插件加载器（8-31 MVP）：扫描 plugins/*.dll → 每个用独立 collectible ALC 加载 →
/// 反射找 IStarviewPlugin 实现 → OnLoad → 登记。坏插件跳过不拖垮。
/// 防投毒：plugins/.starview-plugins.json 记录每个 dll 的 SHA1（首次加载记录），
/// 后续加载前重算比对——不一致 = 掉包 → 跳过 + 日志警告（对齐 mod 哈希投毒检测思路）。
/// 8-31 升级：保留运行句柄（实例/ALC/上下文）支持运行时停用/删除；新增 Import/Delete/ListPlugins。
/// </summary>
public sealed class PluginManager
{
    public static PluginManager Instance { get; } = new();

    /// <summary>插件管理页列表行：已加载插件（含 Name/Version），或仅盘上有文件（Name/Version 未知）。</summary>
    public sealed record PluginDescriptor(
        string FileName, string FilePath, string? Id, string? Name, string? Version,
        bool IsLoaded, bool Enabled, PluginStatus Status);

    /// <summary>导入/删除/启停操作结果（Ok=false 带原因；Ok=true 且 Deferred 表示"重启后生效"）。</summary>
    public sealed record PluginOpResult(bool Ok, string? Message, bool Deferred = false);

    private readonly List<PluginDescriptor> _plugins = [];
    private readonly Dictionary<string, PluginLoader.Loaded> _runtime = [];
    private readonly object _gate = new();

    /// <summary>总开关（LauncherSettings.EnablePlugins，默认关——插件未成熟前不开）</summary>
    public bool Enabled => LauncherSettings.Current.EnablePlugins;

    internal static string? PluginsDirOverride;

    private static string PluginsDir => PluginsDirOverride ?? System.IO.Path.Combine(AppPaths.DataRoot, "plugins");
    private static string HashFile => System.IO.Path.Combine(PluginsDir, ".starview-plugins.json");

    /// <summary>待删墓碑文件：插件文件被占用删不掉时记录，下次启动清除（防复活）。</summary>
    private static string PendingDeleteFile => System.IO.Path.Combine(PluginsDir, ".pending-delete.txt");

    /// <summary>扫描加载 plugins/ 目录的插件。静默失败不阻断启动器（坏插件跳过）。</summary>
    public void Load()
    {
        if (!Enabled) return;
        try
        {
            var dir = PluginsDir;
            if (!Directory.Exists(dir)) return;
            CleanupPendingDeletes(); // 上次删除被占用的文件，重启时补删（防复活）
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                try { LoadIfDue(dll); }
                catch { /* 单个插件失败不拖垮其余 */ }
            }
        }
        catch { /* 插件目录整体异常不阻断启动 */ }
    }

    /// <summary>列出 plugins/ 目录全部插件（含停用/未加载的），供管理页展示。</summary>
    public IReadOnlyList<PluginDescriptor> ListPlugins()
    {
        var result = new List<PluginDescriptor>();
        try
        {
            if (!Directory.Exists(PluginsDir)) return result;
            var pending = ReadPendingDeletes();
            foreach (var dll in Directory.EnumerateFiles(PluginsDir, "*.dll"))
            {
                if (pending.Contains(dll)) continue; // 已删除但文件被占用待清，不展示（防复活）
                var status = PluginHashManifest.GetStatus(dll, HashFile);
                lock (_gate)
                {
                    var loaded = _plugins.FirstOrDefault(p => p.FilePath == dll);
                    if (loaded is not null)
                    {
                        result.Add(loaded with { Status = status == PluginStatus.Tampered ? PluginStatus.Tampered : PluginStatus.Normal });
                        continue;
                    }
                }
                result.Add(new PluginDescriptor(Path.GetFileName(dll), dll, null, null, null, false,
                    status != PluginStatus.Disabled, status));
            }
        }
        catch { /* 目录异常不阻断列表 */ }
        return result;
    }

    /// <summary>导入插件：复制到 plugins/ + 登记基线哈希 + 立即加载（全局开关开时）。</summary>
    public PluginOpResult Import(string srcPath)
    {
        try
        {
            if (!File.Exists(srcPath)) return new PluginOpResult(false, "源文件不存在");
            if (!Path.GetExtension(srcPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                return new PluginOpResult(false, "只支持 .dll 插件文件");
            Directory.CreateDirectory(PluginsDir);
            var dest = Path.Combine(PluginsDir, Path.GetFileName(srcPath));
            if (File.Exists(dest))
            {
                if (!SameFile(dest, srcPath)) return new PluginOpResult(false, "plugins/ 已有同名文件且内容不同，请先删除再导入");
                return new PluginOpResult(false, "该插件已在 plugins/ 目录");
            }
            File.Copy(srcPath, dest);
            if (!PluginHashManifest.VerifyOrRecord(dest, HashFile))
                return new PluginOpResult(false, "插件登记失败"); // 正常不会发生
            if (Enabled) LoadIfDue(dest);
            AppLog.Instance?.LogInformation("[plugin] 已导入 {File}", Path.GetFileName(dest));
            return new PluginOpResult(true, null);
        }
        catch (Exception ex) { return new PluginOpResult(false, "导入失败：" + ex.Message); }
    }

    /// <summary>停用插件：写盘 enabled=false + 尝试立即卸载运行句柄。</summary>
    public PluginOpResult Disable(string dll)
    {
        var status = PluginHashManifest.GetStatus(dll, HashFile);
        if (status == PluginStatus.Tampered) return new PluginOpResult(false, "插件哈希与登记不一致（可能被掉包），已拒绝操作");
        PluginHashManifest.SetEnabled(dll, HashFile, false);
        var deferred = TryUnload(dll);
        return new PluginOpResult(true, deferred ? null : "已停用，重启后完全卸载", deferred);
    }

    /// <summary>启用插件：写盘 enabled=true + 立即加载（全局开关开时）。</summary>
    public PluginOpResult Enable(string dll)
    {
        var status = PluginHashManifest.GetStatus(dll, HashFile);
        if (status == PluginStatus.Tampered) return new PluginOpResult(false, "插件哈希与登记不一致（可能被掉包），已拒绝操作");
        PluginHashManifest.SetEnabled(dll, HashFile, true);
        if (Enabled)
        {
            try { LoadIfDue(dll); }
            catch (Exception ex) { return new PluginOpResult(true, "已启用，但本次加载失败：" + ex.Message); }
        }
        return new PluginOpResult(true, Enabled ? null : "已启用，打开总开关后生效");
    }

    /// <summary>删除插件：卸载运行句柄 + 删登记 + 删配置目录 + 删文件。</summary>
    public PluginOpResult Delete(string dll)
    {
        try
        {
            TryUnload(dll);
            string? id = null;
            lock (_gate)
            {
                var loaded = _plugins.FirstOrDefault(p => p.FilePath == dll);
                id = loaded?.Id;
                _plugins.RemoveAll(p => p.FilePath == dll);
            }
            PluginHashManifest.Remove(dll, HashFile);
            if (id is not null)
            {
                var settingsDir = Path.Combine(PluginsDir, id);
                try { if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, recursive: true); } catch { /* 配置目录占用不阻断删文件 */ }
            }
            // ALC 卸载是异步的：刚卸完文件可能仍被锁。轮询等待解锁（最多 ~2.5s）；
            // 仍锁 → 落墓碑，下次启动清除（插件被外部引用导致卸载推迟时兜底）。
            var deleted = TryDeleteWithPoll(dll, TimeSpan.FromSeconds(2.5));
            if (!deleted)
            {
                WritePendingDelete(dll);
                return new PluginOpResult(true, "插件已移除，文件被占用（重启后自动清除）", Deferred: true);
            }
            AppLog.Instance?.LogInformation("[plugin] 已删除 {File}", Path.GetFileName(dll));
            return new PluginOpResult(true, null);
        }
        catch (Exception ex) { return new PluginOpResult(false, "删除失败：" + ex.Message); }
    }

    // ---------- 内部 ----------

    private void LoadIfDue(string dll)
    {
        var status = PluginHashManifest.GetStatus(dll, HashFile);
        if (status == PluginStatus.Tampered)
        {
            AppLog.Instance?.LogWarning("[plugin] 跳过 {Name}：哈希与登记不一致（可能被掉包/投毒）", Path.GetFileName(dll));
            return;
        }
        if (status == PluginStatus.Disabled) return; // 停用跳过
        if (status == PluginStatus.Unknown && !PluginHashManifest.VerifyOrRecord(dll, HashFile))
        {
            AppLog.Instance?.LogWarning("[plugin] 跳过 {Name}：登记失败", Path.GetFileName(dll));
            return;
        }
        var loaded = PluginLoader.LoadOne(dll, PluginsDir, msg => AppLog.Instance?.LogInformation(msg));
        if (loaded is null) return; // 无插件实现（已卸载）
        lock (_gate)
        {
            _runtime[loaded.Plugin.Id] = loaded;
            _plugins.Add(new PluginDescriptor(Path.GetFileName(dll), dll, loaded.Plugin.Id, loaded.Plugin.Name,
                loaded.Plugin.Version, true, true, PluginStatus.Normal));
        }
        AppLog.Instance?.LogInformation("[plugin] 已加载 {Name} {Version}（{File}）", loaded.Plugin.Name, loaded.Plugin.Version, Path.GetFileName(dll));
    }

    /// <summary>尝试卸载某 dll 的运行句柄。返回 true=已卸载或本未加载；false=仍在运行（需重启）。</summary>
    private bool TryUnload(string dll)
    {
        PluginLoader.Loaded? runtime = null;
        lock (_gate)
        {
            var loaded = _plugins.FirstOrDefault(p => p.FilePath == dll);
            if (loaded is null) return true; // 本未加载 → 无需卸载
            if (!_runtime.Remove(loaded.Id, out runtime)) { _plugins.Remove(loaded); return true; }
            _plugins.Remove(loaded);
        }
        if (runtime is null) return true;
        runtime.Context.DisposeSubscriptions(); // 解除 AppEvents 订阅，放掉插件代码引用
        runtime.Alc.Unload();
        runtime = null; // 释放局部强引用，让 ALC 真正卸载（否则卸载被本方法栈上引用拖住，dll 文件仍被锁）
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // ALC.Unload 是异步的：若插件代码仍被外部引用（如非 AppEvents 的静态持有），卸载推迟到引用释放。
        // 状态已写盘，下次启动不加载；此处无法同步断言，返回 false 提示重启。
        return false;
    }

    private static bool SameFile(string a, string b)
    {
        try { return Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(a))) == Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(b))); }
        catch { return false; }
    }

    // ---------- 待删墓碑（文件被占用时"删除后重启清除"，防复活） ----------

    /// <summary>轮询删除：卸载后文件可能仍被锁（ALC 卸载异步），最多等 total 时长。</summary>
    private static bool TryDeleteWithPoll(string dll, TimeSpan total)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try { File.Delete(dll); return true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (sw.Elapsed >= total) return false;
            Thread.Sleep(150);
        }
    }

    private static void WritePendingDelete(string dll)
    {
        try
        {
            var entries = ReadPendingDeletes();
            entries.Add(Path.GetFullPath(dll));
            File.WriteAllLines(PendingDeleteFile, entries);
        }
        catch { }
    }

    private static void ClearPendingDelete(string dll)
    {
        try
        {
            var entries = ReadPendingDeletes();
            if (entries.Remove(Path.GetFullPath(dll))) File.WriteAllLines(PendingDeleteFile, entries);
        }
        catch { }
    }

    private static HashSet<string> ReadPendingDeletes()
    {
        var set = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            if (!File.Exists(PendingDeleteFile)) return set;
            foreach (var line in File.ReadAllLines(PendingDeleteFile))
                if (!string.IsNullOrWhiteSpace(line)) set.Add(Path.GetFullPath(line.Trim()));
        }
        catch { }
        return set;
    }

    /// <summary>启动时补删上次被占用的插件文件（load 前调用；仍失败则保留墓碑）。</summary>
    private static void CleanupPendingDeletes()
    {
        try
        {
            var entries = ReadPendingDeletes();
            if (entries.Count == 0) return;
            foreach (var path in entries.ToList())
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    ClearPendingDelete(path);
                }
                catch { /* 仍被占用 → 保留墓碑，下次启动再试 */ }
            }
        }
        catch { }
    }
}
