using System;
using System.Text;

namespace PCL.Core.Utils.Codecs;

public static class Encodings {
    // GB18030 在 .NET Core 默认不在内置编码表中（需 CodePagesEncodingProvider），
    // 且 Linux 上类加载即抛异常；改为惰性加载 + 注册 provider + UTF-8 兜底。
    private static readonly Lazy<Encoding> _LazyGB18030 = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030");
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    public static Encoding GB18030 => _LazyGB18030.Value;
}
