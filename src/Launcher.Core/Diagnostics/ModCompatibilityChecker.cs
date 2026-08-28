using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Launcher.Core.Diagnostics;

/// <summary>
/// 启动前模组兼容检查（8-26）：扫实例 mods 目录每个 jar 的 fabric.mod.json 的 depends.minecraft，
/// 与游戏版本比对，发现明显不兼容的 mod 在启动前自动禁用（.jar → .jar.disabled）。
/// 解决「开始前不检查冲突模组」——不等 Fabric 崩溃，先查先禁用，用户可在版本页重新启用。
///
/// 匹配策略保守：只判「明显不匹配」（depends.minecraft 的目标 major.minor 与游戏 major.minor
/// 对不上，如声明 [1.21.x] 但游戏是 26.1.2）；解析不出 / 纯通配 / 范围有交集一律不误报。
/// </summary>
public static class ModCompatibilityChecker
{
    /// <summary>不兼容 mod 信息</summary>
    public sealed record IncompatibleMod(string Id, string FileName, string DeclaredRange, string GameVersion);

    /// <summary>会话级扫描缓存（8-27 加速）：键 = modsDir|gameVersion|jar 指纹。mods 未变则复用上次结果，
    /// 跳过 zip 读取（反复启动/调试秒过预检）。静态 = 本次运行内存；超过 32 条整体清空防累积。</summary>
    private static readonly ConcurrentDictionary<string, List<IncompatibleMod>> ScanCache = new();
    private const int CacheCap = 32;

    /// <summary>
    /// 扫 mods 目录（排除 .disabled），返回与游戏版本明显不兼容的 mod（只读，不禁用）。
    /// 8-27 加速：jar 间并行读取（zip 独立）+ 指纹缓存 + 可选进度回调（done/total，worker 线程调用，节流由调用方做）。
    /// </summary>
    public static List<IncompatibleMod> FindIncompatible(string modsDir, string gameVersion,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var game = ParseGameVersion(gameVersion);
        if (game is null || !Directory.Exists(modsDir)) return [];
        var jars = Directory.EnumerateFiles(modsDir, "*.jar")
            .Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (jars.Count == 0) return [];

        // 指纹缓存：目录未变 → 直接复用上次结果（跳过全部 zip 读取）
        var fingerprint = BuildFingerprint(jars);
        var key = $"{modsDir}{gameVersion}{fingerprint}";
        if (ScanCache.TryGetValue(key, out var cached)) return cached;

        var result = new List<IncompatibleMod>();
        var total = jars.Count;
        var done = 0;
        var sync = new object();
        var options = new ParallelOptions { CancellationToken = ct };
        Parallel.ForEach(jars, options, jar =>
        {
            ct.ThrowIfCancellationRequested(); // 用户跳过 → 提前中止扫描
            if (TryReadFabricMetadata(jar, out var id, out var depends)
                && !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(depends)
                && !RangeAllows(depends, game))
            {
                lock (sync) result.Add(new IncompatibleMod(id!, Path.GetFileName(jar), depends, gameVersion));
            }
            int d;
            lock (sync) { done++; d = done; }
            onProgress?.Invoke(d, total);
        });

        if (ScanCache.Count >= CacheCap) ScanCache.Clear();
        ScanCache[key] = result;
        return result;
    }

    /// <summary>jar 指纹（名称|长度|LastWriteTime，枚举时毫秒级）——目录未变即缓存命中</summary>
    private static string BuildFingerprint(List<string> jars)
    {
        var sb = new StringBuilder();
        foreach (var jar in jars)
        {
            try
            {
                var fi = new FileInfo(jar);
                sb.Append(Path.GetFileName(jar)).Append(':').Append(fi.Length).Append(':')
                  .Append(fi.LastWriteTimeUtc.Ticks).Append(';');
            }
            catch { sb.Append(Path.GetFileName(jar)).Append(';'); }
        }
        return sb.ToString();
    }

    /// <summary>扫 mods 目录并禁用不兼容 mod（.jar→.jar.disabled），返回实际禁用的列表。
    /// preScanned：已有 FindIncompatible 结果时传入，避免内部再扫一遍（8-27 去重扫）。</summary>
    public static List<IncompatibleMod> DisableIncompatible(string modsDir, string gameVersion,
        IReadOnlyList<IncompatibleMod>? preScanned = null, CancellationToken ct = default)
    {
        var disabled = new List<IncompatibleMod>();
        foreach (var m in preScanned ?? FindIncompatible(modsDir, gameVersion, ct: ct))
        {
            ct.ThrowIfCancellationRequested(); // 用户跳过 → 中止禁用
            try { File.Move(Path.Combine(modsDir, m.FileName), Path.Combine(modsDir, m.FileName + ".disabled")); disabled.Add(m); }
            catch { /* 单个文件禁用失败不阻断其他 */ }
        }
        return disabled;
    }

    /// <summary>非 .disabled 的 Fabric 模组 jar 数量（启动前检查报告用）。</summary>
    public static int CountMods(string modsDir)
    {
        if (!Directory.Exists(modsDir)) return 0;
        return Directory.EnumerateFiles(modsDir, "*.jar").Count(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>读 jar（zip）内 fabric.mod.json 的 id + depends.minecraft；非 Fabric 模组 / 无 id / 读失败返回 false。</summary>
    public static bool TryReadFabricMetadata(string jarPath, out string? id, out string? dependsMinecraft)
    {
        id = null; dependsMinecraft = null;
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry is null) return false; // 非 Fabric 模组（Forge 等）不参与
            using var reader = new StreamReader(entry.Open());
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl)) id = idEl.GetString();
            if (root.TryGetProperty("depends", out var depEl)
                && depEl.ValueKind == JsonValueKind.Object
                && depEl.TryGetProperty("minecraft", out var mcEl))
            {
                dependsMinecraft = mcEl.ValueKind switch
                {
                    JsonValueKind.String => mcEl.GetString(),
                    // 数组 = 备选列表（任一允许即兼容），逗号合并后走 RangeAllows 的 OR 语义
                    JsonValueKind.Array => string.Join(",", mcEl.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())),
                    _ => null,
                };
            }
            return id is not null; // 有 id 才算 Fabric 模组
        }
        catch { return false; }
    }

    /// <summary>depends.minecraft 声明是否允许游戏版本。保守：解析不出 / 无约束一律允许（不误报）。</summary>
    internal static bool RangeAllows(string declaredRange, int[] gameVersion)
    {
        var declared = declaredRange.Trim();
        if (declared.Length == 0 || declared == "*"
            || declared.Equals("any", StringComparison.OrdinalIgnoreCase)
            || declared.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return true;

        // 顶层逗号切 OR 备选（括号内逗号不切）：任一备选允许 → 兼容
        foreach (var alt in SplitTopLevel(declared, ','))
            if (AlternativeAllows(alt.Trim(), gameVersion)) return true;
        return false;
    }

    /// <summary>单个备选（区间 / 运算符条件 / 裸版本）是否允许游戏版本。</summary>
    private static bool AlternativeAllows(string alt, int[] game)
    {
        if (alt.Length == 0 || alt == "*") return true;

        // 区间 [a, b] / (a, b) / [a]（fabric 常见 [1.21.x]）——端点含 include 语义
        if ((alt[0] is '[' or '(') && (alt[^1] is ']' or ')'))
        {
            var lowerInc = alt[0] == '[';
            var upperInc = alt[^1] == ']';
            var bounds = SplitTopLevel(alt[1..^1], ',');
            if (bounds.Count == 1)
            {
                // 单值区间 [v] → 精确范围 >=v <=v
                var b = ParseVersionTokens(bounds[0].Trim());
                if (b is null) return true; // 解析不出不误报
                return CompareGameToBound(game, b, false) >= 0 && CompareGameToBound(game, b, true) <= 0;
            }
            if (bounds.Count == 2)
            {
                var lo = ParseVersionTokens(bounds[0].Trim());
                var hi = ParseVersionTokens(bounds[1].Trim());
                if (lo is null || hi is null) return true;
                var cLo = CompareGameToBound(game, lo, false);
                var cHi = CompareGameToBound(game, hi, true);
                return (lowerInc ? cLo >= 0 : cLo > 0) && (upperInc ? cHi <= 0 : cHi < 0);
            }
            return true; // 多端点不解析，不误报
        }

        // 运算符条件（空格切，AND）：>=1.20 <1.22 / ~1.21 / ^1.21
        foreach (var part in alt.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var (op, ver) = SplitOp(part);
            if (ver.Length == 0 || ver == "*") continue;
            if (op == "")
            {
                // 裸版本 / x 通配 → 精确 major.minor 匹配（保守：patch 差异不误报）
                var b = ParseVersionTokens(ver);
                if (b is null) return true;
                if (!MatchesExactMajorMinor(game, b)) return false;
                continue;
            }
            var bound = ParseVersionTokens(ver);
            if (bound is null) return true; // 含不可解析边界不误报
            // lower=true → 通配/缺失按 +∞（上界语义，用于 < <=）；false → 按 -∞（下界语义，用于 > >=）
            var cmp = CompareGameToBound(game, bound, lower: op is "<" or "<=");
            switch (op)
            {
                case ">=": if (cmp < 0) return false; break;
                case ">": if (cmp <= 0) return false; break;
                case "<=": if (cmp > 0) return false; break;
                case "<": if (cmp >= 0) return false; break;
                case "=": if (cmp != 0) return false; break;
                case "~" or "^": if (cmp < 0) return false; break; // 视为 >=（保守，不设上界）
            }
        }
        return true;
    }

    /// <summary>裸版本精确 major.minor 匹配：declared 1.21.x / 1.21 / 1.21.4 都视为目标 1.21 系；
    /// major 通配无法判定 → 保守 true。</summary>
    private static bool MatchesExactMajorMinor(int[] game, int?[] bound)
    {
        if (bound.Length >= 1 && bound[0] is null) return true;
        if (game.Length >= 1 && bound.Length >= 1 && game[0] != bound[0]) return false;
        if (bound.Length >= 2 && bound[1] is null) return true; // minor 通配 → major 相等即可
        if (game.Length >= 2 && bound.Length >= 2 && game[1] != bound[1]) return false;
        return true;
    }

    /// <summary>比较 game 与 bound；lower=true 时 bound 通配/缺失视为 +∞（上界），false 视为 -∞（下界）。返回 -1/0/1。</summary>
    private static int CompareGameToBound(int[] game, int?[] bound, bool lower)
    {
        var len = Math.Max(game.Length, bound.Length);
        for (var i = 0; i < len; i++)
        {
            var gv = i < game.Length ? game[i] : 0;
            var bv = i < bound.Length ? bound[i] : null;
            bv ??= lower ? int.MaxValue : int.MinValue;
            if (gv < bv) return -1;
            if (gv > bv) return 1;
        }
        return 0;
    }

    /// <summary>解析版本号为数值分量；通配 x / X / * 记为 null；含无法解析的字母返回 null。
    /// "26.1.2" → [26,1,2]；"1.21.x" → [1,21,null]；"25w06a" → null（快照版本不参与检查）。</summary>
    private static int?[]? ParseVersionTokens(string s)
    {
        s = s.Trim();
        var dash = s.IndexOf('-'); // 去掉 -suffix（如 1.21.4-rc1）
        if (dash > 0) s = s[..dash];
        if (s.Length == 0) return null;
        var parts = s.Split('.');
        var tokens = new int?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim();
            if (p.Length == 0) return null;
            if (p is "x" or "X" or "*") { tokens[i] = null; continue; }
            if (!int.TryParse(p, out var v)) return null;
            tokens[i] = v;
        }
        return tokens;
    }

    /// <summary>解析游戏版本为数值分量；快照号（25w06a）等含字母 → null（跳过检查，不误报）。</summary>
    internal static int[]? ParseGameVersion(string gameVersion)
    {
        var tokens = ParseVersionTokens(gameVersion);
        if (tokens is null || tokens.Any(t => t is null)) return null;
        return tokens.Select(t => t!.Value).ToArray();
    }

    /// <summary>按顶层分隔符切分（括号内分隔符不计），支持 [1.20.1, 1.21.x] 区间。</summary>
    private static List<string> SplitTopLevel(string s, char sep)
    {
        var result = new List<string>();
        var depth = 0;
        var cur = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '[' or '(') depth++;
            else if (c is ']' or ')') depth--;
            if (c == sep && depth == 0) { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }

    private static (string Op, string Ver) SplitOp(string part)
    {
        foreach (var op in new[] { ">=", "<=", ">", "<", "=", "~", "^" })
            if (part.StartsWith(op, StringComparison.Ordinal)) return (op, part[op.Length..].Trim());
        return ("", part.Trim());
    }
}
