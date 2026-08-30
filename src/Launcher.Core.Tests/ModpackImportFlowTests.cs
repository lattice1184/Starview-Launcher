using Launcher.App.Services;

namespace Launcher.Core.Tests;

/// <summary>整合包导入流：确认弹窗大小格式化（8-31 修「整合包假大小」）。</summary>
public class ModpackImportFlowTests
{
    [Fact]
    public void FormatSize_MB()
        => Assert.Equal("550 MB", ModpackImportFlow.FormatSize(550L * 1024 * 1024));

    [Fact]
    public void FormatSize_KB()
        => Assert.Equal("512 KB", ModpackImportFlow.FormatSize(512L * 1024));

    [Fact]
    public void FormatSize_Bytes()
        => Assert.Equal("42 B", ModpackImportFlow.FormatSize(42));

    [Fact]
    public void FormatSize_Zero()
        => Assert.Equal("0 B", ModpackImportFlow.FormatSize(0));
}
