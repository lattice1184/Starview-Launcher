namespace Launcher.Core.Utils;

/// <summary>
/// 数字感知版本比较（更新检查用）。
/// 忽略前导 v/V 与 -suffix（1.1.4 &gt; 1.1.2，v1.1.4 == 1.1.4，1.2.0 &gt; 1.1.9）。
/// </summary>
public static class VersionUtil
{
    /// <summary>a &gt; b 返回正数；a &lt; b 返回负数；相等 0</summary>
    public static int Compare(string? a, string? b)
    {
        var ap = Parts(a);
        var bp = Parts(b);
        for (var i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            var x = i < ap.Length ? ap[i] : 0;
            var y = i < bp.Length ? bp[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>数字段列表（非数字段按 0 处理——suffix/alpha 后缀不影响主版本序）</summary>
    private static int[] Parts(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return [];
        var s = v.Trim().TrimStart('v', 'V');
        return s.Split(['.', '-']).Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }
}
