using Launcher.Core.Download;
using Launcher.Core.Utils;
using Xunit;

namespace Launcher.Core.Tests;

/// <summary>
/// 8-24 CurseForge 文件 CDN 竞速：edge.forgecdn.net 原单候选直连（4 个 mapper 都不认它），
/// CurseforgeCdnDlSourceMapper 在配置镜像前缀后映射为多候选，让 CF 文件进 AL32 并行竞速。
/// </summary>
public class CurseforgeCdnMapperTests
{
    [Fact]
    public void EdgeForgecdn_WithMirrorPrefix_ResolvesTwoCandidates()
    {
        var prev = LauncherSettings.Current.CurseForgeCdnPrefix;
        try
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = "https://mirror.example.com";
            var url = "https://edge.forgecdn.net/files/123/456/foo.jar";
            var mapper = new CurseforgeCdnDlSourceMapper();
            Assert.Equal("https://mirror.example.com/files/123/456/foo.jar", mapper.Map(url));
            // 接入 Default 链 → 官方 + 镜像两候选（进竞速）
            var resolved = ResolvingDlSourceMapper.Default.Resolve(url);
            Assert.Contains(url, resolved);
            Assert.Contains("https://mirror.example.com/files/123/456/foo.jar", resolved);
        }
        finally
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = prev;
        }
    }

    [Fact]
    public void EdgeForgecdn_NoMirror_SingleCandidate()
    {
        var prev = LauncherSettings.Current.CurseForgeCdnPrefix;
        try
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = "";
            var url = "https://edge.forgecdn.net/files/1/2/f.jar";
            var mapper = new CurseforgeCdnDlSourceMapper();
            Assert.Equal(url, mapper.Map(url));
            Assert.Single(ResolvingDlSourceMapper.Default.Resolve(url));
        }
        finally
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = prev;
        }
    }

    /// <summary>9-2 修：CF API 现返回 mediafilez.forgecdn.net（用户实测的 246MB 直链即此域）——
    /// 只认 edge 会让配了镜像前缀的用户对 CF 文件静默单候选直连。mediafilez/media 也映射。</summary>
    [Fact]
    public void MediafilezAndMediaHosts_WithMirrorPrefix_ResolveTwoCandidates()
    {
        var prev = LauncherSettings.Current.CurseForgeCdnPrefix;
        try
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = "https://mirror.example.com";
            var mapper = new CurseforgeCdnDlSourceMapper();
            Assert.Equal("https://mirror.example.com/files/8739/896/verity-6.0.0-beta.8.jar",
                mapper.Map("https://mediafilez.forgecdn.net/files/8739/896/verity-6.0.0-beta.8.jar"));
            Assert.Equal("https://mirror.example.com/files/1/2/m.jar",
                mapper.Map("https://media.forgecdn.net/files/1/2/m.jar"));
            // 接入 Default 链 → 官方 + 镜像两候选（进竞速）
            var resolved = ResolvingDlSourceMapper.Default.Resolve(
                "https://mediafilez.forgecdn.net/files/8739/896/verity-6.0.0-beta.8.jar");
            Assert.Contains("https://mediafilez.forgecdn.net/files/8739/896/verity-6.0.0-beta.8.jar", resolved);
            Assert.Contains("https://mirror.example.com/files/8739/896/verity-6.0.0-beta.8.jar", resolved);
        }
        finally
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = prev;
        }
    }

    [Fact]
    public void NonEdgeForge_Unchanged()
    {
        var prev = LauncherSettings.Current.CurseForgeCdnPrefix;
        try
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = "https://mirror.example.com";
            var url = "https://cdn.modrinth.com/data/x/f.jar";
            var mapper = new CurseforgeCdnDlSourceMapper();
            Assert.Equal(url, mapper.Map(url));
        }
        finally
        {
            LauncherSettings.Current.CurseForgeCdnPrefix = prev;
        }
    }
}
