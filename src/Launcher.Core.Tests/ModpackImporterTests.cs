using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Launcher.Core.Download;

namespace Launcher.Core.Tests;

/// <summary>整合包导入：自家 manifest.json 解析 / 解压隔离实例 / 安装标记 / mrpack 降级提示</summary>
public class ModpackImporterTests
{
    private static string MakeZip(string dir, string manifestJson, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, "pack.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var m = zip.CreateEntry("manifest.json");
            using (var sw = new StreamWriter(m.Open(), new UTF8Encoding(false))) // 无 BOM：条目长度 = 内容长度
                sw.Write(manifestJson);

            foreach (var (path, content) in files)
            {
                var e = zip.CreateEntry(path);
                using (var sw = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    sw.Write(content);
            }
        }
        return zipPath;
    }

    [Fact]
    public void Parse_OwnFormat_ReturnsInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            var zip = MakeZip(dir, """{"name":"整合测试","mcVersion":"1.21.1","loader":"fabric","fileCount":3}""");
            var info = ModpackImporter.Parse(zip, out var reason);
            Assert.True(info is not null, $"reason={reason}");
            Assert.Equal("整合测试", info!.VersionId);
            Assert.Equal("1.21.1", info.McVersion);
            Assert.Equal("fabric", info.Loader);
            Assert.Null(reason);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parse_Mrpack_ReturnsInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "pack.mrpack");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("modrinth.index.json");
                using var sw = new StreamWriter(e.Open());
                sw.Write("""{"formatVersion":1,"name":"MR测试","dependencies":{"minecraft":"1.21.1","fabric-loader":"*"},"files":[{"path":"mods/a.jar","hashes":{"sha1":"abc"},"downloads":["https://cdn.example/a.jar"],"fileSize":100},{"path":"mods/b.jar","downloads":["https://cdn.example/b.jar"],"env":{"client":"unsupported"}}]}""");
            }
            var info = ModpackImporter.Parse(zip, out var reason);
            Assert.True(info is not null, $"reason={reason}");
            Assert.Equal(ModpackFormat.Modrinth, info!.Format);
            Assert.Equal("MR测试", info.VersionId);
            Assert.Equal("1.21.1", info.McVersion);
            Assert.Equal("fabric", info.Loader);
            Assert.Null(info.LoaderVersion); // "*" → null（最新）
            Assert.Equal(2, info.MrpackFiles!.Count);
            Assert.True(info.MrpackFiles[1].ClientUnsupported);
            Assert.Equal("abc", info.MrpackFiles[0].Sha1);
            Assert.Null(reason);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parse_CurseForge_ReturnsInfo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            var zip = Path.Combine(dir, "cf.zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("manifest.json");
                using var sw = new StreamWriter(e.Open());
                sw.Write("""{"minecraft":{"version":"1.21.1","modLoaders":[{"id":"forge-47.1.0","primary":true}]},"manifestType":"minecraftModpack","manifestVersion":1,"name":"CF包","version":"1.0","files":[{"projectID":100,"fileID":200,"required":true}],"overrides":"overrides"}""");
            }
            var info = ModpackImporter.Parse(zip, out var reason);
            Assert.True(info is not null, $"reason={reason}");
            Assert.Equal(ModpackFormat.CurseForge, info!.Format);
            Assert.Equal("CF包", info.VersionId);
            Assert.Equal("1.21.1", info.McVersion);
            Assert.Equal("forge", info.Loader);
            Assert.Equal("47.1.0", info.LoaderVersion);
            Assert.Single(info.CurseForgeFiles!);
            Assert.Equal(100, info.CurseForgeFiles[0].ProjectId);
            Assert.Null(reason);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parse_CurseForge_Vs_Own_NoMisjudge()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            // 自家格式带 loader 字段但不含 minecraft 对象 → 必须判为 Own 而不是 CF
            var zip = MakeZip(dir, """{"name":"own","mcVersion":"1.21.1","loader":"forge","fileCount":3}""");
            var info = ModpackImporter.Parse(zip, out _);
            Assert.NotNull(info);
            Assert.Equal(ModpackFormat.Own, info!.Format);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseCfModLoader_Prefixes()
    {
        Assert.Equal((Launcher.Core.Model.Loader.LoaderKind.Forge, "47.1.0"), ModpackImporter.ParseCfModLoader("forge-47.1.0"));
        Assert.Equal((Launcher.Core.Model.Loader.LoaderKind.Fabric, "0.15.0"), ModpackImporter.ParseCfModLoader("fabricloader-0.15.0"));
        Assert.Equal((Launcher.Core.Model.Loader.LoaderKind.NeoForge, "21.1.0"), ModpackImporter.ParseCfModLoader("neoforge-21.1.0"));
        Assert.Null(ModpackImporter.ParseCfModLoader("unknown-1.0"));
    }

    [Fact]
    public void ResolvePackId_CollisionAppendsSuffix()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            var gameDir = Path.Combine(dir, "game");
            var vdir = Path.Combine(gameDir, "versions", "pack");
            Directory.CreateDirectory(vdir);
            Assert.Equal("pack (2)", ModpackImporter.ResolvePackId(gameDir, "pack"));
            // 与预取父版本撞名（包名叫 1.21.1）→ 必出后缀
            Directory.CreateDirectory(Path.Combine(gameDir, "versions", "1.21.1"));
            Assert.Equal("1.21.1 (2)", ModpackImporter.ResolvePackId(gameDir, "1.21.1"));
            Assert.Equal("fresh", ModpackImporter.ResolvePackId(gameDir, "fresh"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Parse_NoManifest_Unsupported()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            var zip = MakeZip(dir, """{"nope":1}""");
            // 替换：写一个不含 manifest.json 的 zip
            File.Delete(zip);
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var e = z.CreateEntry("mods/a.jar");
                using var sw = new StreamWriter(e.Open());
                sw.Write("x");
            }
            var info = ModpackImporter.Parse(zip, out var reason);
            Assert.Null(info);
            Assert.Contains("不支持", reason);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Import_ExtractsIsolatedInstance_AndMarks()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            var gameDir = Path.Combine(dir, "game");
            var zip = MakeZip(dir, """{"name":"pack-a","mcVersion":"1.21.1","loader":null,"fileCount":3}""",
                ("mods/example.jar", "JAR"),
                ("config/options.txt", "opt"),
                ("saves/世界/level.dat", "dat"));

            ModpackImporter.Import(zip, gameDir, CancellationToken.None);

            var vdir = Path.Combine(gameDir, "versions", "pack-a");
            Assert.True(File.Exists(Path.Combine(vdir, "mods", "example.jar")));
            Assert.True(File.Exists(Path.Combine(vdir, "config", "options.txt")));
            Assert.True(File.Exists(Path.Combine(vdir, "saves", "世界", "level.dat")));
            Assert.False(File.Exists(Path.Combine(vdir, "manifest.json"))); // 清单不入库
            Assert.True(InstallMarker.IsMarked(gameDir, "pack-a"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Import_PathTraversal_Blocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            var gameDir = Path.Combine(dir, "game");
            var zip = MakeZip(dir, """{"name":"pack-b"}""",
                ("../evil.txt", "hack"));

            ModpackImporter.Import(zip, gameDir, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(dir, "evil.txt"))); // 未逃出
            Assert.True(InstallMarker.IsMarked(gameDir, "pack-b"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExtractZipEntries_ReportsCumulativeBytes()
    {
        // 8-31 修「整合包假大小」：onBytes 回调累计已写字节（解压子任务报真实进度）
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        try
        {
            var zip = MakeZip(dir, "{}", ("mods/a.txt", "aaaa"), ("mods/b.txt", "bbbbbb"));
            using var z = ZipFile.OpenRead(zip);
            var reported = new List<long>();
            ModpackImporter.ExtractZipEntries(z, Path.Combine(dir, "out"),
                rel => rel.StartsWith("mods/", StringComparison.OrdinalIgnoreCase), null,
                CancellationToken.None, reported.Add);
            // a.txt=4 字节 → 4；b.txt=6 字节 → 累计 10
            Assert.Equal([4L, 10L], reported);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
