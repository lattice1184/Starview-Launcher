using Launcher.Core.Services;

namespace Launcher.Core.Tests;

/// <summary>8-24 模组中文名缓存：Apply（命中 → 英文（中文））/ Put（养缓存）/ 种子来源。
/// 静态缓存跨测试共享 → 每个用例 Clear() 隔离；种子由 ModAliasTable 提供（单独验证来源）。</summary>
public class ChineseNameCacheTests
{
    [Fact]
    public void Put_Then_Apply_ReturnsBilingual()
    {
        ChineseNameCache.Clear();
        ChineseNameCache.Put("mr:test-mod", "测试模组");
        Assert.Equal("Test Mod（测试模组）", ChineseNameCache.Apply("mr:test-mod", "Test Mod"));
    }

    [Fact]
    public void Apply_ChineseTitle_Unchanged()
    {
        // 中文搜索链路的标题已是中文（「钠 (Sodium)」）——不再叠后缀成「钠 (Sodium)（钠）」
        ChineseNameCache.Clear();
        ChineseNameCache.Put("mr:sodium", "钠");
        Assert.Equal("钠 (Sodium)", ChineseNameCache.Apply("mr:sodium", "钠 (Sodium)"));
    }

    [Fact]
    public void Apply_Miss_Unchanged()
    {
        ChineseNameCache.Clear();
        Assert.Equal("Unknown Mod", ChineseNameCache.Apply("mr:nonexistent", "Unknown Mod"));
    }

    [Fact]
    public void Apply_NullSlug_Unchanged()
    {
        ChineseNameCache.Clear();
        Assert.Equal("No Slug", ChineseNameCache.Apply("", "No Slug"));
    }

    [Fact]
    public void ModAliasTable_Seeds_Sodium()
    {
        // 种子来源验证：ModAliasTable 有「钠 → sodium」（ChineseNameCache 静态构造用 AllEntries 预填）
        var entries = ModAliasTable.AllEntries().ToList();
        var sodium = entries.FirstOrDefault(e => e.Slugs.Contains("sodium", StringComparer.OrdinalIgnoreCase));
        Assert.NotNull(sodium);
        Assert.Equal("钠", sodium.Chinese);
    }
}
