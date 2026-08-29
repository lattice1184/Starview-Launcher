using System.Text;
using Launcher.Core.Services;

namespace Launcher.Core.Tests;

/// <summary>MC百科中文搜索链路解析（AL63）：搜索结果页条目 / 详情页双层 base64 解 slug / 中文判定</summary>
public class McmodSearchServiceTests
{
    [Fact]
    public void ParseSearchResults_ExtractsClassIdAndTitle()
    {
        // 真实页面结构（08-11 实测抓取格式）
        var html = """
            <div class="b"><a target="_blank" href="https://www.mcmod.cn/class/9090.html">遗落荒野 (Missing Wilds)</a></div>
            <div class="b"><a target="_blank" href="https://www.mcmod.cn/class/1234.html">钠 (Sodium)</a></div>
            """;
        var entries = McmodSearchService.ParseSearchResults(html);

        Assert.Equal(2, entries.Count);
        Assert.Equal("9090", entries[0].ClassId);
        Assert.Contains("遗落荒野", entries[0].Title);
        Assert.Equal("1234", entries[1].ClassId);
    }

    [Fact]
    public void ParseSearchResults_EmHighlightedTitle_NotSkipped()
    {
        // 8-22 修复回归：MC百科对命中词用 <em> 包裹——搜「钠」时 Sodium 本体标题为
        // `<em>钠</em> (Sodium)`，旧正则（首字符非 <）整条跳过 → 钠本体永不出现。
        // 真实抓取格式（08-16 实测 /s?key=钠 第 1 条即 2785）
        var html = """
            <div class="b"><a target="_blank" href="https://www.mcmod.cn/class/2785.html"><em>钠</em> (Sodium)</a></div>
            <div class="b"><a target="_blank" href="https://www.mcmod.cn/class/5608.html">铷 (Rubidium)</a></div>
            """;
        var entries = McmodSearchService.ParseSearchResults(html);

        Assert.Equal(2, entries.Count); // 钠本体不能被跳过
        Assert.Equal("2785", entries[0].ClassId);
        Assert.Equal("钠 (Sodium)", entries[0].Title); // <em> 已剥
    }

    [Theory]
    // 8-22 PCL 式别名直搜：中文 query 命中映射 → 直接精准 slug
    [InlineData("钠", "sodium")]
    [InlineData("钠 1.21", "sodium")]     // query 含别名键
    [InlineData("简单语音", "simple-voice-chat")]
    [InlineData("机械动", "create")]      // 8-30 关键词化：前缀少一字（原「机械动」不含完整键「机械动力」搜不到）
    [InlineData("动力", "create")]        // 8-30 关键词化：中缀少字（子串命中）
    [InlineData("没有这个词的模组", null)] // 无命中 → 空
    public void ModAliasTable_Resolve_HitsKnownSlugs(string query, string? expectedSlug)
    {
        var slugs = ModAliasTable.Resolve(query);
        if (expectedSlug is null)
            Assert.Empty(slugs);
        else
            Assert.Contains(expectedSlug, slugs);
    }

    [Fact]
    public void ModAliasTable_Resolve_ShortWordHitsRelated()
    {
        // 8-30 行为变化确认：子串匹配下「钠」同时命中 钠→sodium 与 钠扩展→sodium-extra
        var slugs = ModAliasTable.Resolve("钠");
        Assert.Contains("sodium", slugs);
        Assert.Contains("sodium-extra", slugs);
    }

    [Fact]
    public void ModAliasTable_Resolve_MultiWordUnion()
    {
        // 8-19 生态修缮：多词并集——「钠 锂」两词都命中（旧实现只中一个键，多词搜索丢一半）
        var slugs = ModAliasTable.Resolve("钠 锂");
        Assert.Contains("sodium", slugs);
        Assert.Contains("lithium", slugs);
    }

    [Fact]
    public void ModAliasTable_Resolve_SubstringKeepsSpecificSlug()
    {
        // 「钠扩展」仍必须含 sodium-extra（并集语义下 sodium 本体出现可接受——用户搜扩展看到本体无害）
        var slugs = ModAliasTable.Resolve("钠扩展");
        Assert.Contains("sodium-extra", slugs);
    }

    [Fact]
    public void DecodeModrinthSlug_ExtractsSlugFromBase64Link()
    {
        // 真实页面结构：href="//link.mcmod.cn/target/{base64(完整 URL)}"
        // base64("https://modrinth.com/mod/missing-wilds") = aHR0cHM6Ly9tb2RyaW50aC5jb20vbW9kL21pc3Npbmctd2lsZHM=
        var html = """
            <li><a data-toggle="tooltip" data-original-title="Modrinth" target="_blank" rel="nofollow noreferrer" target="_blank"
                href="//link.mcmod.cn/target/aHR0cHM6Ly9tb2RyaW50aC5jb20vbW9kL21pc3Npbmctd2lsZHM=">
                <svg class="common-mcicon common-linkicon common-linkicon-modrinth"></svg></a></li>
            """;

        var slug = McmodSearchService.DecodeModrinthSlug(html);

        Assert.Equal("missing-wilds", slug);
    }

    [Fact]
    public void DecodeModrinthSlug_NoModrinthLink_ReturnsNull()
    {
        // 只有 CurseForge 链接（base64("https://www.curseforge.com/minecraft/mc-mods/sodium")）
        var html = """
            <a data-original-title="CurseForge" href="//link.mcmod.cn/target/aHR0cHM6Ly93d3cuY3Vyc2Vmb3JnZS5jb20vbWluZWNyYWZ0L21jLW1vZHMvc29kaXVt">
            <svg class="common-linkicon-curseforge"></svg></a>
            """;
        Assert.Null(McmodSearchService.DecodeModrinthSlug(html));
    }

    [Theory]
    [InlineData("遗落荒野", true)]
    [InlineData("missing wilds", false)]
    [InlineData("  ", false)]
    [InlineData("钠", true)]
    public void ContainsChinese_DetectsCjk(string query, bool expected)
        => Assert.Equal(expected, McmodSearchService.ContainsChinese(query));

    [Fact]
    public void DecodeCurseforgeSlug_ExtractsSlugFromBase64Link()
    {
        // 8-24 CF 中文搜索：实抓 mcmod 详情页 2723.html 确认格式与 Modrinth 同构——
        // base64("https://www.curseforge.com/minecraft/mc-mods/pretty-beaches")
        var html = """
            <li><a data-toggle="tooltip" data-original-title="CurseForge" target="_blank" rel="nofollow noreferrer" target="_blank"
                href="//link.mcmod.cn/target/aHR0cHM6Ly93d3cuY3Vyc2Vmb3JnZS5jb20vbWluZWNyYWZ0L21jLW1vZHMvcHJldHR5LWJlYWNoZXM=">
                <svg class="common-linkicon-curseforge"></svg></a></li>
            """;
        Assert.Equal("pretty-beaches", McmodSearchService.DecodeCurseforgeSlug(html));
    }

    [Fact]
    public void DecodeCurseforgeSlug_NoCurseforgeLink_ReturnsNull()
    {
        // 只有 Modrinth 链接
        var html = """
            <a data-original-title="Modrinth" href="//link.mcmod.cn/target/aHR0cHM6Ly9tb2RyaW50aC5jb20vbW9kL3NvZGl1bQ==">
            <svg class="common-linkicon-modrinth"></svg></a>
            """;
        Assert.Null(McmodSearchService.DecodeCurseforgeSlug(html));
    }

    [Fact]
    public void DecodeCurseforgeSlug_TruncatesQueryString()
    {
        // 8-24 低风险 bug 修复：URL 带 ?/# 时 slug 只取到 query 前（防参数串进搜索词）
        var url = "https://www.curseforge.com/minecraft/mc-mods/sodium?page=2#section";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(url));
        var html = $@"<a data-original-title=""CurseForge"" href=""//link.mcmod.cn/target/{b64}""></a>";
        Assert.Equal("sodium", McmodSearchService.DecodeCurseforgeSlug(html));
    }
}
