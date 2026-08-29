using System.IO.Compression;
using System.Text;
using Launcher.Core.Diagnostics;

namespace Launcher.App.Services;

/// <summary>日志导出共享工具：游戏日志 + 崩溃日志 + 系统信息 → zip</summary>
public static class LogExportHelper
{
    /// <summary>导出日志 zip（系统信息 + 游戏日志 + 崩溃日志 + 动态中文诊断说明）→ 路径；失败抛异常</summary>
    public static string ExportLogs(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var logDir = Path.Combine(Launcher.Core.Utils.AppPaths.DataRoot, "logs");
        var zipPath = Path.Combine(outDir, $"Starview-日志-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        // 1. 系统信息
        var sys = zip.CreateEntry("系统信息.txt");
        using (var sw = new StreamWriter(sys.Open(), new UTF8Encoding(false)))
            sw.Write(SystemInfo());

        // 2. 最近游戏日志 + 崩溃日志（同时收集内容供诊断）
        var collected = new List<string>();
        var diagText = new StringBuilder();
        if (Directory.Exists(logDir))
        {
            var launches = Directory.EnumerateFiles(logDir, "launch-*.log")
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc).Take(2).ToList();
            foreach (var f in launches)
            {
                zip.CreateEntryFromFile(f, $"日志/{Path.GetFileName(f)}");
                collected.Add($"日志/{Path.GetFileName(f)}");
                try { diagText.AppendLine(File.ReadAllText(f)); } catch { }
            }

            foreach (var f in Directory.EnumerateFiles(logDir, "crash-*.log")
                         .OrderByDescending(x => new FileInfo(x).LastWriteTimeUtc).Take(3))
            {
                zip.CreateEntryFromFile(f, $"崩溃/{Path.GetFileName(f)}");
                collected.Add($"崩溃/{Path.GetFileName(f)}");
                try { diagText.AppendLine(File.ReadAllText(f)); } catch { }
            }
        }

        // 3. 诊断说明（动态中文：按实际日志内容匹配已知错误模式，逐条说明 + 建议）
        var diag = LogDiagnostics.Diagnose(diagText.ToString());
        var diagEntry = zip.CreateEntry("诊断说明.txt");
        using (var sw = new StreamWriter(diagEntry.Open(), new UTF8Encoding(false)))
        {
            sw.WriteLine("----- Starview 日志诊断说明 -----");
            sw.WriteLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sw.WriteLine($"包含文件：{(collected.Count > 0 ? string.Join("、", collected) : "（无日志文件）")}");
            sw.WriteLine();
            if (diag.Count > 0)
            {
                sw.WriteLine("检测到这些疑似问题：");
                sw.WriteLine();
                foreach (var d in diag) sw.WriteLine(d);
            }
            else
            {
                sw.WriteLine("没匹配到已知错误。想排查的话可以给：");
                sw.WriteLine("· 当时的操作步骤");
                sw.WriteLine("· 控制台最后 20 行");
            }
        }

        return zipPath;
    }

    /// <summary>系统信息（OS/CPU/内存/目录）</summary>
    public static string SystemInfo()
        => "----- 系统信息 -----" + Environment.NewLine
           + $"系统：{Environment.OSVersion}" + Environment.NewLine
           + $"CPU：{Environment.ProcessorCount} 核" + Environment.NewLine
           + $"可用内存：{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024} MB" + Environment.NewLine
           + $"启动器：Starview" + Environment.NewLine
           + $"游戏目录：{Launcher.Core.Utils.GameDirectory.InstallDir()}" + Environment.NewLine;
}
