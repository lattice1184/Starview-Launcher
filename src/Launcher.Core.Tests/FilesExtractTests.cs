using System.Diagnostics;
using PCL.Core.IO;

namespace Launcher.Core.Tests;

/// <summary>
/// 8-31 解压器回归：Linux 自动更新「更新后文件损坏打不开」根因是
/// _ExtractTarStreamAsync 两遍+Reset（SharpZipLib Reset 是 no-op → 内容错位）。
/// 用与真实发布同款方式打的 tar.gz，断言解压内容与原始完全一致（修复前必失败、修复后通过）。
/// </summary>
public class FilesExtractTests
{
    [Fact]
    public async Task ExtractTarGz_ContentsMatchOriginal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tar-test-{Guid.NewGuid():N}");
        try
        {
            var src = Path.Combine(dir, "src");      // 原始内容
            var outDir = Path.Combine(dir, "out");   // ExtractFileAsync 解压目标
            Directory.CreateDirectory(src);

            // 启动器包样式：ELF 魔数二进制（Launcher.App）+ native .so + 中文名 + 嵌套目录 + 空文件
            var elf = new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 0x02, 0x01, 0x01 }
                .Concat(Enumerable.Repeat((byte)0xAB, 5000)).ToArray();
            File.WriteAllBytes(Path.Combine(src, "Launcher.App"), elf);
            File.WriteAllText(Path.Combine(src, "libSkiaSharp.so"), "native-lib-content");
            File.WriteAllText(Path.Combine(src, "使用必看-Linux.txt"), "中文说明内容");
            Directory.CreateDirectory(Path.Combine(src, "locale", "zh-CN"));
            File.WriteAllText(Path.Combine(src, "locale", "zh-CN", "ui.json"), "{}");
            File.WriteAllText(Path.Combine(src, "empty.dll"), "");

            // 用系统 tar 打包（同真实发布 build-linux-osx 的 `tar czf -C publish .`，带 ./ 前缀）
            var tgz = Path.Combine(dir, "pack.tar.gz");
            using (var p = Process.Start(new ProcessStartInfo("tar")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                ArgumentList = { "czf", tgz, "-C", src, "." },
            })!)
            {
                await p.WaitForExitAsync();
                Assert.True(p.ExitCode == 0, p.StandardError.ReadToEnd());
            }
            Assert.True(File.Exists(tgz), "tar 打包失败");

            // ExtractFileAsync 解压
            await Files.ExtractFileAsync(tgz, outDir, null, CancellationToken.None);

            // 逐文件对比：修复前两遍+Reset 从包尾读 → 内容错位 → 必失败；修复后单遍 → 通过
            Assert.Equal(elf, File.ReadAllBytes(Path.Combine(outDir, "Launcher.App")));
            Assert.Equal("native-lib-content", File.ReadAllText(Path.Combine(outDir, "libSkiaSharp.so")));
            Assert.Equal("中文说明内容", File.ReadAllText(Path.Combine(outDir, "使用必看-Linux.txt")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(outDir, "locale", "zh-CN", "ui.json")));
            Assert.Equal(0, new FileInfo(Path.Combine(outDir, "empty.dll")).Length);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
