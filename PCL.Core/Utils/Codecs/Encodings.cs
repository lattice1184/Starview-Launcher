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
            // 8-30 加固：RegisterProvider 重复注册抛 ArgumentException 被吞 → 走 UTF-8 兜底（中文乱码）。
            // 先试读：已注册直接拿；未注册才 Register（二次注册在别处已注册时仍可能抛，留给外层兜底）。
            try { return Encoding.GetEncoding("GB18030"); }
            catch (ArgumentException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            return Encoding.GetEncoding("GB18030");
        }
        catch
        {
            return Encoding.UTF8; // 平台无 GB18030 支持 → UTF-8 兜底
        }
    });

    public static Encoding GB18030 => _LazyGB18030.Value;
}
