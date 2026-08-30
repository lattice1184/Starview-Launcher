using System.Text.Json;
using Launcher.Core.Plugin;

namespace Launcher.Core.Tests;

/// <summary>插件沙箱试运行（8-31）：TEMP 重定向吸收写入 + 崩溃分类 + 父进程报告合并。</summary>
public class PluginTrialTests : IDisposable
{
    private readonly string _scratch;
    private readonly string? _origTemp, _origTmp, _origTmpdir, _origCwd;

    public PluginTrialTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "plugin-trial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        _origTemp = Environment.GetEnvironmentVariable("TEMP");
        _origTmp = Environment.GetEnvironmentVariable("TMP");
        _origTmpdir = Environment.GetEnvironmentVariable("TMPDIR");
        _origCwd = Environment.CurrentDirectory;
    }

    public void Dispose()
    {
        // 恢复进程环境（PluginTrial.Run 会重定向 TEMP/CWD，测完必须还原，避免影响后续测试）
        Environment.SetEnvironmentVariable("TEMP", _origTemp);
        Environment.SetEnvironmentVariable("TMP", _origTmp);
        Environment.SetEnvironmentVariable("TMPDIR", _origTmpdir);
        try { Environment.CurrentDirectory = _origCwd ?? AppContext.BaseDirectory; } catch { }
        try { Directory.Delete(_scratch, true); } catch { }
    }

    private string ProbeDll => Path.Combine(AppContext.BaseDirectory, "TrialProbe.dll");
    private string ReportPath => Path.Combine(_scratch, "report.json");

    [Fact]
    public void Run_CapturesTempWriteIntoSandbox()
    {
        var code = PluginTrial.Run(ProbeDll, _scratch, ReportPath);
        Assert.Equal(0, code);
        var report = JsonSerializer.Deserialize<PluginTrial.TrialReport>(File.ReadAllText(ReportPath));
        Assert.NotNull(report);
        Assert.Equal("ok", report!.Status);
        Assert.Contains(report.ScratchWrites, p => p.EndsWith("trial-probe.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Run_ThrowingPlugin_ReportsException()
    {
        Environment.SetEnvironmentVariable("TRIALPROBE_THROW", "1");
        try
        {
            var code = PluginTrial.Run(ProbeDll, _scratch, ReportPath);
            Assert.NotEqual(0, code);
            var report = JsonSerializer.Deserialize<PluginTrial.TrialReport>(File.ReadAllText(ReportPath));
            Assert.NotNull(report);
            Assert.Equal("exception", report!.Status);
        }
        finally { Environment.SetEnvironmentVariable("TRIALPROBE_THROW", null); }
    }

    [Fact]
    public void Classify_MergesHostReportAndWatcher()
    {
        var ok = new PluginTrial.TrialReport("ok", [], []);
        Assert.Equal(PluginTrialStatus.Clean, PluginTrialRunner.Classify(false, ok, [], null).Status);
        Assert.Equal(PluginTrialStatus.WroteScratchOnly,
            PluginTrialRunner.Classify(false, ok with { ScratchWrites = ["a.txt"] }, [], null).Status);
        Assert.Equal(PluginTrialStatus.WroteOutside,
            PluginTrialRunner.Classify(false, ok, ["C:\\Users\\x\\Desktop\\evil.txt"], null).Status);
        Assert.Equal(PluginTrialStatus.TimedOut, PluginTrialRunner.Classify(true, null, [], null).Status);
        Assert.Equal(PluginTrialStatus.Crashed, PluginTrialRunner.Classify(false, null, [], null).Status);
        Assert.Equal(PluginTrialStatus.NotAPlugin,
            PluginTrialRunner.Classify(false, ok with { Status = "no-plugin" }, [], null).Status);
        Assert.Equal(PluginTrialStatus.Crashed,
            PluginTrialRunner.Classify(false, ok with { Status = "exception" }, [], null).Status);
    }
}
