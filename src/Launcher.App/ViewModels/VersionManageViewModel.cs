using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.App.Services;
using Launcher.Core.Download;
using Launcher.Core.Model.Modrinth;
using Launcher.Core.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Launcher.Core.Utils;
using PCL.Core.Minecraft.Saves;

namespace Launcher.App.ViewModels;

/// <summary>
/// 版本管理（PCL2 式）：删除 / 备份 / 导出整合包 / 打开文件夹 / MOD 启停删除 / 存档列表。
/// 目录语义与启动一致：隔离 → versions/{id}；否则共享 .minecraft 根。
/// </summary>
public partial class VersionManageViewModel : ViewModelBase
{
    private readonly string _gameDir;
    private readonly string _versionId;
    private readonly Action _onDeleted;

    public ObservableCollection<ModItemVM> Mods { get; } = [];
    /// <summary>存档预览（前 10 条；展开后为全部——全量源见 _allSaves）</summary>
    public ObservableCollection<SaveItemVM> Saves { get; } = [];

    // 8-14 资源包 / 光影包（复用 ModItemVM：同为 zip/jar + .disabled 启停，启停/删除复用 ToggleMod/DeleteMod）
    // 8-26 内存真减：资源包/光影/存档三列表改「预览前 10 + 展开全部」——重度用户几百条不再全量建视觉树
    public ObservableCollection<ModItemVM> PacksPreview { get; } = [];
    public ObservableCollection<ModItemVM> ShaderPreview { get; } = [];

    // 全量源（List 只作 Take/计数，不建 ObservableCollection —— 列表常驻的条目才是内存大头）
    private List<ModItemVM> _allPacks = [];
    private List<ModItemVM> _allShaders = [];
    private List<SaveItemVM> _allSaves = [];

    public string PacksCountText => $"资源包（{_allPacks.Count}）";
    public string ShaderCountText => $"光影（{_allShaders.Count}）";

    private const int PreviewLimit = 10;

    /// <summary>MOD 预览（前 10 条；展开后为全部）</summary>
    public ObservableCollection<ModItemVM> ModsPreview { get; } = [];

    /// <summary>是否展开全部（MOD / 资源包 / 光影 / 存档 各一）</summary>
    [ObservableProperty]
    public partial bool ShowAllMods { get; set; }
    [ObservableProperty]
    public partial bool ShowAllPacks { get; set; }
    [ObservableProperty]
    public partial bool ShowAllShaders { get; set; }
    [ObservableProperty]
    public partial bool ShowAllSaves { get; set; }

    /// <summary>"展开全部（N）"文字（无更多时不显示）</summary>
    [ObservableProperty]
    public partial string ModsExpandText { get; set; } = "";
    [ObservableProperty]
    public partial string PacksExpandText { get; set; } = "";
    [ObservableProperty]
    public partial string ShaderExpandText { get; set; } = "";
    [ObservableProperty]
    public partial string SavesExpandText { get; set; } = "";

    public bool HasMoreMods => Mods.Count > PreviewLimit;
    public bool HasMorePacks => _allPacks.Count > PreviewLimit;
    public bool HasMoreShaders => _allShaders.Count > PreviewLimit;
    public bool HasMoreSaves => _allSaves.Count > PreviewLimit;

    [RelayCommand]
    private void ToggleShowAllMods()
    {
        ShowAllMods = !ShowAllMods;
        RefreshModsPreview();
    }
    [RelayCommand]
    private void ToggleShowAllPacks()
    {
        ShowAllPacks = !ShowAllPacks;
        RefreshListPreviews();
    }
    [RelayCommand]
    private void ToggleShowAllShaders()
    {
        ShowAllShaders = !ShowAllShaders;
        RefreshListPreviews();
    }
    [RelayCommand]
    private void ToggleShowAllSaves()
    {
        ShowAllSaves = !ShowAllSaves;
        RefreshListPreviews();
    }

    private void RefreshModsPreview()
    {
        ModsPreview.Clear();
        var shown = ShowAllMods ? Mods : Mods.Take(PreviewLimit);
        foreach (var m in shown) ModsPreview.Add(m);
        ModsExpandText = ShowAllMods ? "收起 ▴" : $"展开全部（{Mods.Count - PreviewLimit}）▾";
        OnPropertyChanged(nameof(HasMoreMods));
    }

    /// <summary>8-26 资源包/光影/存档三列表统一预览刷新（复用 ModsPreview 模式）</summary>
    private void RefreshListPreviews()
    {
        PacksPreview.Clear();
        foreach (var p in ShowAllPacks ? _allPacks : _allPacks.Take(PreviewLimit)) PacksPreview.Add(p);
        PacksExpandText = ShowAllPacks ? "收起 ▴" : $"展开全部（{_allPacks.Count - PreviewLimit}）▾";

        ShaderPreview.Clear();
        foreach (var s in ShowAllShaders ? _allShaders : _allShaders.Take(PreviewLimit)) ShaderPreview.Add(s);
        ShaderExpandText = ShowAllShaders ? "收起 ▴" : $"展开全部（{_allShaders.Count - PreviewLimit}）▾";

        Saves.Clear();
        foreach (var sv in ShowAllSaves ? _allSaves : _allSaves.Take(PreviewLimit)) Saves.Add(sv);
        SavesExpandText = ShowAllSaves ? "收起 ▴" : $"展开全部（{_allSaves.Count - PreviewLimit}）▾";

        OnPropertyChanged(nameof(PacksCountText));
        OnPropertyChanged(nameof(ShaderCountText));
        OnPropertyChanged(nameof(SavesCountText));
        OnPropertyChanged(nameof(HasMorePacks));
        OnPropertyChanged(nameof(HasMoreShaders));
        OnPropertyChanged(nameof(HasMoreSaves));
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>删除二次确认（点一次变"确认删除？"）</summary>
    [ObservableProperty]
    public partial bool IsConfirmDelete { get; set; }

    public string ModsCountText => $"MOD（{Mods.Count}）";
    public string SavesCountText => $"存档（{_allSaves.Count}）";

    /// <summary>版本根目录（隔离 → versions/{id}；否则共享根）——每次读设置，改隔离开关即时生效（不再构造快照）</summary>
    private string RootDir => LauncherSettings.Current.VersionIsolation
        ? Path.Combine(_gameDir, "versions", _versionId)
        : _gameDir;

    public VersionManageViewModel(string gameDir, string versionId, Action onDeleted)
    {
        _gameDir = gameDir;
        _versionId = versionId;
        _onDeleted = onDeleted;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = "加载中…";
        try
        {
            // 后台只收集 List；ObservableCollection 的写入统一在 UI 线程（避免跨线程集合异常）
            var (mods, saves, packs, shaders) = await Task.Run(async () => (
                CollectMods(), await CollectSavesAsync(),
                CollectPacks("resourcepacks"), CollectPacks("shaderpacks")));
            Mods.Clear();
            foreach (var m in mods) Mods.Add(m);
            _allSaves = saves;
            _allPacks = packs;
            _allShaders = shaders;
            OnPropertyChanged(nameof(ModsCountText));
            ShowAllMods = false;
            ShowAllPacks = ShowAllShaders = ShowAllSaves = false;
            RefreshModsPreview();
            RefreshListPreviews();
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---------- MOD ----------

    private List<ModItemVM> CollectMods()
    {
        var list = new List<ModItemVM>();
        var modsDir = Path.Combine(RootDir, "mods");
        if (!Directory.Exists(modsDir)) return list;
        foreach (var f in Directory.EnumerateFiles(modsDir, "*.jar*"))
        {
            var file = Path.GetFileName(f);
            var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            list.Add(new ModItemVM(f, disabled ? file[..^".disabled".Length] : file,
                new FileInfo(f).Length));
        }
        return list;
    }

    [RelayCommand]
    private async Task ToggleMod(ModItemVM mod)
    {
        // 8-19 二次确认（用户反馈）：启停/删除都是误触不可逆操作，先问再动
        var owner = DialogService.MainWindow();
        if (owner is null) return;
        var enabling = mod.Path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var name = Path.GetFileName(mod.Path);
        var ok = enabling
            ? await DialogService.Confirm(owner, $"启用 {name}？", "启用 MOD", "启用", "取消")
            : await DialogService.Confirm(owner, $"禁用 {name}？", "禁用 MOD", "禁用", "取消");
        if (!ok) return;
        try
        {
            var path = mod.Path;
            if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                File.Move(path, path[..^".disabled".Length]); // 启用：xxx.jar.disabled → xxx.jar
            else
                File.Move(path, path + ".disabled");           // 禁用：xxx.jar → xxx.jar.disabled
        }
        catch { }
        ReloadMods();
    }

    [RelayCommand]
    private async Task DeleteMod(ModItemVM mod)
    {
        var owner = DialogService.MainWindow();
        if (owner is null) return;
        if (!await DialogService.Confirm(owner, $"删除 {Path.GetFileName(mod.Path)}？此操作不可撤销。", "删除文件", "删除", "取消"))
            return;
        try { File.Delete(mod.Path); } catch { }
        ReloadMods();
    }

    /// <summary>启停/删除/外部安装后重扫 MOD（UI 线程收集+填充）。8-26 改 public：磁盘 watcher →
    /// SyncFromDisk 也调它，生态装完前置/主文件后版本页 mod 列表即时刷新。</summary>
    public void ReloadMods()
    {
        Mods.Clear();
        foreach (var m in CollectMods()) Mods.Add(m);
        OnPropertyChanged(nameof(ModsCountText));
        RefreshModsPreview();
    }

    [RelayCommand]
    private void OpenModsFolder() => OpenFolder(Path.Combine(RootDir, "mods"));

    // ---------- 资源包 / 光影包（复用 ModItemVM：zip/jar + .disabled 启停，ToggleMod/DeleteMod 通用） ----------

    private List<ModItemVM> CollectPacks(string dirName)
    {
        var list = new List<ModItemVM>();
        var dir = Path.Combine(RootDir, dirName);
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.jar*")
                 .Concat(Directory.EnumerateFiles(dir, "*.zip*")))
        {
            var file = Path.GetFileName(f);
            var disabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            list.Add(new ModItemVM(f, disabled ? file[..^".disabled".Length] : file, new FileInfo(f).Length));
        }
        return list;
    }

    [RelayCommand]
    private void OpenPacksFolder() => OpenFolder(Path.Combine(RootDir, "resourcepacks"));

    [RelayCommand]
    private void OpenShaderFolder() => OpenFolder(Path.Combine(RootDir, "shaderpacks"));

    // ---------- 存档（借用 PCL.Core SaveManager 解析 level.dat） ----------

    /// <summary>存档目录（读侧放宽）：隔离位存在读隔离位，否则回退共享目录——PCL 版本存档实际在 PCL/.minecraft/saves（PCL 不做隔离）；
    /// 写侧（导出/备份/删除）仍走 RootDir，防误改 PCL 共享数据</summary>
    private string SavesDir
    {
        get
        {
            var isolated = Path.Combine(RootDir, "saves");
            if (Directory.Exists(isolated)) return isolated;
            var shared = Path.Combine(_gameDir, "saves");
            return Directory.Exists(shared) ? shared : isolated;
        }
    }

    private async Task<List<SaveItemVM>> CollectSavesAsync()
    {
        var list = new List<SaveItemVM>();
        var savesDir = SavesDir;
        if (!Directory.Exists(savesDir)) return list;
        try
        {
            // AL51：存档扫描移出 UI 线程（原来 UI 线程同步 .GetResult() 会卡界面）
            var infos = await Task.Run(() =>
                new SaveManager().ScanSaveFoldersAsync(savesDir, CancellationToken.None).GetAwaiter().GetResult());
            foreach (var info in infos)
                list.Add(new SaveItemVM(info.LevelName, info.LastPlayedUtc, info.FolderPath));
        }
        catch { /* level.dat 解析失败不阻塞（有文件夹就显示） */ }
        if (list.Count == 0)
        {
            foreach (var dir in Directory.EnumerateDirectories(savesDir))
                list.Add(new SaveItemVM(Path.GetFileName(dir), DateTime.MinValue, dir));
        }
        return list;
    }

    [RelayCommand]
    private void OpenSaveFolder(SaveItemVM save) => OpenFolder(save.FolderPath);

    [RelayCommand]
    private void OpenSavesFolder() => OpenFolder(SavesDir);

    // ---------- 删除 / 备份 / 导出 / 打开 ----------

    [RelayCommand]
    private async Task Delete()
    {
        var owner = DialogService.MainWindow();
        if (owner is null) return;
        // 8-19 删除扩展（用户拍板）：其他来源的同 id 副本一并清理——此前只删选中行所在目录，
        // 同版本在多源（自建/PCL/官方）并存时删不干净，数据文件夹残留
        var others = GameDirectory.ScanSourceDirs()
            .Where(x => !string.Equals(Path.GetFullPath(x.Dir), Path.GetFullPath(_gameDir),
                StringComparison.OrdinalIgnoreCase))
            .Where(x => Directory.Exists(Path.Combine(x.Dir, "versions", _versionId)))
            .Select(x => x.Dir)
            .ToList();
        var suffix = others.Count > 0 ? $"（另有 {others.Count} 个其他来源的同名副本会一并删除）" : "";
        if (!await DialogService.Confirm(owner,
                $"确认删除版本「{_versionId}」？删除后无法恢复，建议先备份。{suffix}", "删除版本", "删除", "取消"))
        {
            return;
        }
        try
        {
            // 8-23 删时连带（用户确认）：删除前捕获父链——删后 json 没了无法再走 CleanupOrphanParents 的链，
            // 被加载器吸收的原版（.prefetched + 无其他引用）就清理不掉、留红字残件。删除成功后才连带清理，不误删。
            var parentChain = VersionInstaller.ParentChain(_gameDir, _versionId);
            var dir = Path.Combine(_gameDir, "versions", _versionId);
            var mainGone = true;
            if (Directory.Exists(dir))
            {
                // Windows 递归删除不原子：中途任一文件被占（游戏进程/杀毒扫描/索引器）会抛错，
                // 已删部分不恢复 → 留下 json-only 残件 → 版本页显示「缺文件」红字。
                // 策略：重试等短锁释放 → 仍失败则改名隔离（版本立刻消失）→ 后台续删。
                mainGone = await TryDeleteWithRetryAsync(dir);
                if (!mainGone)
                {
                    var quarantine = dir + $".deleting-{Guid.NewGuid():N}";
                    try
                    {
                        Directory.Move(dir, quarantine); // 目录内文件被独占占用时 rename 也失败 → 走 catch 报错
                        mainGone = true;
                    }
                    catch (Exception mvEx)
                    {
                        StatusText = $"删除失败：部分文件被占用。先停止该版本，再删除一次。{mvEx.Message}";
                        return;
                    }
                    _ = Task.Run(() => CleanupQuarantine(quarantine));
                    NotificationService.Info($"「{_versionId}」已移除，残留文件正在后台清理（不影响使用）");
                }
            }
            if (mainGone) VersionInstaller.CleanupParentsAfterDelete(_gameDir, parentChain);
            // 8-19 跨源副本：逐个删（主副本已删，个别失败静默——残留可走存储页手动清）
            foreach (var otherDir in others)
            {
                try
                {
                    var chain = VersionInstaller.ParentChain(otherDir, _versionId);
                    var d = Path.Combine(otherDir, "versions", _versionId);
                    var gone = !Directory.Exists(d);
                    if (!gone) gone = await TryDeleteWithRetryAsync(d);
                    if (gone) VersionInstaller.CleanupParentsAfterDelete(otherDir, chain);
                }
                catch { /* 跨源删除失败不阻断主流程 */ }
            }
            _onDeleted();
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败: {ex.Message}";
        }
    }

    /// <summary>删目录，短锁（Defender/索引器）重试 3 次：0.5s / 2s / 4.5s</summary>
    private static async Task<bool> TryDeleteWithRetryAsync(string dir)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(dir, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) return false;
                await Task.Delay(500 * (attempt + 1) * (attempt + 1));
            }
        }
        return false;
    }

    /// <summary>后台清理隔离目录：30s 内每 3s 重试一次（游戏结束/扫描器放锁后删净）；
    /// 仍失败就留 .deleting- 目录供手动处理——绝不把没删干净冒充成删除成功。</summary>
    private static void CleanupQuarantine(string dir)
    {
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch
            {
                Thread.Sleep(3000);
            }
        }
    }

    [RelayCommand]
    private async Task Backup()
    {
        IsBusy = true;
        StatusText = "备份中…";
        try
        {
            var backupsDir = Path.Combine(_gameDir, "backups");
            Directory.CreateDirectory(backupsDir);
            var zipPath = Path.Combine(backupsDir, $"{_versionId}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            // 8-22 全栈排查：ZipFile.CreateFromDirectory 会把 backups 目录（含正在写入的 zip 自身）
            // 包进归档 → zip 自包含损坏/体积爆炸。改手动遍历排除 backups（仅排除顶层备份目录）
            await Task.Run(() =>
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var f in Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(RootDir, f);
                    if (rel.StartsWith("backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    zip.CreateEntryFromFile(f, rel.Replace('\\', '/'), CompressionLevel.Optimal);
                }
            });
            StatusText = $"已备份 → {zipPath}";
        }
        catch (Exception ex)
        {
            StatusText = $"备份失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportModpack()
    {
        var settings = await ShowExportSettingsAsync();
        if (settings is null) return; // 取消
        IsBusy = true;
        StatusText = "导出中…";
        try
        {
            var staging = Path.Combine(Path.GetTempPath(), $"yanla-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                await Task.Run(() =>
                {
                    CopySelected(staging, settings);

                    // manifest.json（含包名/描述）
                    var manifest = new
                    {
                        name = settings.Name,
                        version = "1.0",
                        description = settings.Description,
                        mcVersion = ExtractMcVersion(_versionId),
                        loader = ExtractLoader(_versionId),
                        fileCount = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Count(),
                        sizeBytes = DirSize(staging),
                    };
                    File.WriteAllText(Path.Combine(staging, "manifest.json"),
                        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                });

                Directory.CreateDirectory(settings.OutputDir);
                var zipPath = Path.Combine(settings.OutputDir, $"{settings.Name}-整合包.zip");
                await Task.Run(() => ZipFile.CreateFromDirectory(staging, zipPath,
                    CompressionLevel.Optimal, includeBaseDirectory: false));
                StatusText = $"已导出 → {zipPath}";
                NotificationService.Success($"已导出整合包：{Path.GetFileName(zipPath)}");
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>导出设置对话框（PCL 式：勾选内容/位置/包名）；取消返回 null</summary>
    private async Task<ExportSettings?> ShowExportSettingsAsync()
    {
        var owner = DialogService.MainWindow();
        var defaultDir = Path.Combine(_gameDir, "downloads", "modpacks");
        return await Views.ExportDialogWindow.ShowAsync(owner, _versionId, defaultDir);
    }

    /// <summary>按勾选复制内容到 staging（PCL 式：只打包勾选部分）</summary>
    private void CopySelected(string staging, ExportSettings s)
    {
        if (s.IncludeMods) CopyIfExists(Path.Combine(RootDir, "mods"), Path.Combine(staging, "mods"));
        if (s.IncludeSaves) CopyIfExists(Path.Combine(RootDir, "saves"), Path.Combine(staging, "saves"));
        if (s.IncludeConfig) CopyIfExists(Path.Combine(RootDir, "config"), Path.Combine(staging, "config"));
        if (s.IncludeResourcepacks) CopyIfExists(Path.Combine(RootDir, "resourcepacks"), Path.Combine(staging, "resourcepacks"));
        if (s.IncludeShaders) CopyIfExists(Path.Combine(RootDir, "shaderpacks"), Path.Combine(staging, "shaderpacks"));
        if (s.IncludeOptions)
        {
            var options = Path.Combine(RootDir, "options.txt");
            if (File.Exists(options)) File.Copy(options, Path.Combine(staging, "options.txt"));
        }
    }

    private static void CopyIfExists(string src, string dest)
    {
        if (Directory.Exists(src)) CopyDir(src, dest);
    }

    /// <summary>导出整合包为 mrpack（Modrinth 标准，可被 PCL/HMCL 导入）</summary>
    [RelayCommand]
    private async Task ExportMrpack()
    {
        var settings = await ShowExportSettingsAsync();
        if (settings is null) return; // 取消
        IsBusy = true;
        StatusText = "导出中…";
        try
        {
            var options = new MrpackExporter.ExportOptions(
                settings.IncludeMods, settings.IncludeSaves, settings.IncludeConfig,
                settings.IncludeResourcepacks, settings.IncludeShaders, settings.IncludeOptions,
                settings.Name, settings.Description);
            var zipPath = await Task.Run(() => MrpackExporter.Export(RootDir, options, settings.OutputDir));
            StatusText = $"已导出 → {zipPath}";
            NotificationService.Success($"已导出 mrpack：{Path.GetFileName(zipPath)}");
        }
        catch (Exception ex)
        {
            StatusText = $"导出失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            var dir = Path.Combine(_gameDir, "versions", _versionId);
            if (!Directory.Exists(dir)) dir = _gameDir;
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败: {ex.Message}";
        }
    }

    // ---------- 工具 ----------

    private static void OpenFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static long DirSize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f =>
            new FileInfo(f).Length);

    /// <summary>从版本 id 提取 MC 版本（1.21.1-fabric-0.16.9 → 1.21.1；26.2 → 26.2）</summary>
    private static string ExtractMcVersion(string id)
    {
        var m = System.Text.RegularExpressions.Regex.Match(id, @"^\d+\.\d+(\.\d+)?");
        return m.Success ? m.Value : id;
    }

    private static string ExtractLoader(string id)
    {
        var lower = id.ToLowerInvariant();
        foreach (var (kw, name) in new[] { ("fabric", "fabric"), ("neoforge", "neoforge"),
                     ("forge", "forge"), ("quilt", "quilt") })
        {
            if (lower.Contains(kw)) return name;
        }
        return "vanilla";
    }
}

/// <summary>MOD 文件项（名称 / 路径 / 禁用态 / 大小 + 更新检查状态，可通知 UI 行内变化）</summary>
public sealed partial class ModItemVM : ObservableObject
{
    public string Path { get; }
    public string Name { get; }
    public long SizeBytes { get; }

    public ModItemVM(string path, string name, long sizeBytes)
    {
        Path = path;
        Name = name;
        SizeBytes = sizeBytes;
    }

    public string SizeText => SizeBytes >= 1024 * 1024 ? $"{SizeBytes / 1024.0 / 1024:0.0} MB" : $"{SizeBytes / 1024:0} KB";
}

/// <summary>存档项（世界名 / 最后游玩 / 路径，来自 PCL.Core SaveManager 解析 level.dat）</summary>
public sealed record SaveItemVM(string LevelName, DateTime LastPlayedUtc, string FolderPath)
{
    public string LastPlayedText => LastPlayedUtc.Year > 2000 ? $"最后游玩 {LastPlayedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" : "";
}
